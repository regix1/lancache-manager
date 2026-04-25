import React, { useState, useMemo } from 'react';
import { PieChart, Maximize2, Minimize2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { isActiveGame } from '@utils/gameDetection';
import { useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { Tooltip } from '@components/ui/Tooltip';
import LoadingSpinner from '@components/common/LoadingSpinner';
import DoughnutChart from './DoughnutChart';
import ChartLegend from './ChartLegend';
import { useChartData } from './useChartData';
import { getInsightCards, getLegendColorClass, type FooterStats } from './serviceLegendClasses';
import { formatBytes } from '@utils/formatters';
import type { ServiceAnalyticsChartProps, TabId, LegendItem } from './types';

interface TabOption {
  value: TabId;
  label: string;
  tooltip?: string;
}

const ServiceAnalyticsChart: React.FC<ServiceAnalyticsChartProps> = React.memo(
  ({ serviceStats, glassmorphism = false, loading = false }) => {
    const { t } = useTranslation();
    const [activeTab, setActiveTab] = useState<TabId>('service');
    const [showList, setShowList] = useState<boolean>(true);
    const { gameDetectionData } = useGameDetection();

    const games = useMemo(() => gameDetectionData?.games ?? [], [gameDetectionData?.games]);

    const tabs: TabOption[] = useMemo(
      () => [
        {
          value: 'service',
          label: t('dashboard.serviceAnalytics.tabs.service'),
          tooltip: t('dashboard.serviceAnalytics.tabs.serviceDistribution')
        },
        {
          value: 'hit-ratio',
          label: t('dashboard.serviceAnalytics.tabs.hitRatio'),
          tooltip: t('dashboard.serviceAnalytics.tabs.hitRatioFull')
        },
        {
          value: 'bandwidth',
          label: t('dashboard.serviceAnalytics.tabs.bandwidth'),
          tooltip: t('dashboard.serviceAnalytics.tabs.bandwidthFull')
        },
        {
          value: 'misses',
          label: t('dashboard.serviceAnalytics.tabs.misses'),
          tooltip: t('dashboard.serviceAnalytics.tabs.missesFull')
        },
        {
          value: 'games',
          label: t('dashboard.serviceAnalytics.tabs.games', 'Games'),
          tooltip: t('dashboard.serviceAnalytics.tabs.gamesFull', 'Games on Disk')
        }
      ],
      [t]
    );

    const activeTabConfig = useMemo(
      () => tabs.find((tab) => tab.value === activeTab) ?? tabs[0],
      [tabs, activeTab]
    );

    const activeDescription = useMemo(() => {
      switch (activeTab) {
        case 'hit-ratio':
          return t(
            'dashboard.serviceAnalytics.descriptions.hitRatio',
            'See how much traffic was served locally instead of downloaded again.'
          );
        case 'bandwidth':
          return t(
            'dashboard.serviceAnalytics.descriptions.bandwidth',
            'Rank services by bandwidth saved from cache hits.'
          );
        case 'misses':
          return t(
            'dashboard.serviceAnalytics.descriptions.misses',
            'Bytes the cache server fetched from origin (not served from cache). Lower is better.'
          );
        case 'games':
          return t(
            'dashboard.serviceAnalytics.descriptions.games',
            'Review detected game installs and their on-disk footprint.'
          );
        default:
          return t(
            'dashboard.serviceAnalytics.descriptions.service',
            'Compare total cache traffic across every tracked service.'
          );
      }
    }, [activeTab, t]);

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
        valueLabel: formatBytes(originalData[index]),
        colorClassName: getLegendColorClass(label, index, activeTab)
      }));
    }, [activeTab, chartData]);

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
    const footerStats: FooterStats = useMemo(() => {
      if (activeTab === 'games') {
        const activeGames = games.filter(isActiveGame);
        const totalDisk = activeGames.reduce((sum, g) => sum + g.total_size_bytes, 0);
        const sorted = [...activeGames].sort((a, b) => b.total_size_bytes - a.total_size_bytes);
        const largest = sorted[0];
        return {
          totalBytes: totalDisk,
          hitRatio: 0,
          missBytes: 0,
          serviceCount: 0,
          gameCount: activeGames.length,
          largestGame: largest?.game_name ?? '',
          largestGameBytes: largest?.total_size_bytes ?? 0,
          topServiceName: '',
          topServiceBytes: 0,
          totalHitBytes: 0
        };
      }
      const totalBytes = serviceStats.reduce((sum, s) => sum + s.totalBytes, 0);
      const totalHits = serviceStats.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
      const totalMisses = serviceStats.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
      const hitRatio = totalBytes > 0 ? (totalHits / totalBytes) * 100 : 0;
      const sortedByTotal = [...serviceStats].sort((a, b) => b.totalBytes - a.totalBytes);
      const top = sortedByTotal[0];
      return {
        totalBytes,
        hitRatio,
        missBytes: totalMisses,
        serviceCount: serviceStats.length,
        gameCount: 0,
        largestGame: '',
        largestGameBytes: 0,
        topServiceName: top?.service ?? '',
        topServiceBytes: top?.totalBytes ?? 0,
        totalHitBytes: totalHits
      };
    }, [serviceStats, activeTab, games]);

    const insightCards = useMemo(
      () => getInsightCards(activeTab, footerStats, chartData, t),
      [activeTab, footerStats, chartData, t]
    );

    const hideListLabel = t('dashboard.serviceAnalytics.hideList', 'Hide breakdown');
    const showListLabel = t('dashboard.serviceAnalytics.showList', 'Show breakdown');
    const toggleAriaLabel = showList ? hideListLabel : showListLabel;

    return (
      <Card glassmorphism={glassmorphism} className="service-chart-panel">
        {/* Header */}
        <div className="service-analytics-header">
          <div className="service-analytics-heading">
            <div className="service-analytics-kicker">
              {activeTabConfig.tooltip ?? activeTabConfig.label}
            </div>
            <h3>{t('dashboard.serviceAnalytics.title')}</h3>
            <p>{activeDescription}</p>
          </div>

          <div className="service-analytics-controls">
            <SegmentedControl
              options={tabs}
              value={activeTab}
              onChange={(next) => setActiveTab(next as TabId)}
              size="sm"
              showLabels="responsive"
            />
            <Tooltip content={toggleAriaLabel}>
              <Button
                variant="subtle"
                color="default"
                size="xs"
                onClick={() => setShowList((prev) => !prev)}
                aria-pressed={!showList}
                aria-label={toggleAriaLabel}
                title={toggleAriaLabel}
                className="service-analytics-toggle"
              >
                {showList ? <Minimize2 size={14} /> : <Maximize2 size={14} />}
              </Button>
            </Tooltip>
          </div>
        </div>

        {loading ? (
          <div className="service-analytics-loading">
            <LoadingSpinner size="lg" />
          </div>
        ) : !chartData.isEmpty ? (
          <>
            {/* Main content - side by side */}
            <div className="service-analytics-body" data-show-list={showList}>
              {/* Chart */}
              <div className="analytics-chart-card">
                <div className="chart-side">
                  <DoughnutChart
                    labels={chartData.labels}
                    datasets={chartData.datasets}
                    total={chartData.total}
                    centerLabel={centerLabel}
                    gameSliceExtras={chartData.gameSliceExtras}
                  />
                </div>
              </div>

              {/* Legend with progress bars */}
              {showList && (
                <div className="analytics-list-card">
                  <div className="analytics-list-header">
                    <span>{activeTabConfig.tooltip ?? activeTabConfig.label}</span>
                    <span>
                      {t('dashboard.serviceAnalytics.itemCount', {
                        count: legendItems.length,
                        defaultValue: '{{count}} items'
                      })}
                    </span>
                  </div>
                  <ChartLegend items={legendItems} />
                </div>
              )}
            </div>

            {/* Stats footer */}
            <div className="analytics-insight-grid">
              {insightCards.map((stat) => (
                <div
                  key={stat.label}
                  className={`analytics-insight ${stat.tone === 'primary' ? 'primary' : ''}`}
                >
                  <div className="analytics-insight-value">{stat.value}</div>
                  <div className="analytics-insight-label">{stat.label}</div>
                </div>
              ))}
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
