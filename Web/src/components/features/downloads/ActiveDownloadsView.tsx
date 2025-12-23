import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Activity, HardDrive, Users, Loader2, RefreshCw } from 'lucide-react';
import { useSignalR } from '@contexts/SignalRContext';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import ApiService from '@services/api.service';
import { formatBytes } from '@utils/formatters';
import type { DownloadSpeedSnapshot, GameSpeedInfo, ClientSpeedInfo } from '../../../types';

// Format speed
const formatSpeed = (bytesPerSecond: number): string => {
  if (bytesPerSecond === 0) return '0 B/s';
  if (bytesPerSecond < 1024) return `${bytesPerSecond.toFixed(0)} B/s`;
  if (bytesPerSecond < 1024 * 1024) return `${(bytesPerSecond / 1024).toFixed(1)} KB/s`;
  if (bytesPerSecond < 1024 * 1024 * 1024) return `${(bytesPerSecond / (1024 * 1024)).toFixed(1)} MB/s`;
  return `${(bytesPerSecond / (1024 * 1024 * 1024)).toFixed(2)} GB/s`;
};

const ActiveDownloadsView: React.FC = () => {
  const signalR = useSignalR();
  const { getRefreshInterval } = useRefreshRate();

  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [viewMode, setViewMode] = useState<'games' | 'clients'>('games');
  const lastUpdateRef = useRef<number>(0);
  const pendingUpdateRef = useRef<NodeJS.Timeout | null>(null);
  const lastActiveCountRef = useRef<number | null>(null);

  // Fetch current speeds (for initial load, manual refresh, and visibility change)
  const fetchSpeeds = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      setSpeedSnapshot(data);
      lastActiveCountRef.current = data?.gameSpeeds?.length ?? 0;
    } catch (err) {
      console.error('Failed to fetch speeds:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  // SignalR setup with user-controlled throttling
  useEffect(() => {
    // Initial fetch
    fetchSpeeds();

    // SignalR handler with debouncing and throttling
    const handleSpeedUpdate = (payload: DownloadSpeedSnapshot) => {
      // Clear any pending update
      if (pendingUpdateRef.current) {
        clearTimeout(pendingUpdateRef.current);
      }

      const newCount = payload.gameSpeeds?.length ?? 0;

      // ALWAYS accept updates immediately when active games count changes
      // This ensures "download finished" events are never throttled
      const countChanged = lastActiveCountRef.current !== null &&
        lastActiveCountRef.current !== newCount;

      if (countChanged) {
        lastUpdateRef.current = Date.now();
        lastActiveCountRef.current = newCount;
        setSpeedSnapshot(payload);
        setLoading(false);
        return;
      }

      // Debounce: wait 100ms for more events
      pendingUpdateRef.current = setTimeout(() => {
        const maxRefreshRate = getRefreshInterval();
        const now = Date.now();
        const timeSinceLastUpdate = now - lastUpdateRef.current;

        // User's setting controls max refresh rate
        // LIVE mode (0) = minimum 500ms to prevent UI thrashing
        const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

        if (timeSinceLastUpdate >= minInterval) {
          lastUpdateRef.current = now;
          lastActiveCountRef.current = newCount;
          setSpeedSnapshot(payload);
          setLoading(false);
        }
        pendingUpdateRef.current = null;
      }, 100);
    };

    signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);

    return () => {
      signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
      if (pendingUpdateRef.current) {
        clearTimeout(pendingUpdateRef.current);
      }
    };
  }, [signalR, getRefreshInterval, fetchSpeeds]);

  // Refresh speeds when tab becomes visible
  useEffect(() => {
    const handleVisibilityChange = () => {
      if (!document.hidden) {
        // Reset throttle and fetch fresh data
        lastUpdateRef.current = 0;
        fetchSpeeds();
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [fetchSpeeds]);

  // Use speedSnapshot for all active download data (real-time from Rust speed tracker)
  const hasActiveDownloads = speedSnapshot?.hasActiveDownloads || false;
  const games = speedSnapshot?.gameSpeeds || [];
  const clients = speedSnapshot?.clientSpeeds || [];

  if (loading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="w-8 h-8 animate-spin" style={{ color: 'var(--theme-text-muted)' }} />
      </div>
    );
  }

  if (!hasActiveDownloads) {
    return (
      <div className="active-empty-state">
        <style>{`
          .active-empty-state {
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            padding: 4rem 2rem;
            text-align: center;
          }

          .empty-icon-container {
            position: relative;
            width: 80px;
            height: 80px;
            margin-bottom: 1.5rem;
          }

          .empty-icon-ring {
            position: absolute;
            inset: 0;
            border-radius: 50%;
            border: 2px dashed var(--theme-border-secondary);
            animation: rotate-slow 20s linear infinite;
          }

          @keyframes rotate-slow {
            from { transform: rotate(0deg); }
            to { transform: rotate(360deg); }
          }

          .empty-icon {
            position: absolute;
            inset: 8px;
            display: flex;
            align-items: center;
            justify-content: center;
            border-radius: 50%;
            background: var(--theme-bg-tertiary);
          }

          .empty-title {
            font-size: 1.125rem;
            font-weight: 600;
            color: var(--theme-text-primary);
            margin-bottom: 0.5rem;
          }

          .empty-description {
            font-size: 0.875rem;
            color: var(--theme-text-muted);
            max-width: 300px;
          }
        `}</style>

        <div className="empty-icon-container">
          <div className="empty-icon-ring" />
          <div className="empty-icon">
            <Activity size={32} style={{ color: 'var(--theme-text-muted)' }} />
          </div>
        </div>
        <div className="empty-title">No Active Downloads</div>
        <div className="empty-description">
          Downloads will appear here in real-time when clients start downloading games through the cache.
        </div>
      </div>
    );
  }

  return (
    <div className="active-downloads-view">
      <style>{`
        .active-downloads-view {
          display: flex;
          flex-direction: column;
          gap: 1rem;
        }

        .view-toggle-row {
          display: flex;
          align-items: center;
          justify-content: space-between;
          padding: 0 0.25rem;
        }

        .view-toggle {
          display: flex;
          padding: 3px;
          border-radius: 10px;
          background: var(--theme-bg-tertiary);
          border: 1px solid var(--theme-border-secondary);
        }

        .view-toggle-btn {
          display: flex;
          align-items: center;
          gap: 0.4rem;
          padding: 0.4rem 0.75rem;
          font-size: 0.75rem;
          font-weight: 600;
          color: var(--theme-text-muted);
          background: transparent;
          border: none;
          border-radius: 7px;
          cursor: pointer;
          transition: all 0.2s ease;
        }

        .view-toggle-btn:hover:not(.active) {
          color: var(--theme-text-secondary);
          background: color-mix(in srgb, var(--theme-bg-secondary) 50%, transparent);
        }

        .view-toggle-btn.active {
          color: var(--theme-button-text);
          background: var(--theme-primary);
          box-shadow: 0 2px 4px color-mix(in srgb, var(--theme-primary) 25%, transparent);
        }

        .view-toggle-btn svg {
          width: 14px;
          height: 14px;
        }

        .refresh-btn {
          display: flex;
          align-items: center;
          gap: 0.35rem;
          padding: 0.4rem 0.6rem;
          font-size: 0.7rem;
          font-weight: 500;
          color: var(--theme-text-muted);
          background: var(--theme-bg-tertiary);
          border: 1px solid var(--theme-border-secondary);
          border-radius: 6px;
          cursor: pointer;
          transition: all 0.2s ease;
        }

        .refresh-btn:hover {
          color: var(--theme-text-primary);
          background: var(--theme-bg-secondary);
          border-color: var(--theme-border-primary);
        }

        .refresh-btn svg {
          width: 12px;
          height: 12px;
        }

        .downloads-list {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }

        .download-item {
          display: flex;
          align-items: center;
          gap: 1rem;
          padding: 1rem;
          border-radius: 12px;
          background: var(--theme-bg-secondary);
          border: 1px solid var(--theme-border-secondary);
          transition: all 0.2s ease;
        }

        .download-item:hover {
          border-color: var(--theme-border-primary);
          box-shadow: 0 2px 8px color-mix(in srgb, black 8%, transparent);
        }

        .download-item.top {
          background: linear-gradient(
            135deg,
            color-mix(in srgb, var(--theme-success) 8%, var(--theme-bg-secondary)) 0%,
            var(--theme-bg-secondary) 100%
          );
          border-color: color-mix(in srgb, var(--theme-success) 30%, var(--theme-border-secondary));
        }

        .download-avatar {
          position: relative;
          width: 48px;
          height: 48px;
          border-radius: 10px;
          overflow: hidden;
          flex-shrink: 0;
          background: var(--theme-bg-tertiary);
          display: flex;
          align-items: center;
          justify-content: center;
        }

        .download-avatar img {
          width: 100%;
          height: 100%;
          object-fit: cover;
        }

        .download-avatar .fallback-icon {
          color: var(--theme-text-muted);
        }

        .active-indicator {
          position: absolute;
          bottom: -2px;
          right: -2px;
          width: 12px;
          height: 12px;
          border-radius: 50%;
          background: var(--theme-success);
          border: 2px solid var(--theme-bg-secondary);
          animation: pulse-dot 1.5s ease-in-out infinite;
        }

        @keyframes pulse-dot {
          0%, 100% {
            box-shadow: 0 0 0 0 color-mix(in srgb, var(--theme-success) 40%, transparent);
          }
          50% {
            box-shadow: 0 0 0 4px color-mix(in srgb, var(--theme-success) 0%, transparent);
          }
        }

        .download-info {
          flex: 1;
          min-width: 0;
        }

        .download-name {
          font-size: 0.9rem;
          font-weight: 600;
          color: var(--theme-text-primary);
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
          margin-bottom: 0.25rem;
        }

        .download-meta {
          display: flex;
          flex-wrap: wrap;
          gap: 0.5rem;
          font-size: 0.75rem;
          color: var(--theme-text-muted);
        }

        .meta-item {
          display: flex;
          align-items: center;
          gap: 0.25rem;
        }

        .meta-divider {
          color: var(--theme-border-secondary);
        }

        .cache-hit {
          color: var(--theme-success);
          font-weight: 500;
        }

        .cache-hit.medium {
          color: var(--theme-warning);
        }

        .cache-hit.low {
          color: var(--theme-text-muted);
        }

        .download-speed {
          display: flex;
          flex-direction: column;
          align-items: flex-end;
          flex-shrink: 0;
        }

        .speed-value {
          font-size: 1rem;
          font-weight: 700;
          color: var(--theme-success);
          font-variant-numeric: tabular-nums;
        }

        .speed-label {
          font-size: 0.65rem;
          color: var(--theme-text-muted);
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }

        .summary-footer {
          display: flex;
          align-items: center;
          justify-content: space-between;
          padding: 0.75rem 1rem;
          margin-top: 0.5rem;
          border-radius: 8px;
          background: var(--theme-bg-tertiary);
          border: 1px solid var(--theme-border-secondary);
          font-size: 0.75rem;
          color: var(--theme-text-muted);
        }

        .summary-stat {
          display: flex;
          align-items: center;
          gap: 0.35rem;
        }

        .summary-stat strong {
          color: var(--theme-text-primary);
          font-weight: 600;
        }
      `}</style>

      {/* View Toggle */}
      <div className="view-toggle-row">
        <div className="view-toggle">
          <button
            className={`view-toggle-btn ${viewMode === 'games' ? 'active' : ''}`}
            onClick={() => setViewMode('games')}
          >
            <HardDrive />
            Games ({games.length})
          </button>
          <button
            className={`view-toggle-btn ${viewMode === 'clients' ? 'active' : ''}`}
            onClick={() => setViewMode('clients')}
          >
            <Users />
            Clients ({clients.length})
          </button>
        </div>

        <button className="refresh-btn" onClick={fetchSpeeds}>
          <RefreshCw />
          Refresh
        </button>
      </div>

      {/* Downloads List */}
      <div className="downloads-list">
        {viewMode === 'games' ? (
          games.map((game: GameSpeedInfo, index: number) => (
            <div
              key={game.depotId}
              className={`download-item ${index === 0 ? 'top' : ''}`}
            >
              <div className="download-avatar">
                <HardDrive className="fallback-icon" size={20} />
                <div className="active-indicator" />
              </div>

              <div className="download-info">
                <div className="download-name" title={game.gameName || `Depot ${game.depotId}`}>
                  {game.gameName || `Depot ${game.depotId}`}
                </div>
                <div className="download-meta">
                  <span className="meta-item">{formatBytes(game.totalBytes)}</span>
                  <span className="meta-divider">•</span>
                  <span className={`meta-item cache-hit ${
                    game.cacheHitPercent >= 80 ? '' : game.cacheHitPercent >= 50 ? 'medium' : 'low'
                  }`}>
                    {game.cacheHitPercent.toFixed(0)}% hit
                  </span>
                  <span className="meta-divider">•</span>
                  <span className="meta-item">{game.requestCount} requests</span>
                </div>
              </div>

              <div className="download-speed">
                <span className="speed-value">{formatSpeed(game.bytesPerSecond)}</span>
                <span className="speed-label">Speed</span>
              </div>
            </div>
          ))
        ) : (
          clients.map((client: ClientSpeedInfo, index: number) => (
            <div
              key={client.clientIp}
              className={`download-item ${index === 0 ? 'top' : ''}`}
            >
              <div className="download-avatar">
                <Users className="fallback-icon" size={20} />
                <div className="active-indicator" />
              </div>

              <div className="download-info">
                <div className="download-name" title={client.clientIp}>
                  {client.clientIp}
                </div>
                <div className="download-meta">
                  <span className="meta-item">{formatBytes(client.totalBytes)}</span>
                  <span className="meta-divider">•</span>
                  <span className="meta-item">{client.activeGames} game{client.activeGames !== 1 ? 's' : ''}</span>
                </div>
              </div>

              <div className="download-speed">
                <span className="speed-value">{formatSpeed(client.bytesPerSecond)}</span>
                <span className="speed-label">Speed</span>
              </div>
            </div>
          ))
        )}
      </div>

      {/* Summary Footer */}
      <div className="summary-footer">
        <div className="summary-stat">
          <strong>{games.length}</strong> game{games.length !== 1 ? 's' : ''}
        </div>
        <div className="summary-stat">
          <strong>{clients.length}</strong> client{clients.length !== 1 ? 's' : ''}
        </div>
        <div className="summary-stat">
          <strong>{formatSpeed(speedSnapshot?.totalBytesPerSecond || 0)}</strong> total
        </div>
        <div className="summary-stat">
          <strong>{speedSnapshot?.entriesInWindow || 0}</strong> requests/2s
        </div>
      </div>
    </div>
  );
};

export default ActiveDownloadsView;
