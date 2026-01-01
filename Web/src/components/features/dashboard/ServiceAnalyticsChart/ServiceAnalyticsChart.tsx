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
      return chartData.labels.map((label, index) => ({
        label,
        value: dataset.data[index],
        color: dataset.backgroundColor[index],
        percentage: chartData.total > 0 ? (dataset.data[index] / chartData.total) * 100 : 0,
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
        <style>{`
          .service-chart-panel {
            container-type: inline-size;
            display: flex;
            flex-direction: column;
            height: 100%;
          }

          .chart-header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 0.5rem;
            margin-bottom: 0.75rem;
            flex-shrink: 0;
          }

          .header-title h3 {
            font-size: 1rem;
            font-weight: 600;
            color: var(--theme-text-primary);
            margin: 0;
          }

          .tab-toggle {
            display: flex;
            padding: 3px;
            border-radius: 10px;
            background: var(--theme-bg-tertiary);
            border: 1px solid var(--theme-border-secondary);
          }

          .tab-btn {
            display: flex;
            align-items: center;
            gap: 0.35rem;
            padding: 0.35rem 0.55rem;
            font-size: 0.68rem;
            font-weight: 600;
            color: var(--theme-text-muted);
            background: transparent;
            border: none;
            border-radius: 7px;
            cursor: pointer;
            transition: all 0.2s ease;
            white-space: nowrap;
          }

          .tab-btn:hover:not(.active) {
            color: var(--theme-text-secondary);
            background: color-mix(in srgb, var(--theme-bg-secondary) 50%, transparent);
          }

          .tab-btn.active {
            color: var(--theme-button-text);
            background: var(--theme-primary);
            box-shadow: 0 2px 4px color-mix(in srgb, var(--theme-primary) 25%, transparent);
          }

          .tab-btn svg {
            width: 12px;
            height: 12px;
          }

          @container (max-width: 380px) {
            .tab-btn span {
              display: none;
            }
            .tab-btn {
              padding: 0.45rem;
            }
          }

          .chart-body {
            flex: 1;
            display: flex;
            gap: 1rem;
            min-height: 0;
            padding-bottom: 0.75rem;
          }

          @container (min-width: 420px) {
            .chart-body {
              flex-direction: row;
              align-items: center;
            }
          }

          @container (max-width: 419px) {
            .chart-body {
              flex-direction: column;
            }
          }

          .chart-side {
            position: relative;
            flex-shrink: 0;
          }

          @container (min-width: 420px) {
            .chart-side {
              width: 55%;
              max-width: 280px;
            }
          }

          @container (max-width: 419px) {
            .chart-side {
              width: 100%;
              max-width: 200px;
              margin: 0 auto;
            }
          }

          .chart-wrapper {
            position: relative;
            width: 100%;
            aspect-ratio: 1;
          }

          .chart-wrapper canvas {
            max-width: 100%;
            max-height: 100%;
          }

          .chart-center {
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            text-align: center;
            pointer-events: none;
          }

          .chart-center-value {
            font-size: 1.25rem;
            font-weight: 700;
            color: var(--theme-text-primary);
            line-height: 1.1;
          }

          @container (min-width: 500px) {
            .chart-center-value {
              font-size: 1.4rem;
            }
          }

          .chart-center-label {
            font-size: 0.6rem;
            color: var(--theme-text-muted);
            text-transform: uppercase;
            letter-spacing: 0.06em;
            margin-top: 0.2rem;
          }

          .data-side {
            flex: 1;
            display: flex;
            flex-direction: column;
            justify-content: flex-start;
            gap: 0.5rem;
            min-width: 0;
          }

          @container (max-width: 419px) {
            .data-side {
              padding-top: 0.5rem;
            }
          }

          .legend-item {
            display: flex;
            flex-direction: column;
            gap: 0.3rem;
            padding: 0.5rem 0;
            border-bottom: 1px solid var(--theme-border-secondary);
          }

          .legend-item:last-child {
            border-bottom: none;
          }

          .legend-row {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 0.5rem;
          }

          .legend-label {
            display: flex;
            align-items: center;
            gap: 0.4rem;
            min-width: 0;
          }

          .legend-dot {
            width: 10px;
            height: 10px;
            border-radius: 3px;
            flex-shrink: 0;
          }

          .legend-name {
            font-size: 0.8rem;
            font-weight: 500;
            color: var(--theme-text-primary);
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
          }

          .legend-value {
            font-size: 0.75rem;
            font-weight: 600;
            color: var(--theme-text-secondary);
            white-space: nowrap;
          }

          .legend-bar-track {
            height: 4px;
            background: var(--theme-bg-tertiary);
            border-radius: 2px;
            overflow: hidden;
          }

          .legend-bar-fill {
            height: 100%;
            border-radius: 2px;
            transition: width 0.6s ease;
          }

          .stats-footer {
            display: flex;
            gap: 0.75rem;
            padding-top: 0.75rem;
            margin-top: auto;
            border-top: 1px solid var(--theme-border-secondary);
            flex-shrink: 0;
          }

          .stat-box {
            flex: 1;
            text-align: center;
            padding: 0.5rem 0.25rem;
            border-radius: 8px;
            background: var(--theme-bg-secondary);
            transition: all 0.2s ease;
          }

          .stat-box:hover {
            background: var(--theme-bg-tertiary);
          }

          .stat-box.primary {
            background: color-mix(in srgb, var(--theme-primary) 12%, var(--theme-bg-secondary));
            border: 1px solid color-mix(in srgb, var(--theme-primary) 25%, transparent);
          }

          .stat-box-value {
            font-size: 0.9rem;
            font-weight: 700;
            color: var(--theme-text-primary);
            line-height: 1.2;
          }

          .stat-box.primary .stat-box-value {
            color: var(--theme-primary);
          }

          .stat-box-label {
            font-size: 0.6rem;
            color: var(--theme-text-muted);
            text-transform: uppercase;
            letter-spacing: 0.04em;
            margin-top: 0.15rem;
          }

          .empty-state {
            flex: 1;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            padding: 2rem 1rem;
            text-align: center;
          }

          .empty-icon {
            position: relative;
            width: 56px;
            height: 56px;
            margin-bottom: 1rem;
          }

          .empty-icon-bg {
            position: absolute;
            inset: 0;
            border-radius: 50%;
            border: 2px dashed var(--theme-border-secondary);
            animation: rotate-slow 15s linear infinite;
          }

          @keyframes rotate-slow {
            from { transform: rotate(0deg); }
            to { transform: rotate(360deg); }
          }

          .empty-icon svg {
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            color: var(--theme-text-muted);
            opacity: 0.5;
          }

          .empty-title {
            font-size: 0.9rem;
            font-weight: 600;
            color: var(--theme-text-primary);
            margin-bottom: 0.25rem;
          }

          .empty-desc {
            font-size: 0.75rem;
            color: var(--theme-text-muted);
          }
        `}</style>

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
