import React, { useState, useEffect, useMemo, memo, useCallback, useRef } from 'react';
import { Activity, Gauge, Users, Monitor, Loader2, ArrowDown, ArrowUp, Wifi, History, Zap, Clock } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import { useSignalR } from '@contexts/SignalRContext';
import { usePollingRate } from '@contexts/PollingRateContext';
import ApiService from '@services/api.service';
import type {
  DownloadSpeedSnapshot,
  GameSpeedInfo,
  ClientSpeedInfo,
  NetworkBandwidthSnapshot,
  SpeedHistorySnapshot,
  GameSpeedHistoryInfo,
  ClientSpeedHistoryInfo
} from '../../../../types';
import type { DownloadSpeedUpdatePayload, NetworkBandwidthUpdatePayload } from '@contexts/SignalRContext/types';

interface DownloadSpeedsWidgetProps {
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** Stagger index for entrance animation */
  staggerIndex?: number;
}

// Format speed to human-readable string
const formatSpeed = (bytesPerSecond: number): string => {
  if (bytesPerSecond === 0) return '0 B/s';
  if (bytesPerSecond < 0) return '0 B/s';
  const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
  const exp = Math.min(Math.floor(Math.log(bytesPerSecond) / Math.log(1024)), units.length - 1);
  const value = bytesPerSecond / Math.pow(1024, exp);
  return `${value.toFixed(1)} ${units[exp]}`;
};

// Format duration to human-readable string
const formatDuration = (seconds: number): string => {
  if (seconds < 60) return `${Math.round(seconds)}s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
  return `${(seconds / 3600).toFixed(1)}h`;
};

// Format relative time
const formatRelativeTime = (dateStr: string): string => {
  const now = new Date();
  const date = new Date(dateStr);
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);

  if (diffMins < 1) return 'just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffMins < 1440) return `${Math.floor(diffMins / 60)}h ago`;
  return `${Math.floor(diffMins / 1440)}d ago`;
};

/**
 * Widget showing real-time download speeds per game and client,
 * plus network interface bandwidth (upload/download)
 */
const DownloadSpeedsWidget: React.FC<DownloadSpeedsWidgetProps> = memo(({
  glassmorphism = true,
  staggerIndex,
}) => {
  const [gameSnapshot, setGameSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [networkSnapshot, setNetworkSnapshot] = useState<NetworkBandwidthSnapshot | null>(null);
  const [historySnapshot, setHistorySnapshot] = useState<SpeedHistorySnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [viewMode, setViewMode] = useState<'games' | 'clients'>('games');
  const [dataMode, setDataMode] = useState<'active' | 'history'>('active');
  const [historyMinutes, setHistoryMinutes] = useState(60);
  const signalR = useSignalR();
  const { pollingRate, getPollingInterval } = usePollingRate();
  const pollingIntervalRef = useRef<NodeJS.Timeout | null>(null);

  // Fetch speeds from API
  const fetchSpeeds = useCallback(async () => {
    try {
      const [gameData, networkData] = await Promise.all([
        ApiService.getCurrentSpeeds(),
        ApiService.getNetworkBandwidth()
      ]);
      setGameSnapshot(gameData);
      setNetworkSnapshot(networkData);
    } catch (error) {
      console.error('Failed to fetch speeds:', error);
    } finally {
      setLoading(false);
    }
  }, []);

  // Fetch history data
  const fetchHistory = useCallback(async () => {
    setHistoryLoading(true);
    try {
      const data = await ApiService.getSpeedHistory(historyMinutes);
      setHistorySnapshot(data);
    } catch (error) {
      console.error('Failed to fetch speed history:', error);
    } finally {
      setHistoryLoading(false);
    }
  }, [historyMinutes]);

  // Initial fetch
  useEffect(() => {
    fetchSpeeds();
  }, [fetchSpeeds]);

  // Fetch history when switching to history mode or changing time period
  useEffect(() => {
    if (dataMode === 'history') {
      fetchHistory();
    }
  }, [dataMode, historyMinutes, fetchHistory]);

  // Subscribe to SignalR updates (only in LIVE mode and active data mode)
  const handleSpeedUpdate = useCallback((payload: DownloadSpeedUpdatePayload) => {
    setGameSnapshot(payload as DownloadSpeedSnapshot);
  }, []);

  const handleNetworkUpdate = useCallback((payload: NetworkBandwidthUpdatePayload) => {
    setNetworkSnapshot(payload as NetworkBandwidthSnapshot);
  }, []);

  useEffect(() => {
    // Clear any existing polling interval
    if (pollingIntervalRef.current) {
      clearInterval(pollingIntervalRef.current);
      pollingIntervalRef.current = null;
    }

    if (dataMode === 'active') {
      if (pollingRate === 'LIVE') {
        // LIVE mode: Use SignalR for real-time updates
        signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);
        signalR.on('NetworkBandwidthUpdate', handleNetworkUpdate);

        return () => {
          signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
          signalR.off('NetworkBandwidthUpdate', handleNetworkUpdate);
        };
      } else {
        // Polling mode: Use API polling at configured interval
        const interval = getPollingInterval();
        pollingIntervalRef.current = setInterval(fetchSpeeds, interval);

        return () => {
          if (pollingIntervalRef.current) {
            clearInterval(pollingIntervalRef.current);
            pollingIntervalRef.current = null;
          }
        };
      }
    } else {
      // History mode: Poll for updates (use same interval, minimum 10s for history)
      const interval = Math.max(getPollingInterval(), 10000);
      pollingIntervalRef.current = setInterval(fetchHistory, interval);

      return () => {
        if (pollingIntervalRef.current) {
          clearInterval(pollingIntervalRef.current);
          pollingIntervalRef.current = null;
        }
      };
    }
  }, [signalR, pollingRate, getPollingInterval, handleSpeedUpdate, handleNetworkUpdate, fetchSpeeds, fetchHistory, dataMode]);

  // Get top 5 items based on view mode and data mode
  const topItems = useMemo(() => {
    if (dataMode === 'history') {
      if (!historySnapshot) return [];
      if (viewMode === 'games') {
        return historySnapshot.gameSpeeds.slice(0, 5);
      } else {
        return historySnapshot.clientSpeeds.slice(0, 5);
      }
    } else {
      if (!gameSnapshot) return [];
      if (viewMode === 'games') {
        return gameSnapshot.gameSpeeds.slice(0, 5);
      } else {
        return gameSnapshot.clientSpeeds.slice(0, 5);
      }
    }
  }, [gameSnapshot, historySnapshot, viewMode, dataMode]);

  // Build animation classes
  const animationClasses = staggerIndex !== undefined
    ? `animate-card-entrance stagger-${Math.min(staggerIndex + 1, 12)}`
    : '';

  // Loading state
  if (loading) {
    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
        <div className="flex items-center gap-2 mb-3">
          <Gauge className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Network & Download Speeds
          </h3>
        </div>
        <div className="flex items-center justify-center py-4">
          <Loader2 className="w-6 h-6 animate-spin" style={{ color: 'var(--theme-text-muted)' }} />
        </div>
      </div>
    );
  }

  const hasNetworkData = networkSnapshot?.isAvailable;
  const hasGameData = dataMode === 'active' ? gameSnapshot?.hasActiveDownloads : (historySnapshot?.totalSessions ?? 0) > 0;

  return (
    <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
      {/* Header */}
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2">
          <Gauge className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Network & Download Speeds
          </h3>
        </div>

        {/* Active/History Toggle */}
        <div className="flex items-center gap-1 p-0.5 rounded-lg" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
          <button
            onClick={() => setDataMode('active')}
            className="flex items-center gap-1 px-2 py-1 text-xs rounded-md transition-colors"
            style={{
              backgroundColor: dataMode === 'active' ? 'var(--theme-primary)' : 'transparent',
              color: dataMode === 'active' ? 'white' : 'var(--theme-text-muted)',
            }}
          >
            <Zap className="w-3 h-3" />
            Active
          </button>
          <button
            onClick={() => setDataMode('history')}
            className="flex items-center gap-1 px-2 py-1 text-xs rounded-md transition-colors"
            style={{
              backgroundColor: dataMode === 'history' ? 'var(--theme-primary)' : 'transparent',
              color: dataMode === 'history' ? 'white' : 'var(--theme-text-muted)',
            }}
          >
            <History className="w-3 h-3" />
            History
          </button>
        </div>
      </div>

      {/* Network Interface Bandwidth - only show in active mode */}
      {dataMode === 'active' && (
        <div
          className="grid grid-cols-2 gap-2 p-3 mb-3 rounded-lg"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          {/* Download (from internet) */}
          <div className="flex items-center gap-2">
            <div
              className="p-1.5 rounded-lg"
              style={{ backgroundColor: 'color-mix(in srgb, var(--theme-success) 20%, transparent)' }}
            >
              <ArrowDown className="w-4 h-4" style={{ color: 'var(--theme-success)' }} />
            </div>
            <div>
              <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>Download</div>
              <div className="text-sm font-bold" style={{ color: 'var(--theme-success)' }}>
                {hasNetworkData ? formatSpeed(networkSnapshot.downloadBytesPerSecond) : '--'}
              </div>
            </div>
          </div>

          {/* Upload (to clients) */}
          <div className="flex items-center gap-2">
            <div
              className="p-1.5 rounded-lg"
              style={{ backgroundColor: 'color-mix(in srgb, var(--theme-primary) 20%, transparent)' }}
            >
              <ArrowUp className="w-4 h-4" style={{ color: 'var(--theme-primary)' }} />
            </div>
            <div>
              <div className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>Upload</div>
              <div className="text-sm font-bold" style={{ color: 'var(--theme-primary)' }}>
                {hasNetworkData ? formatSpeed(networkSnapshot.uploadBytesPerSecond) : '--'}
              </div>
            </div>
          </div>

          {/* Interface name */}
          {hasNetworkData && (
            <div className="col-span-2 flex items-center gap-1 mt-1">
              <Wifi className="w-3 h-3" style={{ color: 'var(--theme-text-muted)' }} />
              <span className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
                {networkSnapshot.interfaceName}
              </span>
            </div>
          )}

          {!hasNetworkData && networkSnapshot?.errorMessage && (
            <div className="col-span-2 text-xs mt-1" style={{ color: 'var(--theme-warning)' }}>
              {networkSnapshot.errorMessage}
            </div>
          )}
        </div>
      )}

      {/* History time period selector */}
      {dataMode === 'history' && (
        <div className="flex items-center gap-2 mb-3 p-2 rounded-lg" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
          <Clock className="w-4 h-4" style={{ color: 'var(--theme-text-muted)' }} />
          <span className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>Period:</span>
          <div className="flex items-center gap-1">
            {[30, 60, 180, 360, 1440].map((mins) => (
              <button
                key={mins}
                onClick={() => setHistoryMinutes(mins)}
                className="px-2 py-0.5 text-xs rounded transition-colors"
                style={{
                  backgroundColor: historyMinutes === mins ? 'var(--theme-primary)' : 'transparent',
                  color: historyMinutes === mins ? 'white' : 'var(--theme-text-muted)',
                }}
              >
                {mins < 60 ? `${mins}m` : mins < 1440 ? `${mins / 60}h` : '24h'}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* History loading state */}
      {dataMode === 'history' && historyLoading && (
        <div className="flex items-center justify-center py-4">
          <Loader2 className="w-6 h-6 animate-spin" style={{ color: 'var(--theme-text-muted)' }} />
        </div>
      )}

      {/* Games/Clients toggle - show when we have data */}
      {hasGameData && !historyLoading && (
        <div className="flex items-center justify-center mb-2">
          <div className="flex items-center gap-1 p-0.5 rounded-lg" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
            <button
              onClick={() => setViewMode('games')}
              className="flex items-center gap-1 px-2 py-1 text-xs rounded-md transition-colors"
              style={{
                backgroundColor: viewMode === 'games' ? 'var(--theme-primary)' : 'transparent',
                color: viewMode === 'games' ? 'white' : 'var(--theme-text-muted)',
              }}
            >
              <Monitor className="w-3 h-3" />
              Games
            </button>
            <button
              onClick={() => setViewMode('clients')}
              className="flex items-center gap-1 px-2 py-1 text-xs rounded-md transition-colors"
              style={{
                backgroundColor: viewMode === 'clients' ? 'var(--theme-primary)' : 'transparent',
                color: viewMode === 'clients' ? 'white' : 'var(--theme-text-muted)',
              }}
            >
              <Users className="w-3 h-3" />
              Clients
            </button>
          </div>
        </div>
      )}

      {/* No data message */}
      {!hasGameData && !historyLoading && (
        <div className="flex flex-col items-center justify-center py-4 text-center">
          <Activity className="w-6 h-6 mb-2" style={{ color: 'var(--theme-text-muted)', opacity: 0.5 }} />
          <p className="text-xs" style={{ color: 'var(--theme-text-muted)' }}>
            {dataMode === 'active' ? 'No active game downloads' : 'No downloads in this period'}
          </p>
        </div>
      )}

      {/* Active mode content */}
      {dataMode === 'active' && hasGameData && (
        <>
          {/* Per-game total from logs */}
          <div
            className="flex items-center justify-between p-2 mb-2 rounded-lg"
            style={{ backgroundColor: 'color-mix(in srgb, var(--theme-primary) 10%, var(--theme-bg-secondary))' }}
          >
            <span className="text-xs font-medium" style={{ color: 'var(--theme-text-secondary)' }}>
              Game Traffic (from logs)
            </span>
            <span className="text-sm font-bold" style={{ color: 'var(--theme-primary)' }}>
              {formatSpeed(gameSnapshot?.totalBytesPerSecond || 0)}
            </span>
          </div>

          <div className="space-y-1.5">
            {viewMode === 'games' ? (
              // Game speeds
              (topItems as GameSpeedInfo[]).map((game, index) => (
                <div
                  key={game.depotId}
                  className="flex items-center gap-2 p-1.5 rounded-lg transition-colors"
                  style={{
                    backgroundColor: index === 0 ? 'color-mix(in srgb, var(--theme-success) 8%, var(--theme-bg-secondary))' : 'transparent',
                  }}
                >
                  {/* Game info */}
                  <div className="flex-1 min-w-0">
                    <div
                      className="text-xs font-medium truncate"
                      style={{ color: 'var(--theme-text-primary)' }}
                      title={game.gameName || `Depot ${game.depotId}`}
                    >
                      {game.gameName || `Depot ${game.depotId}`}
                    </div>
                    <div className="flex items-center gap-1.5 text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
                      <span>{formatBytes(game.totalBytes)}</span>
                      <span>•</span>
                      <span
                        style={{
                          color: game.cacheHitPercent >= 80
                            ? 'var(--theme-success)'
                            : game.cacheHitPercent >= 50
                              ? 'var(--theme-warning)'
                              : 'var(--theme-text-muted)',
                        }}
                      >
                        {game.cacheHitPercent.toFixed(0)}% hit
                      </span>
                    </div>
                  </div>

                  {/* Speed */}
                  <div
                    className="text-xs font-bold"
                    style={{ color: 'var(--theme-success)' }}
                  >
                    {formatSpeed(game.bytesPerSecond)}
                  </div>
                </div>
              ))
            ) : (
              // Client speeds
              (topItems as ClientSpeedInfo[]).map((client, index) => (
                <div
                  key={client.clientIp}
                  className="flex items-center gap-2 p-1.5 rounded-lg transition-colors"
                  style={{
                    backgroundColor: index === 0 ? 'color-mix(in srgb, var(--theme-success) 8%, var(--theme-bg-secondary))' : 'transparent',
                  }}
                >
                  {/* Client info */}
                  <div className="flex-1 min-w-0">
                    <div
                      className="text-xs font-medium truncate"
                      style={{ color: 'var(--theme-text-primary)' }}
                      title={client.clientIp}
                    >
                      {client.clientIp}
                    </div>
                    <div className="flex items-center gap-1.5 text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
                      <span>{formatBytes(client.totalBytes)}</span>
                      <span>•</span>
                      <span>{client.activeGames} game{client.activeGames !== 1 ? 's' : ''}</span>
                    </div>
                  </div>

                  {/* Speed */}
                  <div
                    className="text-xs font-bold"
                    style={{ color: 'var(--theme-success)' }}
                  >
                    {formatSpeed(client.bytesPerSecond)}
                  </div>
                </div>
              ))
            )}
          </div>

          {/* Footer with stats */}
          <div
            className="flex items-center justify-between mt-2 pt-2 text-[10px]"
            style={{
              borderTop: '1px solid var(--theme-border)',
              color: 'var(--theme-text-muted)',
            }}
          >
            <span>{gameSnapshot?.gameSpeeds.length || 0} game{(gameSnapshot?.gameSpeeds.length || 0) !== 1 ? 's' : ''}</span>
            <span>{gameSnapshot?.clientSpeeds.length || 0} client{(gameSnapshot?.clientSpeeds.length || 0) !== 1 ? 's' : ''}</span>
          </div>
        </>
      )}

      {/* History mode content */}
      {dataMode === 'history' && hasGameData && !historyLoading && historySnapshot && (
        <>
          {/* Period summary */}
          <div
            className="flex items-center justify-between p-2 mb-2 rounded-lg"
            style={{ backgroundColor: 'color-mix(in srgb, var(--theme-primary) 10%, var(--theme-bg-secondary))' }}
          >
            <span className="text-xs font-medium" style={{ color: 'var(--theme-text-secondary)' }}>
              Avg Speed (last {historyMinutes < 60 ? `${historyMinutes}m` : historyMinutes < 1440 ? `${historyMinutes / 60}h` : '24h'})
            </span>
            <span className="text-sm font-bold" style={{ color: 'var(--theme-primary)' }}>
              {formatSpeed(historySnapshot.averageBytesPerSecond)}
            </span>
          </div>

          <div className="space-y-1.5">
            {viewMode === 'games' ? (
              // Historical game speeds
              (topItems as GameSpeedHistoryInfo[]).map((game, index) => (
                <div
                  key={`${game.gameAppId || 'unknown'}-${game.service}-${index}`}
                  className="flex items-center gap-2 p-1.5 rounded-lg transition-colors"
                  style={{
                    backgroundColor: index === 0 ? 'color-mix(in srgb, var(--theme-success) 8%, var(--theme-bg-secondary))' : 'transparent',
                  }}
                >
                  {/* Game image */}
                  {game.gameImageUrl && (
                    <img
                      src={game.gameImageUrl}
                      alt=""
                      className="w-8 h-8 rounded object-cover"
                      onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
                    />
                  )}

                  {/* Game info */}
                  <div className="flex-1 min-w-0">
                    <div
                      className="text-xs font-medium truncate"
                      style={{ color: 'var(--theme-text-primary)' }}
                      title={game.gameName || `Game ${game.gameAppId || 'Unknown'}`}
                    >
                      {game.gameName || `Game ${game.gameAppId || 'Unknown'}`}
                    </div>
                    <div className="flex items-center gap-1.5 text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
                      <span>{formatBytes(game.totalBytes)}</span>
                      <span>•</span>
                      <span
                        style={{
                          color: game.cacheHitPercent >= 80
                            ? 'var(--theme-success)'
                            : game.cacheHitPercent >= 50
                              ? 'var(--theme-warning)'
                              : 'var(--theme-text-muted)',
                        }}
                      >
                        {game.cacheHitPercent.toFixed(0)}% hit
                      </span>
                      <span>•</span>
                      <span>{formatDuration(game.totalDurationSeconds)}</span>
                    </div>
                  </div>

                  {/* Average speed */}
                  <div className="text-right">
                    <div
                      className="text-xs font-bold"
                      style={{ color: 'var(--theme-success)' }}
                    >
                      {formatSpeed(game.averageBytesPerSecond)}
                    </div>
                    <div className="text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
                      {formatRelativeTime(game.lastSeenUtc)}
                    </div>
                  </div>
                </div>
              ))
            ) : (
              // Historical client speeds
              (topItems as ClientSpeedHistoryInfo[]).map((client, index) => (
                <div
                  key={client.clientIp}
                  className="flex items-center gap-2 p-1.5 rounded-lg transition-colors"
                  style={{
                    backgroundColor: index === 0 ? 'color-mix(in srgb, var(--theme-success) 8%, var(--theme-bg-secondary))' : 'transparent',
                  }}
                >
                  {/* Client info */}
                  <div className="flex-1 min-w-0">
                    <div
                      className="text-xs font-medium truncate"
                      style={{ color: 'var(--theme-text-primary)' }}
                      title={client.clientIp}
                    >
                      {client.clientIp}
                    </div>
                    <div className="flex items-center gap-1.5 text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
                      <span>{formatBytes(client.totalBytes)}</span>
                      <span>•</span>
                      <span>{client.gamesDownloaded} item{client.gamesDownloaded !== 1 ? 's' : ''}</span>
                      <span>•</span>
                      <span>{client.sessionCount} session{client.sessionCount !== 1 ? 's' : ''}</span>
                    </div>
                  </div>

                  {/* Average speed */}
                  <div className="text-right">
                    <div
                      className="text-xs font-bold"
                      style={{ color: 'var(--theme-success)' }}
                    >
                      {formatSpeed(client.averageBytesPerSecond)}
                    </div>
                    <div className="text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
                      {formatRelativeTime(client.lastSeenUtc)}
                    </div>
                  </div>
                </div>
              ))
            )}
          </div>

          {/* Footer with stats */}
          <div
            className="flex items-center justify-between mt-2 pt-2 text-[10px]"
            style={{
              borderTop: '1px solid var(--theme-border)',
              color: 'var(--theme-text-muted)',
            }}
          >
            <span>{historySnapshot.gameSpeeds.length} item{historySnapshot.gameSpeeds.length !== 1 ? 's' : ''}</span>
            <span>{historySnapshot.totalSessions} session{historySnapshot.totalSessions !== 1 ? 's' : ''}</span>
            <span>{formatBytes(historySnapshot.totalBytes)} total</span>
          </div>
        </>
      )}
    </div>
  );
});

DownloadSpeedsWidget.displayName = 'DownloadSpeedsWidget';

export default DownloadSpeedsWidget;
