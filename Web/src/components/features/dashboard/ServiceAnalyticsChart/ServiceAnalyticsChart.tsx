import React, { useState, useMemo } from 'react';
import { PieChart, Zap, Database } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import { Card } from '@components/ui/Card';
import DoughnutChart from './DoughnutChart';
import ChartLegend from './ChartLegend';
import { useChartData } from './useChartData';
import type { ServiceAnalyticsChartProps, TabConfig, TabId, LegendItem } from './types';

const ServiceAnalyticsChart: React.FC<ServiceAnalyticsChartProps> = React.memo(
  ({ serviceStats, glassmorphism = false }) => {
    const { t } = useTranslation();
    const [activeTab, setActiveTab] = useState<TabId>('service');

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
        }
      ],
      [t]
    );

    // Get chart data from hook
    const chartData = useChartData(serviceStats, activeTab);

    // Transform to legend items
    const legendItems: LegendItem[] = useMemo(() => {
      if (chartData.isEmpty || !chartData.datasets[0]) return [];

      const dataset = chartData.datasets[0];
      const originalData = dataset.originalData ?? dataset.data;
      return chartData.labels.map((label, index) => ({
        label,
        value: originalData[index],
        color: dataset.backgroundColor[index],
        percentage: chartData.total > 0 ? (originalData[index] / chartData.total) * 100 : 0
      }));
    }, [chartData]);

    // Center label based on active tab
    const centerLabel = useMemo(() => {
      switch (activeTab) {
        case 'bandwidth':
          return t('dashboard.serviceAnalytics.centerLabels.saved');
        case 'hit-ratio':
          return t('dashboard.serviceAnalytics.centerLabels.total');
        default:
          return t('dashboard.serviceAnalytics.centerLabels.total');
      }
    }, [activeTab, t]);

    // Stats for footer
    const footerStats = useMemo(() => {
      const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
      const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
      const hitRatio = totalBytes > 0 ? (totalHits / totalBytes) * 100 : 0;
      return { totalBytes, hitRatio, serviceCount: serviceStats.length };
    }, [serviceStats]);

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

        {!chartData.isEmpty ? (
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
                />
              </div>

              {/* Legend with progress bars */}
              <ChartLegend items={legendItems} />
            </div>

            {/* Stats footer */}
            <div className="stats-footer">
              <div className="stat-box primary">
                <div className="stat-box-value">{formatBytes(footerStats.totalBytes)}</div>
                <div className="stat-box-label">
                  {t('dashboard.serviceAnalytics.footer.totalData')}
                </div>
              </div>
              <div className="stat-box">
                <div className="stat-box-value">{footerStats.serviceCount}</div>
                <div className="stat-box-label">
                  {t('dashboard.serviceAnalytics.footer.services')}
                </div>
              </div>
              <div className="stat-box">
                <div className="stat-box-value">{formatPercent(footerStats.hitRatio)}</div>
                <div className="stat-box-label">
                  {t('dashboard.serviceAnalytics.footer.hitRate')}
                </div>
              </div>
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
