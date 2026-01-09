import React, { useState, useEffect, useCallback, useRef } from 'react';
import { Zap, Clock, HardDrive, Users, TrendingUp } from 'lucide-react';
import { useDownloads } from '@contexts/DownloadsContext';
import { useSignalR } from '@contexts/SignalRContext';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { Tooltip } from '@components/ui/Tooltip';
import ApiService from '@services/api.service';
import type { DownloadSpeedSnapshot, SpeedHistorySnapshot } from '../../../types';

// Format bytes to human readable
const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${sizes[i]}`;
};

// Format speed in bits (network speeds are traditionally in bits)
const formatSpeed = (bytesPerSecond: number): { value: string; unit: string } => {
  const bitsPerSecond = bytesPerSecond * 8;
  if (bitsPerSecond === 0) return { value: '0', unit: 'b/s' };
  if (bitsPerSecond < 1024) return { value: bitsPerSecond.toFixed(0), unit: 'b/s' };
  if (bitsPerSecond < 1024 * 1024) return { value: (bitsPerSecond / 1024).toFixed(1), unit: 'Kb/s' };
  if (bitsPerSecond < 1024 * 1024 * 1024) return { value: (bitsPerSecond / (1024 * 1024)).toFixed(1), unit: 'Mb/s' };
  return { value: (bitsPerSecond / (1024 * 1024 * 1024)).toFixed(2), unit: 'Gb/s' };
};

interface DownloadsHeaderProps {
  activeTab: 'active' | 'recent';
  onTabChange: (tab: 'active' | 'recent') => void;
}

const DownloadsHeader: React.FC<DownloadsHeaderProps> = ({ activeTab, onTabChange }) => {
  const { latestDownloads } = useDownloads();
  const signalR = useSignalR();
  const { getRefreshInterval } = useRefreshRate();
  const { timeRange, selectedEventIds } = useTimeFilter();

  // Determine if we're viewing historical data (not live)
  const isHistoricalView = timeRange === 'custom' || selectedEventIds.length > 0;

  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [historySnapshot, setHistorySnapshot] = useState<SpeedHistorySnapshot | null>(null);
  const lastSpeedUpdateRef = useRef<number>(0);
  const pendingSpeedUpdateRef = useRef<NodeJS.Timeout | null>(null);
  const lastActiveCountRef = useRef<number | null>(null);

  // Fetch current speeds (for initial load and visibility change)
  const fetchSpeeds = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      setSpeedSnapshot(data);
      lastActiveCountRef.current = data?.gameSpeeds?.length ?? 0;
    } catch (err) {
      console.error('Failed to fetch speeds:', err);
    }
  }, []);

  // Fetch history for "today" stats
  const fetchHistory = useCallback(async () => {
    try {
      const data = await ApiService.getSpeedHistory(1440); // 24 hours
      setHistorySnapshot(data);
    } catch (err) {
      console.error('Failed to fetch history:', err);
    }
  }, []);

  // SignalR setup with user-controlled throttling
  useEffect(() => {
    // Initial fetch
    fetchSpeeds();
    fetchHistory();

    // SignalR handler with debouncing and throttling
    const handleSpeedUpdate = (speedData: DownloadSpeedSnapshot) => {
      if (pendingSpeedUpdateRef.current) {
        clearTimeout(pendingSpeedUpdateRef.current);
      }

      const newCount = speedData.gameSpeeds?.length ?? 0;

      // ALWAYS accept updates immediately when active games count changes
      // This ensures the badge count updates instantly when downloads start/finish
      const countChanged = lastActiveCountRef.current !== null &&
        lastActiveCountRef.current !== newCount;

      if (countChanged) {
        lastSpeedUpdateRef.current = Date.now();
        lastActiveCountRef.current = newCount;
        setSpeedSnapshot(speedData);
        return;
      }

      pendingSpeedUpdateRef.current = setTimeout(() => {
        const maxRefreshRate = getRefreshInterval();
        const now = Date.now();
        const timeSinceLastUpdate = now - lastSpeedUpdateRef.current;
        const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

        if (timeSinceLastUpdate >= minInterval) {
          lastSpeedUpdateRef.current = now;
          lastActiveCountRef.current = newCount;
          setSpeedSnapshot(speedData);
        }
        pendingSpeedUpdateRef.current = null;
      }, 100);
    };

    signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);

    // Listen for data refresh events to update history
    signalR.on('DownloadsRefresh', fetchHistory);
    signalR.on('FastProcessingComplete', fetchHistory);

    return () => {
      signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
      signalR.off('DownloadsRefresh', fetchHistory);
      signalR.off('FastProcessingComplete', fetchHistory);
      if (pendingSpeedUpdateRef.current) {
        clearTimeout(pendingSpeedUpdateRef.current);
      }
    };
  }, [signalR, getRefreshInterval, fetchSpeeds, fetchHistory]);

  // Use speedSnapshot for all active download data (real-time from Rust speed tracker)
  const isActive = speedSnapshot?.hasActiveDownloads || false;
  const totalSpeed = speedSnapshot?.totalBytesPerSecond || 0;
  const activeGamesCount = speedSnapshot?.gameSpeeds?.length || 0;
  const activeClientsCount = speedSnapshot?.clientSpeeds?.length || 0;
  const todayTotal = historySnapshot?.totalBytes || 0;
  const { value: speedValue, unit: speedUnit } = formatSpeed(totalSpeed);

  return (
    <div className="downloads-header">
      <style>{`
        .downloads-header {
          position: relative;
          padding: 1.25rem;
          border-radius: 16px;
          background: var(--theme-card-bg);
          border: 1px solid var(--theme-border-primary);
          overflow: hidden;
        }

        .header-content {
          display: flex;
          flex-direction: column;
          gap: 1rem;
        }

        @media (min-width: 768px) {
          .header-content {
            flex-direction: row;
            align-items: center;
            justify-content: space-between;
          }
        }

        .speed-section {
          display: flex;
          align-items: center;
          gap: 1rem;
        }

        .speed-indicator {
          position: relative;
          display: flex;
          align-items: center;
          justify-content: center;
          width: 72px;
          height: 72px;
          border-radius: 16px;
          background: var(--theme-bg-tertiary);
          border: 1px solid var(--theme-border-secondary);
        }

        .speed-indicator.active {
          border-color: color-mix(in srgb, var(--theme-success) 50%, var(--theme-border-secondary));
          background: color-mix(in srgb, var(--theme-success) 8%, var(--theme-bg-tertiary));
        }

        .speed-ring {
          position: absolute;
          inset: -3px;
          border-radius: 18px;
          border: 2px solid transparent;
          opacity: 0;
          transition: opacity 0.3s ease;
        }

        .speed-indicator.active .speed-ring {
          opacity: 1;
          border-color: var(--theme-success);
          animation: pulse-ring 2s ease-out infinite;
        }

        @keyframes pulse-ring {
          0% {
            transform: scale(1);
            opacity: 0.8;
          }
          100% {
            transform: scale(1.15);
            opacity: 0;
          }
        }

        .speed-icon {
          color: var(--theme-text-muted);
          transition: color 0.3s ease, transform 0.3s ease;
        }

        .speed-indicator.active .speed-icon {
          color: var(--theme-success);
          animation: icon-pulse 1.5s ease-in-out infinite;
        }

        @keyframes icon-pulse {
          0%, 100% { transform: scale(1); }
          50% { transform: scale(1.1); }
        }

        .speed-content {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .speed-label {
          font-size: 0.7rem;
          font-weight: 600;
          text-transform: uppercase;
          letter-spacing: 0.08em;
          color: var(--theme-text-muted);
        }

        .speed-value {
          display: flex;
          align-items: baseline;
          gap: 0.35rem;
        }

        .speed-number {
          font-size: 2rem;
          font-weight: 700;
          line-height: 1;
          color: var(--theme-text-primary);
          font-variant-numeric: tabular-nums;
          transition: color 0.3s ease;
        }

        .speed-number.active {
          color: var(--theme-success);
        }

        .speed-unit {
          font-size: 0.875rem;
          font-weight: 500;
          color: var(--theme-text-muted);
        }

        .stats-row {
          display: flex;
          gap: 0.5rem;
          margin-top: 0.25rem;
        }

        .stat-chip {
          display: inline-flex;
          align-items: center;
          gap: 0.35rem;
          padding: 0.25rem 0.6rem;
          font-size: 0.7rem;
          font-weight: 500;
          border-radius: 6px;
          background: var(--theme-bg-tertiary);
          color: var(--theme-text-secondary);
          border: 1px solid var(--theme-border-secondary);
        }

        .stat-chip svg {
          width: 12px;
          height: 12px;
          opacity: 0.7;
        }

        .stat-chip.highlight {
          background: color-mix(in srgb, var(--theme-primary) 15%, var(--theme-bg-tertiary));
          border-color: color-mix(in srgb, var(--theme-primary) 30%, var(--theme-border-secondary));
          color: var(--theme-primary);
        }

        .right-section {
          display: flex;
          flex-direction: column;
          align-items: flex-start;
          gap: 0.75rem;
        }

        @media (min-width: 768px) {
          .right-section {
            align-items: flex-end;
          }
        }

        .tab-container {
          display: flex;
          padding: 4px;
          border-radius: 12px;
          background: var(--theme-bg-tertiary);
          border: 1px solid var(--theme-border-secondary);
        }

        .tab-button {
          position: relative;
          display: flex;
          align-items: center;
          gap: 0.5rem;
          padding: 0.5rem 1rem;
          font-size: 0.8rem;
          font-weight: 600;
          color: var(--theme-text-muted);
          background: transparent;
          border: none;
          border-radius: 8px;
          cursor: pointer;
          transition: all 0.2s ease;
        }

        .tab-button:hover:not(.active) {
          color: var(--theme-text-secondary);
          background: color-mix(in srgb, var(--theme-bg-secondary) 50%, transparent);
        }

        .tab-button.active {
          color: var(--theme-button-text);
          background: var(--theme-primary);
          box-shadow: 0 2px 8px color-mix(in srgb, var(--theme-primary) 30%, transparent);
        }

        .tab-button.disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        .tab-button.disabled:hover {
          color: var(--theme-text-muted);
          background: transparent;
        }

        .tab-button svg {
          width: 14px;
          height: 14px;
        }

        .tab-badge {
          display: inline-flex;
          align-items: center;
          justify-content: center;
          min-width: 18px;
          height: 18px;
          padding: 0 5px;
          font-size: 0.65rem;
          font-weight: 700;
          border-radius: 9px;
          background: color-mix(in srgb, var(--theme-text-muted) 20%, transparent);
          color: inherit;
        }

        .tab-button.active .tab-badge {
          background: color-mix(in srgb, white 20%, transparent);
        }

        .tab-button:not(.active) .tab-badge.has-active {
          background: var(--theme-success);
          color: white;
          animation: badge-pulse 2s ease-in-out infinite;
        }

        @keyframes badge-pulse {
          0%, 100% { opacity: 1; }
          50% { opacity: 0.7; }
        }

        .today-stat {
          display: flex;
          align-items: center;
          gap: 0.5rem;
          padding: 0.5rem 0.75rem;
          border-radius: 8px;
          background: color-mix(in srgb, var(--theme-bg-tertiary) 60%, transparent);
          border: 1px solid var(--theme-border-secondary);
        }

        .today-stat svg {
          width: 16px;
          height: 16px;
          color: var(--theme-text-muted);
        }

        .today-label {
          font-size: 0.7rem;
          color: var(--theme-text-muted);
        }

        .today-value {
          font-size: 0.85rem;
          font-weight: 600;
          color: var(--theme-text-primary);
        }

        .active-dot {
          width: 8px;
          height: 8px;
          border-radius: 50%;
          background: var(--theme-success);
          animation: dot-pulse 1.5s ease-in-out infinite;
        }

        @keyframes dot-pulse {
          0%, 100% {
            opacity: 1;
            box-shadow: 0 0 0 0 color-mix(in srgb, var(--theme-success) 40%, transparent);
          }
          50% {
            opacity: 0.8;
            box-shadow: 0 0 0 4px color-mix(in srgb, var(--theme-success) 0%, transparent);
          }
        }
      `}</style>

      <div className="header-content">
        {/* Left: Speed Display */}
        <div className="speed-section">
          <div className={`speed-indicator ${!isHistoricalView && isActive ? 'active' : ''}`}>
            <div className="speed-ring" />
            {isHistoricalView ? (
              <Clock className="speed-icon" size={28} />
            ) : (
              <TrendingUp className="speed-icon" size={28} />
            )}
          </div>

          <div className="speed-content">
            {isHistoricalView ? (
              <>
                <span className="speed-label">Historical View</span>
                <div className="speed-value">
                  <span className="speed-number">Viewing past data</span>
                </div>
                <div className="stats-row">
                  <span className="stat-chip">Live stats unavailable</span>
                </div>
              </>
            ) : (
              <>
                <span className="speed-label">
                  {isActive ? 'Transfer Speed' : 'Idle'}
                </span>
                <div className="speed-value">
                  <span className={`speed-number ${isActive ? 'active' : ''}`}>
                    {speedValue}
                  </span>
                  <span className="speed-unit">{speedUnit}</span>
                </div>
                <div className="stats-row">
                  {activeGamesCount > 0 && (
                    <span className="stat-chip highlight">
                      <HardDrive />
                      {activeGamesCount} {activeGamesCount === 1 ? 'game' : 'games'}
                    </span>
                  )}
                  {activeClientsCount > 0 && (
                    <span className="stat-chip">
                      <Users />
                      {activeClientsCount} {activeClientsCount === 1 ? 'client' : 'clients'}
                    </span>
                  )}
                  {!isActive && (
                    <span className="stat-chip">No active downloads</span>
                  )}
                </div>
              </>
            )}
          </div>
        </div>

        {/* Right: Tabs & Today Stat */}
        <div className="right-section">
          <div className="tab-container">
            {isHistoricalView ? (
              <Tooltip content="Active downloads only available in Live mode">
                <button
                  className={`tab-button disabled`}
                  onClick={(e) => e.preventDefault()}
                >
                  <Zap />
                  Active
                  <span className="tab-badge">
                    {activeGamesCount}
                  </span>
                </button>
              </Tooltip>
            ) : (
              <button
                className={`tab-button ${activeTab === 'active' ? 'active' : ''}`}
                onClick={() => onTabChange('active')}
              >
                <Zap />
                Active
                <span className={`tab-badge ${activeGamesCount > 0 && activeTab !== 'active' ? 'has-active' : ''}`}>
                  {activeGamesCount}
                </span>
              </button>
            )}
            <button
              className={`tab-button ${activeTab === 'recent' ? 'active' : ''}`}
              onClick={() => onTabChange('recent')}
            >
              <Clock />
              Recent
              <span className="tab-badge">{latestDownloads.length}</span>
            </button>
          </div>

          <div className="today-stat">
            <HardDrive />
            <span className="today-label">Today:</span>
            <span className="today-value">{formatBytes(todayTotal)}</span>
            {isActive && <div className="active-dot" />}
          </div>
        </div>
      </div>
    </div>
  );
};

export default DownloadsHeader;
