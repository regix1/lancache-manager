import React, { useState, useMemo } from 'react';
import { PieChart, Zap, Database, Gamepad2, CloudDownload } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import { isActiveGame } from '@utils/gameDetection';
import { useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { Card } from '@components/ui/Card';
import DoughnutChart from './DoughnutChart';
import ChartLegend from './ChartLegend';
import { useChartData } from './useChartData';
import type { ServiceAnalyticsChartProps, TabConfig, TabId, LegendItem } from './types';

const ServiceAnalyticsChart: React.FC<ServiceAnalyticsChartProps> = React.memo(
  ({ serviceStats, glassmorphism = false, loading = false }) => {
    const { t } = useTranslation();
    const [activeTab, setActiveTab] = useState<TabId>('service');
    const { gameDetectionData } = useGameDetection();

    const games = useMemo(() => gameDetectionData?.games ?? [], [gameDetectionData?.games]);

    const TABS: TabConfig[] = useMemo(
      () => [
        {
          id: 'service',
          name: t('dashboard.serviceAnalytics.tabs.serviceDistribution'),
          shortName: t('dashboard.serviceAnalytics.tabs.service'),
          icon: PieChart
        },
        {
          id: 'hit-ratio',
          name: t('dashboard.serviceAnalytics.tabs.hitRatioFull'),
          shortName: t('dashboard.serviceAnalytics.tabs.hitRatio'),
          icon: Database
        },
        {
          id: 'bandwidth',
          name: t('dashboard.serviceAnalytics.tabs.bandwidthFull'),
          shortName: t('dashboard.serviceAnalytics.tabs.bandwidth'),
          icon: Zap
        },
        {
          id: 'misses',
          name: t('dashboard.serviceAnalytics.tabs.missesFull'),
          shortName: t('dashboard.serviceAnalytics.tabs.misses'),
          icon: CloudDownload
        },
        {
          id: 'games',
          name: t('dashboard.serviceAnalytics.tabs.gamesFull', 'Games on Disk'),
          shortName: t('dashboard.serviceAnalytics.tabs.games', 'Games'),
          icon: Gamepad2
        }
      ],
      [t]
    );

    // Get chart data from hook
    const chartData = useChartData(serviceStats, activeTab, games);

    // Transform to legend items
    const legendItems: LegendItem[] = useMemo(() => {
      if (chartData.isEmpty || !chartData.datasets[0]) return [];

      const dataset = chartData.datasets[0];
      const originalData = dataset.originalData ?? dataset.data;
      return chartData.labels.map((label, index) => ({
        label,
        value: originalData[index],
        color: dataset.backgroundColor[index],
        percentage: chartData.total > 0 ? (originalData[index] / chartData.total) * 100 : 0,
        valueLabel: formatBytes(originalData[index])
      }));
    }, [chartData]);

    // Center label based on active tab
    const centerLabel = useMemo(() => {
      switch (activeTab) {
        case 'bandwidth':
          return t('dashboard.serviceAnalytics.centerLabels.saved');
        case 'misses':
          return t('dashboard.serviceAnalytics.centerLabels.internet');
        case 'games':
          return t('dashboard.serviceAnalytics.centerLabels.onDisk', 'On Disk');
        case 'hit-ratio':
          return t('dashboard.serviceAnalytics.centerLabels.total');
        default:
          return t('dashboard.serviceAnalytics.centerLabels.total');
      }
    }, [activeTab, t]);

    // Stats for footer
    const footerStats = useMemo(() => {
      if (activeTab === 'games') {
        const activeGames = games.filter(isActiveGame);
        const totalDisk = activeGames.reduce((sum, g) => sum + g.total_size_bytes, 0);
        const sorted = [...activeGames].sort((a, b) => b.total_size_bytes - a.total_size_bytes);
        const largestGame = sorted[0]?.game_name ?? '-';
        return {
          totalBytes: totalDisk,
          hitRatio: 0,
          missBytes: 0,
          serviceCount: 0,
          gameCount: activeGames.length,
          largestGame
        };
      }
      const totalBytes = serviceStats.reduce((sum, s) => sum + s.totalBytes, 0);
      const totalHits = serviceStats.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
      const totalMisses = serviceStats.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
      const hitRatio = totalBytes > 0 ? (totalHits / totalBytes) * 100 : 0;
      return {
        totalBytes,
        hitRatio,
        missBytes: totalMisses,
        serviceCount: serviceStats.length,
        gameCount: 0,
        largestGame: ''
      };
    }, [serviceStats, activeTab, games]);

    return (
      <Card glassmorphism={glassmorphism} className="service-chart-panel">
        {/* Header */}
        <div className="chart-header">
          <div className="header-title">
            <h3>{t('dashboard.serviceAnalytics.title')}</h3>
          </div>

          <div className="tab-toggle">
            {TABS.map((tab) => {
              const Icon = tab.icon;
              return (
                <button
                  key={tab.id}
                  className={`tab-btn ${activeTab === tab.id ? 'active' : ''}`}
                  onClick={() => setActiveTab(tab.id)}
                >
                  <Icon />
                  <span>{tab.shortName}</span>
                </button>
              );
            })}
          </div>
        </div>

        {loading ? (
          <div className="service-analytics-skeleton">
            <div className="service-analytics-skeleton-chart" />
            <div className="service-analytics-skeleton-legend">
              <div className="service-analytics-skeleton-item" />
              <div className="service-analytics-skeleton-item" />
              <div className="service-analytics-skeleton-item" />
            </div>
          </div>
        ) : !chartData.isEmpty ? (
          <>
            {/* Main content - side by side */}
            <div className="chart-body">
              {/* Chart */}
              <div className="chart-side">
                <DoughnutChart
                  labels={chartData.labels}
                  datasets={chartData.datasets}
                  total={chartData.total}
                  centerLabel={centerLabel}
                  gameSliceExtras={chartData.gameSliceExtras}
                />
              </div>

              {/* Legend with progress bars */}
              <ChartLegend items={legendItems} />
            </div>

            {/* Stats footer */}
            <div className="stats-footer">
              <div className="stat-box primary">
                <div className="stat-box-value">
                  {formatBytes(
                    activeTab === 'misses' ? footerStats.missBytes : footerStats.totalBytes
                  )}
                </div>
                <div className="stat-box-label">
                  {activeTab === 'games'
                    ? t('dashboard.serviceAnalytics.footer.totalDisk', 'Total on Disk')
                    : activeTab === 'misses'
                      ? t('dashboard.serviceAnalytics.footer.internetDownloaded')
                      : t('dashboard.serviceAnalytics.footer.totalData')}
                </div>
              </div>
              {activeTab === 'games' ? (
                <>
                  <div className="stat-box">
                    <div className="stat-box-value">{footerStats.gameCount}</div>
                    <div className="stat-box-label">
                      {t('dashboard.serviceAnalytics.footer.gamesDetected', 'Games')}
                    </div>
                  </div>
                  <div className="stat-box">
                    <div className="stat-box-value stat-box-value-truncate">
                      {footerStats.largestGame}
                    </div>
                    <div className="stat-box-label">
                      {t('dashboard.serviceAnalytics.footer.largestGame', 'Largest')}
                    </div>
                  </div>
                </>
              ) : (
                <>
                  <div className="stat-box">
                    <div className="stat-box-value">
                      {activeTab === 'misses'
                        ? formatBytes(footerStats.totalBytes - footerStats.missBytes)
                        : footerStats.serviceCount}
                    </div>
                    <div className="stat-box-label">
                      {activeTab === 'misses'
                        ? t('dashboard.serviceAnalytics.footer.fromCache')
                        : t('dashboard.serviceAnalytics.footer.services')}
                    </div>
                  </div>
                  <div className="stat-box">
                    <div className="stat-box-value">{formatPercent(footerStats.hitRatio)}</div>
                    <div className="stat-box-label">
                      {t('dashboard.serviceAnalytics.footer.hitRate')}
                    </div>
                  </div>
                </>
              )}
            </div>
          </>
        ) : (
          <div className="empty-state">
            <div className="empty-icon">
              <div className="empty-icon-bg" />
              <PieChart size={24} />
            </div>
            <div className="empty-title">{t('dashboard.serviceAnalytics.empty.title')}</div>
            <div className="empty-desc">{t('dashboard.serviceAnalytics.empty.description')}</div>
          </div>
        )}
      </Card>
    );
  }
);

ServiceAnalyticsChart.displayName = 'ServiceAnalyticsChart';

export default ServiceAnalyticsChart;
