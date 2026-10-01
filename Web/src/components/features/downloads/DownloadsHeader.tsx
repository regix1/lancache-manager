import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Zap, Clock, HardDrive, Users, TrendingUp } from 'lucide-react';
import { useDownloads } from '@contexts/DashboardDataContext/hooks';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useSpeed } from '@contexts/SpeedContext/useSpeed';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { buildTrafficKey } from './liveDownloadPreviews';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import Badge from '@components/ui/Badge';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { formatBytes, formatPercent, formatSpeedWithSeparatedUnit } from '@utils/formatters';
import ApiService from '@services/api.service';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useRefreshThrottle } from '@hooks/useRefreshThrottle';
import { useRefreshRate } from '@contexts/useRefreshRate';
import { getErrorMessage } from '@utils/error';
import type { SpeedHistorySnapshot } from '../../../types';

interface DownloadsHeaderProps {
  activeTab: 'active' | 'recent';
  onTabChange: (tab: 'active' | 'recent') => void;
}

const DownloadsHeader: React.FC<DownloadsHeaderProps> = ({ activeTab, onTabChange }) => {
  const { t } = useTranslation();
  const { downloadTotals } = useDownloads();
  const signalR = useSignalR();
  const { speedSnapshot, gameSpeeds, activeDownloadCount, totalActiveClients } = useSpeed();
  const activity = useActivityStatus();
  const { timeRange } = useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  const isHistoricalView = timeRange !== 'live';

  const [historySnapshot, setHistorySnapshot] = useState<SpeedHistorySnapshot | null>(null);

  // Fetch aggregated 24h history for "today" total bytes stat.
  // This intentionally calls ApiService directly rather than using SpeedContext because
  // SpeedContext provides real-time snapshot data (current speed, active games/clients),
  // while this needs aggregated historical data (totalBytes over 24 hours) - a fundamentally
  // different concern that doesn't belong in the real-time speed context.
  const fetchHistory = useCallback(async () => {
    try {
      const data = await ApiService.getSpeedHistory(1440); // 24 hours
      setHistorySnapshot(data);
    } catch (err) {
      // Background refresh - retried on the next SignalR refresh event; the header's speed/count
      // stats still come from the live SpeedContext, so this only affects the 24h history figures.
      console.error('Failed to fetch history:', getErrorMessage(err));
    }
  }, []);

  const scheduleHistoryRefresh = useRefreshThrottle(getRefreshInterval);
  // A live pass sends these events about once a second. The history uses the same cadence as the
  // other live surfaces instead of requesting its 24-hour aggregate once per event.
  const handleHistoryEvent = useCallback(
    () => scheduleHistoryRefresh(() => void fetchHistory()),
    [scheduleHistoryRefresh, fetchHistory]
  );

  // Fetch history on mount and listen for refresh events
  // Note: Speed data comes from SpeedContext (single source of truth)
  useEffect(() => {
    fetchHistory();

    // Listen for data refresh events to update history
    signalR.on('DownloadsRefresh', handleHistoryEvent);
    signalR.on('LogProcessingComplete', handleHistoryEvent);

    return () => {
      signalR.off('DownloadsRefresh', handleHistoryEvent);
      signalR.off('LogProcessingComplete', handleHistoryEvent);
    };
  }, [signalR, fetchHistory, handleHistoryEvent]);

  // Refresh events emitted while the connection was down are gone, so refetch on reconnect.
  useReconnectRefetch(signalR.isConnected, fetchHistory);

  // Use speedSnapshot from SpeedContext (single source of truth for real-time data)
  const isActive = speedSnapshot?.hasActiveDownloads ?? false;
  const isDownloadingDot = gameSpeeds.some((game) =>
    activity.isActive('download', buildTrafficKey(game), 'downloading')
  );
  const totalSpeed = isActive ? (speedSnapshot?.totalBytesPerSecond ?? 0) : 0;
  const activeGamesCount = activeDownloadCount;
  const activeClientsCount = totalActiveClients;
  const todayTotal = historySnapshot?.totalBytes || 0;
  const { value: speedValue, unit: speedUnit } = formatSpeedWithSeparatedUnit(totalSpeed);

  // Overall cache hit rate, counted server-side over every visible row rather than the recent slice
  const totalHitBytes = downloadTotals?.cacheHitBytes ?? 0;
  const totalBytesAll = totalHitBytes + (downloadTotals?.cacheMissBytes ?? 0);
  const overallHitPercent = totalBytesAll > 0 ? (totalHitBytes / totalBytesAll) * 100 : 0;

  return (
    <div className="downloads-header">
      <div className="header-content">
        {/* Left: Speed Display */}
        <div className="speed-section">
          <div className={`speed-indicator ${isDownloadingDot ? 'active' : ''}`}>
            <div className="speed-ring" />
            <TrendingUp className="speed-icon" size={28} />
          </div>

          <div className="speed-content">
            <span className="speed-label caps-label">{t('downloads.header.transferSpeed')}</span>
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
          </div>
        </div>

        {/* Right: Tabs & Today Stat */}
        <div className="right-section">
          <SegmentedControl
            value={activeTab}
            onChange={(next) => onTabChange(next as 'active' | 'recent')}
            showLabels
            options={[
              {
                value: 'active',
                icon: <Zap />,
                label: (
                  <>
                    {t('downloads.header.activeTab')}
                    <Badge variant="neutral" className="badge-count">
                      {activeGamesCount}
                    </Badge>
                  </>
                )
              },
              {
                value: 'recent',
                icon: <Clock />,
                label: (
                  <>
                    {t('downloads.header.recentTab')}
                    <Badge variant="neutral" className="badge-count">
                      {downloadTotals?.count ?? 0}
                    </Badge>
                  </>
                )
              }
            ]}
          />

          <div className="today-stats">
            <div
              className={`today-stat ${isActive && !isHistoricalView ? 'is-live' : ''} ${isHistoricalView ? 'disabled' : ''}`}
            >
              <HardDrive />
              <span className="today-label">{t('downloads.header.todayLabel')}</span>
              <span className="today-value">
                {isHistoricalView ? t('downloads.header.disabled') : formatBytes(todayTotal)}
              </span>
              <span className="today-stat-help">
                <HelpPopover position="left" width={320}>
                  <HelpSection title={t('downloads.header.help.totalBytes.title')} variant="subtle">
                    {t('downloads.header.help.totalBytes.description')}
                  </HelpSection>
                </HelpPopover>
              </span>
            </div>

            <div
              className={`today-stat ${isActive && !isHistoricalView ? 'is-live' : ''} ${isHistoricalView ? 'disabled' : ''}`}
            >
              <TrendingUp />
              <span className="today-label">{t('downloads.header.hitRateLabel')}</span>
              <span className="today-value">
                {isHistoricalView
                  ? t('downloads.header.disabled')
                  : formatPercent(overallHitPercent)}
              </span>
              <span className="today-stat-help">
                <HelpPopover position="left" width={320}>
                  <HelpSection title={t('downloads.header.help.hitRate.title')} variant="subtle">
                    {t('downloads.header.help.hitRate.description')}
                  </HelpSection>
                </HelpPopover>
              </span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};

export default DownloadsHeader;
