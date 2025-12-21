import React, { useState, useEffect, useCallback, useRef } from 'react';
import { Zap, Clock, HardDrive, Users, TrendingUp } from 'lucide-react';
import { useDownloads } from '@contexts/DownloadsContext';
import { useSignalR } from '@contexts/SignalRContext';
import { usePollingRate } from '@contexts/PollingRateContext';
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

// Format speed
const formatSpeed = (bytesPerSecond: number): { value: string; unit: string } => {
  if (bytesPerSecond === 0) return { value: '0', unit: 'B/s' };
  if (bytesPerSecond < 1024) return { value: bytesPerSecond.toFixed(0), unit: 'B/s' };
  if (bytesPerSecond < 1024 * 1024) return { value: (bytesPerSecond / 1024).toFixed(1), unit: 'KB/s' };
  if (bytesPerSecond < 1024 * 1024 * 1024) return { value: (bytesPerSecond / (1024 * 1024)).toFixed(1), unit: 'MB/s' };
  return { value: (bytesPerSecond / (1024 * 1024 * 1024)).toFixed(2), unit: 'GB/s' };
};

interface DownloadsHeaderProps {
  activeTab: 'active' | 'recent';
  onTabChange: (tab: 'active' | 'recent') => void;
}

const DownloadsHeader: React.FC<DownloadsHeaderProps> = ({ activeTab, onTabChange }) => {
  const { activeDownloads, latestDownloads } = useDownloads();
  const signalR = useSignalR();
  const { pollingRate, getPollingInterval } = usePollingRate();

  const [speedSnapshot, setSpeedSnapshot] = useState<DownloadSpeedSnapshot | null>(null);
  const [historySnapshot, setHistorySnapshot] = useState<SpeedHistorySnapshot | null>(null);
  const pollingRef = useRef<NodeJS.Timeout | null>(null);

  // Fetch current speeds
  const fetchSpeeds = useCallback(async () => {
    try {
      const data = await ApiService.getCurrentSpeeds();
      setSpeedSnapshot(data);
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

  // Initial fetch
  useEffect(() => {
    fetchSpeeds();
    fetchHistory();
  }, [fetchSpeeds, fetchHistory]);

  // Handle SignalR updates
  const handleSpeedUpdate = useCallback((payload: DownloadSpeedSnapshot) => {
    setSpeedSnapshot(payload);
  }, []);

  // Polling/SignalR setup
  useEffect(() => {
    if (pollingRef.current) {
      clearInterval(pollingRef.current);
      pollingRef.current = null;
    }

    if (pollingRate === 'LIVE') {
      signalR.on('DownloadSpeedUpdate', handleSpeedUpdate);
      // Still poll for history less frequently
      pollingRef.current = setInterval(() => {
        fetchSpeeds();
        fetchHistory();
      }, 10000);

      return () => {
        signalR.off('DownloadSpeedUpdate', handleSpeedUpdate);
        if (pollingRef.current) clearInterval(pollingRef.current);
      };
    } else {
      const interval = getPollingInterval();
      pollingRef.current = setInterval(() => {
        fetchSpeeds();
        fetchHistory();
      }, interval);

      return () => {
        if (pollingRef.current) clearInterval(pollingRef.current);
      };
    }
  }, [signalR, pollingRate, getPollingInterval, handleSpeedUpdate, fetchSpeeds, fetchHistory]);

  // Trust speedSnapshot when available (fresh data from API), only use activeDownloads as initial fallback
  const isActive = speedSnapshot
    ? speedSnapshot.hasActiveDownloads
    : activeDownloads.length > 0;
  const totalSpeed = speedSnapshot?.totalBytesPerSecond || 0;
  const activeGamesCount = speedSnapshot
    ? speedSnapshot.gameSpeeds?.length || 0
    : activeDownloads.length;
  const activeClientsCount = speedSnapshot
    ? speedSnapshot.clientSpeeds?.length || 0
    : new Set(activeDownloads.map(d => d.clientIp)).size;
  const todayTotal = historySnapshot?.totalBytes || 0;
  const { value: speedValue, unit: speedUnit } = formatSpeed(totalSpeed);

  return (
    <div className="downloads-header">
      <style>{`
        .downloads-header {
          position: relative;
          padding: 1.25rem;
          border-radius: 16px;
          background: linear-gradient(
            135deg,
            color-mix(in srgb, var(--theme-bg-secondary) 95%, var(--theme-primary) 5%) 0%,
            var(--theme-bg-secondary) 50%,
            color-mix(in srgb, var(--theme-bg-secondary) 95%, var(--theme-success) 5%) 100%
          );
          border: 1px solid var(--theme-border-primary);
          overflow: hidden;
        }

        .downloads-header::before {
          content: '';
          position: absolute;
          top: 0;
          left: 0;
          right: 0;
          height: 1px;
          background: linear-gradient(
            90deg,
            transparent 0%,
            color-mix(in srgb, var(--theme-primary) 40%, transparent) 20%,
            color-mix(in srgb, var(--theme-primary) 60%, transparent) 50%,
            color-mix(in srgb, var(--theme-primary) 40%, transparent) 80%,
            transparent 100%
          );
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
          <div className={`speed-indicator ${isActive ? 'active' : ''}`}>
            <div className="speed-ring" />
            <TrendingUp className="speed-icon" size={28} />
          </div>

          <div className="speed-content">
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
          </div>
        </div>

        {/* Right: Tabs & Today Stat */}
        <div className="right-section">
          <div className="tab-container">
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
