import React, { useState, useEffect } from 'react';
import { Activity, RefreshCw, Loader2 } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';

interface SpeedStats {
  totalSessions: number;
  totalBytesSent: number;
  totalBytesReceived: number;
  totalBytesSentFormatted: string;
  totalBytesReceivedFormatted: string;
  avgDownloadSpeedBps: number;
  avgUploadSpeedBps: number;
  avgDownloadSpeedFormatted: string;
  avgUploadSpeedFormatted: string;
  correlatedCount: number;
  correlationPercent: number;
}

interface StreamSession {
  id: number;
  clientIp: string;
  sessionStartUtc: string;
  sessionEndUtc: string;
  protocol: string;
  status: number;
  bytesSent: number;
  bytesReceived: number;
  durationSeconds: number;
  upstreamHost: string;
  downloadId: number | null;
  datasource: string;
  downloadSpeedBps: number;
  uploadSpeedBps: number;
  downloadSpeedFormatted: string;
  uploadSpeedFormatted: string;
  // Game/service info from correlated download
  gameName: string | null;
  service: string | null;
  gameAppId: number | null;
  gameImageUrl: string | null;
}

interface StreamSessionsWidgetProps {
  glassmorphism?: boolean;
}

const StreamSessionsWidget: React.FC<StreamSessionsWidgetProps> = ({ glassmorphism = true }) => {
  const [stats, setStats] = useState<SpeedStats | null>(null);
  const [sessions, setSessions] = useState<StreamSession[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const loadData = async () => {
    setLoading(true);
    setError(null);
    try {
      const [statsData, sessionsData] = await Promise.all([
        ApiService.getStreamSpeedStats(),
        ApiService.getStreamSessions(1, 5)
      ]);
      setStats(statsData);
      setSessions(sessionsData.sessions || []);
    } catch (err) {
      console.error('Failed to load stream session data:', err);
      setError(err instanceof Error ? err.message : 'Failed to load data');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadData();
  }, []);

  const formatDuration = (seconds: number): string => {
    if (seconds < 60) return `${seconds.toFixed(1)}s`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${Math.floor(seconds % 60)}s`;
    return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
  };

  const formatTime = (utcString: string): string => {
    const date = new Date(utcString);
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  };

  return (
    <Card className={glassmorphism ? 'glass-card' : ''}>
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple">
            <Activity className="w-5 h-5 icon-purple" />
          </div>
          <div>
            <h3 className="text-lg font-semibold text-themed-primary">Stream Sessions</h3>
            <p className="text-xs text-themed-muted">Speed data from stream-access.log</p>
          </div>
        </div>
        <button
          onClick={loadData}
          disabled={loading}
          className="p-2 rounded-lg transition-colors disabled:opacity-50"
          style={{ color: 'var(--theme-text-muted)' }}
        >
          {loading ? (
            <Loader2 className="w-4 h-4 animate-spin" />
          ) : (
            <RefreshCw className="w-4 h-4" />
          )}
        </button>
      </div>

      {error ? (
        <div className="text-center py-4 text-red-500 text-sm">{error}</div>
      ) : loading ? (
        <div className="flex items-center justify-center py-8">
          <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
        </div>
      ) : (
        <div className="space-y-4">
          {/* Stats Summary */}
          {stats && stats.totalSessions > 0 ? (
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
              <div className="p-3 rounded-lg bg-themed-tertiary">
                <div className="text-xs text-themed-muted">Sessions</div>
                <div className="text-lg font-semibold text-themed-primary">
                  {stats.totalSessions.toLocaleString()}
                </div>
              </div>
              <div className="p-3 rounded-lg bg-themed-tertiary">
                <div className="text-xs text-themed-muted">Avg Download</div>
                <div className="text-lg font-semibold text-themed-primary">
                  {stats.avgDownloadSpeedFormatted}
                </div>
              </div>
              <div className="p-3 rounded-lg bg-themed-tertiary">
                <div className="text-xs text-themed-muted">Total Sent</div>
                <div className="text-lg font-semibold text-themed-primary">
                  {stats.totalBytesSentFormatted}
                </div>
              </div>
              <div className="p-3 rounded-lg bg-themed-tertiary">
                <div className="text-xs text-themed-muted">Correlated</div>
                <div className="text-lg font-semibold text-themed-primary">
                  {stats.correlationPercent.toFixed(1)}%
                </div>
              </div>
            </div>
          ) : (
            <div className="text-center py-4 text-themed-muted text-sm">
              No stream sessions found. Process stream-access.log to see speed data.
            </div>
          )}

          {/* Recent Sessions */}
          {sessions.length > 0 && (
            <div className="space-y-2">
              <div className="text-sm font-medium text-themed-secondary">Recent Sessions</div>
              <div className="space-y-2">
                {sessions.map((session) => (
                  <div
                    key={session.id}
                    className="flex items-center justify-between p-2 rounded-lg bg-themed-tertiary text-sm"
                  >
                    <div className="flex items-center gap-3 min-w-0">
                      {session.gameName ? (
                        <div className="text-themed-primary truncate max-w-[140px]" title={session.gameName}>
                          {session.gameName}
                        </div>
                      ) : session.service ? (
                        <div className="text-themed-secondary capitalize">
                          {session.service}
                        </div>
                      ) : (
                        <div className="text-themed-muted font-mono text-xs">
                          {session.clientIp}
                        </div>
                      )}
                      <div className="text-themed-primary font-medium">
                        {session.downloadSpeedFormatted}
                      </div>
                    </div>
                    <div className="flex items-center gap-3 text-xs text-themed-muted">
                      <span>{formatDuration(session.durationSeconds)}</span>
                      <span>{formatTime(session.sessionEndUtc)}</span>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </Card>
  );
};

export default StreamSessionsWidget;
