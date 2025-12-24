import React, { useEffect, useRef, useState, useMemo, useCallback } from 'react';
import { PieChart, Zap, Database } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import { Card } from '@components/ui/Card';
import { Chart, ChartConfiguration, registerables } from 'chart.js';
import { useRefreshRate } from '@contexts/RefreshRateContext';
import type { ServiceStat } from '@/types';

// Register Chart.js components once
Chart.register(...registerables);

interface EnhancedServiceChartProps {
  serviceStats: ServiceStat[];
  timeRange?: string;
  glassmorphism?: boolean;
}

type TabId = 'service' | 'hit-ratio' | 'bandwidth';

interface TabConfig {
  id: TabId;
  name: string;
  shortName: string;
  icon: React.ElementType;
}

const TABS: TabConfig[] = [
  { id: 'service', name: 'Service Distribution', shortName: 'Services', icon: PieChart },
  { id: 'hit-ratio', name: 'Cache Hit Ratio', shortName: 'Cache', icon: Database },
  { id: 'bandwidth', name: 'Bandwidth Saved', shortName: 'Saved', icon: Zap }
];

const EnhancedServiceChart: React.FC<EnhancedServiceChartProps> = React.memo(
  ({ serviceStats, glassmorphism = false }) => {
    const [activeTab, setActiveTab] = useState<TabId>('service');
    const [, setThemeVersion] = useState(0);
    const chartRef = useRef<HTMLCanvasElement>(null);
    const chartInstance = useRef<Chart<'doughnut'> | null>(null);
    const prevDataRef = useRef<string>('');
    const originalDataRef = useRef<number[]>([]);
    const totalValueRef = useRef<number>(0);
    const { refreshRate } = useRefreshRate();

    // Get service color from theme
    const getServiceColor = useCallback((serviceName: string) => {
      const computedStyle = getComputedStyle(document.documentElement);
      const serviceLower = serviceName.toLowerCase();

      const colorMap: Record<string, string> = {
        'steam': '--theme-steam',
        'epic': '--theme-epic',
        'epicgames': '--theme-epic',
        'origin': '--theme-origin',
        'ea': '--theme-origin',
        'blizzard': '--theme-blizzard',
        'battle.net': '--theme-blizzard',
        'battlenet': '--theme-blizzard',
        'wsus': '--theme-wsus',
        'windows': '--theme-wsus',
        'riot': '--theme-riot',
        'riotgames': '--theme-riot',
        'xbox': '--theme-xbox',
        'xboxlive': '--theme-xbox',
        'ubisoft': '--theme-ubisoft',
        'uplay': '--theme-ubisoft',
        'gog': '--theme-text-secondary',
        'rockstar': '--theme-warning'
      };

      const varName = colorMap[serviceLower] || '--theme-text-secondary';
      return computedStyle.getPropertyValue(varName).trim() || '#888888';
    }, []);

    // Chart data generators
    const getServiceDistributionData = useCallback(() => {
      if (!serviceStats?.length) return { labels: [], data: [], colors: [] };

      const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
      if (totalBytes === 0) return { labels: [], data: [], colors: [] };

      const sorted = serviceStats
        .map((s) => ({ name: s.service, value: s.totalBytes }))
        .filter((s) => s.value > 0)
        .sort((a, b) => b.value - a.value);

      return {
        labels: sorted.map((s) => s.name),
        data: sorted.map((s) => s.value),
        colors: sorted.map((s) => getServiceColor(s.name))
      };
    }, [serviceStats, getServiceColor]);

    const getCacheHitRatioData = useCallback(() => {
      if (!serviceStats?.length) return { labels: [], data: [], colors: [] };

      const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
      const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);

      if (totalHits + totalMisses === 0) return { labels: [], data: [], colors: [] };

      const computedStyle = getComputedStyle(document.documentElement);
      const hitColor = computedStyle.getPropertyValue('--theme-chart-cache-hit').trim() || '#22c55e';
      const missColor = computedStyle.getPropertyValue('--theme-chart-cache-miss').trim() || '#ef4444';

      return {
        labels: ['Cache Hits', 'Cache Misses'],
        data: [totalHits, totalMisses],
        colors: [hitColor, missColor]
      };
    }, [serviceStats]);

    const getBandwidthSavedData = useCallback(() => {
      if (!serviceStats?.length) return { labels: [], data: [], colors: [] };

      const servicesWithSavings = serviceStats
        .map((s) => ({ name: s.service, value: s.totalCacheHitBytes || 0 }))
        .filter((s) => s.value > 0)
        .sort((a, b) => b.value - a.value);

      if (!servicesWithSavings.length) return { labels: [], data: [], colors: [] };

      return {
        labels: servicesWithSavings.map((s) => s.name),
        data: servicesWithSavings.map((s) => s.value),
        colors: servicesWithSavings.map((s) => getServiceColor(s.name))
      };
    }, [serviceStats, getServiceColor]);

    // Get current chart data
    const rawChartData = useMemo(() => {
      switch (activeTab) {
        case 'service': return getServiceDistributionData();
        case 'hit-ratio': return getCacheHitRatioData();
        case 'bandwidth': return getBandwidthSavedData();
        default: return getServiceDistributionData();
      }
    }, [activeTab, getServiceDistributionData, getCacheHitRatioData, getBandwidthSavedData]);

    // Inflate small segments for visibility while keeping total constant
    const chartData = useMemo(() => {
      const { labels, data, colors } = rawChartData;
      if (data.length === 0) return rawChartData;

      const total = data.reduce((a, b) => a + b, 0);
      if (total === 0) return rawChartData;

      const minPercent = 1.5; // Minimum visual percentage for small segments
      const minValue = (minPercent / 100) * total;

      // Identify segments that need inflation vs those that can shrink
      const needsInflation: number[] = [];
      const canShrink: number[] = [];
      let inflationNeeded = 0;
      let shrinkableTotal = 0;

      data.forEach((value, index) => {
        const percent = (value / total) * 100;
        if (percent > 0 && percent < minPercent) {
          needsInflation.push(index);
          inflationNeeded += minValue - value;
        } else if (percent >= minPercent) {
          canShrink.push(index);
          shrinkableTotal += value;
        }
      });

      // If nothing needs inflation or nothing can shrink, return original
      if (needsInflation.length === 0 || canShrink.length === 0) {
        return rawChartData;
      }

      // Check if shrinkable segments can absorb the inflation
      // They must remain at least minValue each after shrinking
      const maxShrinkage = shrinkableTotal - (canShrink.length * minValue);
      if (inflationNeeded > maxShrinkage || inflationNeeded <= 0) {
        return rawChartData;
      }

      // Apply inflation: inflate small, proportionally shrink large
      const shrinkRatio = (shrinkableTotal - inflationNeeded) / shrinkableTotal;

      const adjustedData = data.map((value, index) => {
        if (needsInflation.includes(index)) {
          return minValue;
        } else if (canShrink.includes(index)) {
          return value * shrinkRatio;
        }
        return value; // zero values stay zero
      });

      return { labels, data: adjustedData, colors };
    }, [rawChartData]);

    // Theme change listener
    useEffect(() => {
      const handleThemeChange = () => {
        setTimeout(() => {
          if (chartInstance.current) {
            chartInstance.current.destroy();
            chartInstance.current = null;
          }
          prevDataRef.current = '';
          setThemeVersion((v) => v + 1);
        }, 50);
      };

      window.addEventListener('themechange', handleThemeChange);
      return () => window.removeEventListener('themechange', handleThemeChange);
    }, []);

    // Rebuild chart from scratch when refresh rate changes
    useEffect(() => {
      if (chartInstance.current) {
        chartInstance.current.destroy();
        chartInstance.current = null;
      }
      prevDataRef.current = '';
      setThemeVersion((v) => v + 1);
    }, [refreshRate]);

    // Cleanup on unmount
    useEffect(() => {
      return () => {
        if (chartInstance.current) {
          chartInstance.current.destroy();
          chartInstance.current = null;
        }
      };
    }, []);

    // Chart creation and updates
    useEffect(() => {
      if (!chartRef.current || chartData.labels.length === 0) return;

      const ctx = chartRef.current.getContext('2d');
      if (!ctx) return;

      const computedStyle = getComputedStyle(document.documentElement);
      const borderColor = computedStyle.getPropertyValue('--theme-chart-border').trim() || '#1a1a2e';
      const textColor = computedStyle.getPropertyValue('--theme-chart-text').trim() || '#a0aec0';
      const titleColor = computedStyle.getPropertyValue('--theme-text-primary').trim() || '#ffffff';

      const currentDataString = JSON.stringify({ labels: chartData.labels, data: chartData.data, tab: activeTab });

      // If chart exists and only data changed (same tab), update in place
      if (chartInstance.current && prevDataRef.current) {
        const prevData = JSON.parse(prevDataRef.current);
        if (prevData.tab === activeTab) {
          // Same tab - just update data
          chartInstance.current.data.labels = chartData.labels;
          chartInstance.current.data.datasets[0].data = chartData.data;
          chartInstance.current.data.datasets[0].backgroundColor = chartData.colors;
          chartInstance.current.update('none');
          prevDataRef.current = currentDataString;
          return;
        }
      }

      // Destroy existing chart if switching tabs or first render
      if (chartInstance.current) {
        chartInstance.current.destroy();
        chartInstance.current = null;
      }

      prevDataRef.current = currentDataString;

      const config: ChartConfiguration<'doughnut'> = {
        type: 'doughnut',
        data: {
          labels: chartData.labels,
          datasets: [{
            data: chartData.data,
            backgroundColor: chartData.colors,
            borderColor: borderColor,
            borderWidth: 2,
            borderAlign: 'inner',
            borderRadius: 4,
            spacing: 6,
            offset: 0,
            hoverOffset: 8
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: true,
          aspectRatio: 1,
          cutout: '70%',
          radius: '90%',
          layout: {
            padding: 10
          },
          animation: {
            animateRotate: true,
            animateScale: false,
            duration: 600,
            easing: 'easeOutQuart'
          },
          plugins: {
            legend: { display: false },
            tooltip: {
              backgroundColor: 'rgba(0,0,0,0.9)',
              titleColor: titleColor,
              bodyColor: textColor,
              borderColor: borderColor,
              borderWidth: 1,
              cornerRadius: 10,
              padding: 14,
              displayColors: true,
              boxPadding: 6,
              callbacks: {
                label: (context) => {
                  // Use refs to get current original data for tooltip display
                  const originalValue = originalDataRef.current[context.dataIndex] || 0;
                  const total = totalValueRef.current;
                  const percentage = total > 0 ? ((originalValue / total) * 100).toFixed(1) : '0';
                  return `${context.label}: ${formatBytes(originalValue)} (${percentage}%)`;
                }
              }
            }
          }
        }
      };

      chartInstance.current = new Chart(ctx, config);
    }, [chartData, activeTab]);

    // Use raw data for display values (tooltips, legend percentages)
    const originalData = rawChartData.data;
    const totalValue = originalData.reduce((a, b) => a + b, 0);

    // Update refs for tooltip callback access
    originalDataRef.current = originalData;
    totalValueRef.current = totalValue;

    const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    const hitRatio = totalBytes > 0 ? (totalHits / totalBytes) * 100 : 0;

    // Get center label based on tab
    const getCenterLabel = () => {
      switch (activeTab) {
        case 'bandwidth': return 'Saved';
        case 'hit-ratio': return 'Total';
        default: return 'Total';
      }
    };

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
            max-height: 280px;
            overflow-y: auto;
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

        {chartData.labels.length > 0 ? (
          <>
            {/* Main content - side by side */}
            <div className="chart-body">
              {/* Chart */}
              <div className="chart-side">
                <div className="chart-wrapper">
                  <canvas ref={chartRef} />
                  <div className="chart-center">
                    <div className="chart-center-value">
                      {formatBytes(totalValue)}
                    </div>
                    <div className="chart-center-label">
                      {getCenterLabel()}
                    </div>
                  </div>
                </div>
              </div>

              {/* Legend with progress bars */}
              <div className="data-side">
                {chartData.labels.map((label, index) => {
                  // Use original values for display
                  const value = originalData[index];
                  const percentage = totalValue > 0 ? (value / totalValue) * 100 : 0;
                  return (
                    <div key={label} className="legend-item">
                      <div className="legend-row">
                        <div className="legend-label">
                          <span
                            className="legend-dot"
                            style={{ backgroundColor: chartData.colors[index] }}
                          />
                          <span className="legend-name">{label}</span>
                        </div>
                        <span className="legend-value">{percentage.toFixed(1)}%</span>
                      </div>
                      <div className="legend-bar-track">
                        <div
                          className="legend-bar-fill"
                          style={{
                            width: `${Math.max(percentage, 0.5)}%`,
                            backgroundColor: chartData.colors[index]
                          }}
                        />
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>

            {/* Stats footer */}
            <div className="stats-footer">
              <div className="stat-box primary">
                <div className="stat-box-value">{formatBytes(totalBytes)}</div>
                <div className="stat-box-label">Total Data</div>
              </div>
              <div className="stat-box">
                <div className="stat-box-value">{serviceStats.length}</div>
                <div className="stat-box-label">Services</div>
              </div>
              <div className="stat-box">
                <div className="stat-box-value">{hitRatio.toFixed(1)}%</div>
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
  },
  (prevProps, nextProps) => {
    return (
      prevProps.timeRange === nextProps.timeRange &&
      prevProps.glassmorphism === nextProps.glassmorphism &&
      JSON.stringify(prevProps.serviceStats) === JSON.stringify(nextProps.serviceStats)
    );
  }
);

EnhancedServiceChart.displayName = 'EnhancedServiceChart';

export default EnhancedServiceChart;
