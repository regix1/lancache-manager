import React, { useState, useMemo } from 'react';
import { PieChart } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import { isActiveGame } from '@utils/gameDetection';
import { useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { Card } from '@components/ui/Card';
import LoadingSpinner from '@components/common/LoadingSpinner';
import DoughnutChart from './DoughnutChart';
import ChartLegend from './ChartLegend';
import { useChartData } from './useChartData';
import type { ServiceAnalyticsChartProps, TabConfig, TabId, LegendItem } from './types';

const SERVICE_LEGEND_CLASSES: Record<string, string> = {
  steam: 'legend-color-steam',
  epic: 'legend-color-epic',
  epicgames: 'legend-color-epic',
  origin: 'legend-color-origin',
  ea: 'legend-color-origin',
  blizzard: 'legend-color-blizzard',
  battlenet: 'legend-color-blizzard',
  'battle.net': 'legend-color-blizzard',
  wsus: 'legend-color-wsus',
  windows: 'legend-color-wsus',
  riot: 'legend-color-riot',
  riotgames: 'legend-color-riot',
  xbox: 'legend-color-xbox',
  xboxlive: 'legend-color-xbox',
  ubisoft: 'legend-color-ubisoft',
  uplay: 'legend-color-ubisoft',
  gog: 'legend-color-gog',
  rockstar: 'legend-color-rockstar'
};

function getLegendColorClass(label: string, index: number, activeTab: TabId): string {
  if (activeTab === 'games') {
    return `legend-color-game-${(index % 20) + 1}`;
  }

  if (activeTab === 'hit-ratio') {
    return label.toLowerCase().includes('miss')
      ? 'legend-color-cache-miss'
      : 'legend-color-cache-hit';
  }

  const normalizedLabel = label.toLowerCase().replace(/[^a-z0-9.]/g, '');
  return SERVICE_LEGEND_CLASSES[normalizedLabel] ?? 'legend-color-fallback';
}

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
          shortName: t('dashboard.serviceAnalytics.tabs.service')
        },
        {
          id: 'hit-ratio',
          name: t('dashboard.serviceAnalytics.tabs.hitRatioFull'),
          shortName: t('dashboard.serviceAnalytics.tabs.hitRatio')
        },
        {
          id: 'bandwidth',
          name: t('dashboard.serviceAnalytics.tabs.bandwidthFull'),
          shortName: t('dashboard.serviceAnalytics.tabs.bandwidth')
        },
        {
          id: 'misses',
          name: t('dashboard.serviceAnalytics.tabs.missesFull'),
          shortName: t('dashboard.serviceAnalytics.tabs.misses')
        },
        {
          id: 'games',
          name: t('dashboard.serviceAnalytics.tabs.gamesFull', 'Games on Disk'),
          shortName: t('dashboard.serviceAnalytics.tabs.games', 'Games')
        }
      ],
      [t]
    );

    const activeTabConfig = useMemo(
      () => TABS.find((tab) => tab.id === activeTab) ?? TABS[0],
      [TABS, activeTab]
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
            'Find the services still pulling fresh data from the internet.'
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

    const insightCards = useMemo(() => {
      if (activeTab === 'games') {
        return [
          {
            label: t('dashboard.serviceAnalytics.footer.totalDisk', 'Total on Disk'),
            value: formatBytes(footerStats.totalBytes),
            primary: true
          },
          {
            label: t('dashboard.serviceAnalytics.footer.gamesDetected', 'Games'),
            value: footerStats.gameCount
          },
          {
            label: t('dashboard.serviceAnalytics.footer.largestGame', 'Largest'),
            value: footerStats.largestGame
          }
        ];
      }

      return [
        {
          label:
            activeTab === 'misses'
              ? t('dashboard.serviceAnalytics.footer.internetDownloaded')
              : t('dashboard.serviceAnalytics.footer.totalData'),
          value: formatBytes(
            activeTab === 'misses' ? footerStats.missBytes : footerStats.totalBytes
          ),
          primary: true
        },
        {
          label:
            activeTab === 'misses'
              ? t('dashboard.serviceAnalytics.footer.fromCache')
              : t('dashboard.serviceAnalytics.footer.services'),
          value:
            activeTab === 'misses'
              ? formatBytes(Math.max(footerStats.totalBytes - footerStats.missBytes, 0))
              : footerStats.serviceCount
        },
        {
          label: t('dashboard.serviceAnalytics.footer.hitRate'),
          value: formatPercent(footerStats.hitRatio)
        }
      ];
    }, [activeTab, footerStats, t]);

    return (
      <Card glassmorphism={glassmorphism} className="service-chart-panel">
        {/* Header */}
        <div className="service-analytics-header">
          <div className="service-analytics-heading">
            <div className="service-analytics-kicker">{activeTabConfig.name}</div>
            <h3>{t('dashboard.serviceAnalytics.title')}</h3>
            <p>{activeDescription}</p>
          </div>

          <div
            className="service-analytics-tabs"
            role="tablist"
            aria-label={t('dashboard.serviceAnalytics.tabsLabel', 'Service analytics views')}
          >
            {TABS.map((tab) => (
              <button
                key={tab.id}
                type="button"
                role="tab"
                aria-selected={activeTab === tab.id}
                className={`service-analytics-tab ${activeTab === tab.id ? 'active' : ''}`}
                onClick={() => setActiveTab(tab.id)}
              >
                {tab.shortName}
              </button>
            ))}
          </div>
        </div>

        {loading ? (
          <div className="service-analytics-loading">
            <LoadingSpinner size="lg" />
          </div>
        ) : !chartData.isEmpty ? (
          <>
            {/* Main content - side by side */}
            <div className="service-analytics-body">
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
              <div className="analytics-list-card">
                <div className="analytics-list-header">
                  <span>{activeTabConfig.name}</span>
                  <span>
                    {t('dashboard.serviceAnalytics.itemCount', {
                      count: legendItems.length,
                      defaultValue: '{{count}} items'
                    })}
                  </span>
                </div>
                <ChartLegend items={legendItems} />
              </div>
            </div>

            {/* Stats footer */}
            <div className="analytics-insight-grid">
              {insightCards.map((stat) => (
                <div
                  key={stat.label}
                  className={`analytics-insight ${stat.primary ? 'primary' : ''}`}
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
