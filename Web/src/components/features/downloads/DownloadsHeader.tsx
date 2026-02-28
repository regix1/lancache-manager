import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Zap, Clock, HardDrive, Users, TrendingUp } from 'lucide-react';
import { useDownloads } from '@contexts/DashboardDataContext';
import { useSignalR } from '@contexts/SignalRContext';
import { useSpeed } from '@contexts/SpeedContext';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { Tooltip } from '@components/ui/Tooltip';
import { formatBytes, formatSpeedWithSeparatedUnit } from '@utils/formatters';
import ApiService from '@services/api.service';
import type { SpeedHistorySnapshot } from '../../../types';

interface DownloadsHeaderProps {
  activeTab: 'active' | 'recent';
  onTabChange: (tab: 'active' | 'recent') => void;
}

const DownloadsHeader: React.FC<DownloadsHeaderProps> = ({ activeTab, onTabChange }) => {
  const { t } = useTranslation();
  const { latestDownloads } = useDownloads();
  const signalR = useSignalR();
  const { speedSnapshot, activeDownloadCount, totalActiveClients } = useSpeed();
  const { timeRange } = useTimeFilter();

  // Determine if we're viewing historical/filtered data (not live)
  // Any time range other than 'live' is historical (including presets like 12h, 24h, 7d, etc.)
  const isHistoricalView = timeRange !== 'live';

  const [historySnapshot, setHistorySnapshot] = useState<SpeedHistorySnapshot | null>(null);

  // Fetch history for "today" stats
  const fetchHistory = useCallback(async () => {
    try {
      const data = await ApiService.getSpeedHistory(1440); // 24 hours
      setHistorySnapshot(data);
    } catch (err) {
      console.error('Failed to fetch history:', err);
    }
  }, []);

  // Fetch history on mount and listen for refresh events
  // Note: Speed data comes from SpeedContext (single source of truth)
  useEffect(() => {
    fetchHistory();

    // Listen for data refresh events to update history
    signalR.on('DownloadsRefresh', fetchHistory);
    signalR.on('LogProcessingComplete', fetchHistory);

    return () => {
      signalR.off('DownloadsRefresh', fetchHistory);
      signalR.off('LogProcessingComplete', fetchHistory);
    };
  }, [signalR, fetchHistory]);

  // Use speedSnapshot from SpeedContext (single source of truth for real-time data)
  const isActive = speedSnapshot?.hasActiveDownloads || false;
  const totalSpeed = isActive ? speedSnapshot?.totalBytesPerSecond || 0 : 0;
  const activeGamesCount = activeDownloadCount;
  const activeClientsCount = totalActiveClients;
  const todayTotal = historySnapshot?.totalBytes || 0;
  const { value: speedValue, unit: speedUnit } = formatSpeedWithSeparatedUnit(totalSpeed);

  return (
    <div className="downloads-header">
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
                <span className="speed-label">{t('downloads.header.historical.title')}</span>
                <div className="stats-row">
                  <span className="stat-chip">{t('downloads.header.historical.viewing')}</span>
                  <span className="stat-chip">{t('downloads.header.historical.unavailable')}</span>
                </div>
              </>
            ) : (
              <>
                <span className="speed-label">
                  {isActive ? t('downloads.header.transferSpeed') : t('downloads.header.idle')}
                </span>
                <div className="speed-value">
                  <span className={`speed-number ${isActive ? 'active' : ''}`}>{speedValue}</span>
                  <span className="speed-unit">{speedUnit}</span>
                </div>
                <div className="stats-row">
                  {activeGamesCount > 0 && (
                    <span className="stat-chip highlight">
                      <HardDrive />
                      {t('downloads.header.activeGames', { count: activeGamesCount })}
                    </span>
                  )}
                  {activeClientsCount > 0 && (
                    <span className="stat-chip">
                      <Users />
                      {t('downloads.header.activeClients', { count: activeClientsCount })}
                    </span>
                  )}
                  {!isActive && <span className="stat-chip">{t('downloads.header.noActive')}</span>}
                </div>
              </>
            )}
          </div>
        </div>

        {/* Right: Tabs & Today Stat */}
        <div className="right-section">
          <div className="tab-container">
            {isHistoricalView ? (
              <Tooltip content={t('downloads.header.activeTooltip')}>
                <button className={`tab-button disabled`} onClick={(e) => e.preventDefault()}>
                  <Zap />
                  {t('downloads.header.activeTab')}
                  <span className="tab-badge">—</span>
                </button>
              </Tooltip>
            ) : (
              <button
                className={`tab-button ${activeTab === 'active' ? 'active' : ''}`}
                onClick={() => onTabChange('active')}
              >
                <Zap />
                {t('downloads.header.activeTab')}
                <span
                  className={`tab-badge ${activeGamesCount > 0 && activeTab !== 'active' ? 'has-active' : ''}`}
                >
                  {activeGamesCount}
                </span>
              </button>
            )}
            <button
              className={`tab-button ${activeTab === 'recent' ? 'active' : ''}`}
              onClick={() => onTabChange('recent')}
            >
              <Clock />
              {t('downloads.header.recentTab')}
              <span className="tab-badge">{latestDownloads.length}</span>
            </button>
          </div>

          <div className={`today-stat ${isHistoricalView ? 'disabled' : ''}`}>
            <HardDrive />
            <span className="today-label">{t('downloads.header.todayLabel')}</span>
            <span className="today-value">
              {isHistoricalView ? t('downloads.header.disabled') : formatBytes(todayTotal)}
            </span>
            {isActive && !isHistoricalView && <div className="active-dot" />}
          </div>
        </div>
      </div>
    </div>
  );
};

export default DownloadsHeader;
