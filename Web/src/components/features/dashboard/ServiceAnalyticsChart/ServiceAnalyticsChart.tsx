import React, { useState, useMemo } from 'react';
import { PieChart, Zap, Database } from 'lucide-react';
import { formatBytes, formatPercent } from '@utils/formatters';
import { Card } from '@components/ui/Card';
import DoughnutChart from './DoughnutChart';
import ChartLegend from './ChartLegend';
import { useChartData } from './useChartData';
import type { ServiceAnalyticsChartProps, TabConfig, TabId, LegendItem } from './types';

const TABS: TabConfig[] = [
  { id: 'service', name: 'Service Distribution', shortName: 'Services', icon: PieChart },
  { id: 'hit-ratio', name: 'Cache Hit Ratio', shortName: 'Cache', icon: Database },
  { id: 'bandwidth', name: 'Bandwidth Saved', shortName: 'Saved', icon: Zap },
];

const ServiceAnalyticsChart: React.FC<ServiceAnalyticsChartProps> = React.memo(
  ({ serviceStats, glassmorphism = false }) => {
    const [activeTab, setActiveTab] = useState<TabId>('service');

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
        percentage: chartData.total > 0 ? (originalData[index] / chartData.total) * 100 : 0,
      }));
    }, [chartData]);

    // Center label based on active tab
    const centerLabel = useMemo(() => {
      switch (activeTab) {
        case 'bandwidth':
          return 'Saved';
        case 'hit-ratio':
          return 'Total';
        default:
          return 'Total';
      }
    }, [activeTab]);

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
            <h3>Service Analytics</h3>
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
                <div className="stat-box-label">Total Data</div>
              </div>
              <div className="stat-box">
                <div className="stat-box-value">{footerStats.serviceCount}</div>
                <div className="stat-box-label">Services</div>
              </div>
              <div className="stat-box">
                <div className="stat-box-value">{formatPercent(footerStats.hitRatio)}</div>
                <div className="stat-box-label">Hit Rate</div>
              </div>
            </div>
          </>
        ) : (
          <div className="empty-state">
            <div className="empty-icon">
              <div className="empty-icon-bg" />
              <PieChart size={24} />
            </div>
            <div className="empty-title">No Data Available</div>
            <div className="empty-desc">Service statistics will appear here</div>
          </div>
        )}
      </Card>
    );
  }
);

ServiceAnalyticsChart.displayName = 'ServiceAnalyticsChart';

export default ServiceAnalyticsChart;
