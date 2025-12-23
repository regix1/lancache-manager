import React, { useState, useMemo, useCallback, useEffect } from 'react';
import { PieChart as PieChartIcon, Zap, Database } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import { Card } from '@components/ui/Card';
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from 'recharts';
import type { ServiceStat } from '@/types';

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
  { id: 'service', name: 'Service Distribution', shortName: 'Services', icon: PieChartIcon },
  { id: 'hit-ratio', name: 'Cache Hit Ratio', shortName: 'Cache', icon: Database },
  { id: 'bandwidth', name: 'Bandwidth Saved', shortName: 'Saved', icon: Zap }
];

interface ChartDataItem {
  name: string;
  value: number;
  color: string;
  originalValue: number;
  [key: string]: string | number;
}

const EnhancedServiceChart: React.FC<EnhancedServiceChartProps> = React.memo(
  ({ serviceStats, glassmorphism = false }) => {
    const [activeTab, setActiveTab] = useState<TabId>('service');
    const [activeIndex, setActiveIndex] = useState<number | undefined>(undefined);
    const [, setThemeVersion] = useState(0);

    // Theme change listener
    useEffect(() => {
      const handleThemeChange = () => {
        setTimeout(() => setThemeVersion((v) => v + 1), 50);
      };
      window.addEventListener('themechange', handleThemeChange);
      return () => window.removeEventListener('themechange', handleThemeChange);
    }, []);

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
    const getServiceDistributionData = useCallback((): ChartDataItem[] => {
      if (!serviceStats?.length) return [];

      const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
      if (totalBytes === 0) return [];

      return serviceStats
        .map((s) => ({
          name: s.service,
          value: s.totalBytes,
          originalValue: s.totalBytes,
          color: getServiceColor(s.service)
        }))
        .filter((s) => s.value > 0)
        .sort((a, b) => b.value - a.value);
    }, [serviceStats, getServiceColor]);

    const getCacheHitRatioData = useCallback((): ChartDataItem[] => {
      if (!serviceStats?.length) return [];

      const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
      const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);

      if (totalHits + totalMisses === 0) return [];

      const computedStyle = getComputedStyle(document.documentElement);
      const hitColor = computedStyle.getPropertyValue('--theme-chart-cache-hit').trim() || '#22c55e';
      const missColor = computedStyle.getPropertyValue('--theme-chart-cache-miss').trim() || '#ef4444';

      return [
        { name: 'Cache Hits', value: totalHits, originalValue: totalHits, color: hitColor },
        { name: 'Cache Misses', value: totalMisses, originalValue: totalMisses, color: missColor }
      ];
    }, [serviceStats]);

    const getBandwidthSavedData = useCallback((): ChartDataItem[] => {
      if (!serviceStats?.length) return [];

      return serviceStats
        .map((s) => ({
          name: s.service,
          value: s.totalCacheHitBytes || 0,
          originalValue: s.totalCacheHitBytes || 0,
          color: getServiceColor(s.service)
        }))
        .filter((s) => s.value > 0)
        .sort((a, b) => b.value - a.value);
    }, [serviceStats, getServiceColor]);

    // Get current chart data with minimum segment sizing
    const chartData = useMemo((): ChartDataItem[] => {
      let data: ChartDataItem[];
      switch (activeTab) {
        case 'service': data = getServiceDistributionData(); break;
        case 'hit-ratio': data = getCacheHitRatioData(); break;
        case 'bandwidth': data = getBandwidthSavedData(); break;
        default: data = getServiceDistributionData();
      }

      if (data.length === 0) return data;

      const total = data.reduce((sum, d) => sum + d.value, 0);
      if (total === 0) return data;

      // Apply minimum visual percentage (2.5%) for small segments
      const minPercent = 2.5;
      const minValue = (minPercent / 100) * total;

      // Count small segments
      const smallCount = data.filter(d => (d.value / total) * 100 < minPercent && d.value > 0).length;

      // If too many small segments, don't inflate
      if (smallCount > 6) return data;

      // Inflate small segments
      const inflated = data.map(d => {
        const percent = (d.value / total) * 100;
        if (percent > 0 && percent < minPercent) {
          return { ...d, value: minValue };
        }
        return d;
      });

      // Calculate excess and redistribute
      const inflatedTotal = inflated.reduce((sum, d) => sum + d.value, 0);
      const excess = inflatedTotal - total;

      if (excess <= 0) return inflated;

      // Find largest segment
      const largestIdx = data.reduce((maxIdx, d, idx, arr) =>
        d.value > arr[maxIdx].value ? idx : maxIdx, 0);

      if (inflated[largestIdx].value > excess * 1.5) {
        inflated[largestIdx] = { ...inflated[largestIdx], value: inflated[largestIdx].value - excess };
        return inflated;
      }

      // Scale proportionally as fallback
      const scale = total / inflatedTotal;
      return inflated.map(d => ({ ...d, value: d.value * scale }));
    }, [activeTab, getServiceDistributionData, getCacheHitRatioData, getBandwidthSavedData]);

    const totalValue = chartData.reduce((sum, d) => sum + d.originalValue, 0);
    const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    const hitRatio = totalBytes > 0 ? (totalHits / totalBytes) * 100 : 0;

    // Custom tooltip
    const CustomTooltip = ({ active, payload }: { active?: boolean; payload?: Array<{ payload: ChartDataItem }> }) => {
      if (!active || !payload?.length) return null;

      const data = payload[0].payload;
      const percentage = totalValue > 0 ? ((data.originalValue / totalValue) * 100).toFixed(1) : '0';

      return (
        <div className="chart-tooltip">
          <div className="tooltip-label">{data.name}</div>
          <div className="tooltip-value">{formatBytes(data.originalValue)} ({percentage}%)</div>
        </div>
      );
    };

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
              width: 65%;
              max-width: 320px;
            }
          }

          @container (max-width: 419px) {
            .chart-side {
              width: 100%;
              max-width: 220px;
              margin: 0 auto;
            }
          }

          .chart-wrapper {
            position: relative;
            width: 100%;
            aspect-ratio: 1;
          }

          .chart-center {
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            text-align: center;
            pointer-events: none;
            z-index: 10;
          }

          .chart-center-value {
            font-size: 1.35rem;
            font-weight: 700;
            color: var(--theme-text-primary);
            line-height: 1.1;
          }

          @container (min-width: 500px) {
            .chart-center-value {
              font-size: 1.5rem;
            }
          }

          .chart-center-label {
            font-size: 0.65rem;
            color: var(--theme-text-muted);
            text-transform: uppercase;
            letter-spacing: 0.06em;
            margin-top: 0.2rem;
          }

          .chart-tooltip {
            background: rgba(0, 0, 0, 0.9);
            border: 1px solid var(--theme-chart-border, #333);
            border-radius: 10px;
            padding: 10px 14px;
          }

          .tooltip-label {
            font-size: 0.85rem;
            font-weight: 600;
            color: var(--theme-text-primary, #fff);
            margin-bottom: 4px;
          }

          .tooltip-value {
            font-size: 0.8rem;
            color: var(--theme-text-secondary, #a0aec0);
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
            padding: 0.5rem 0.5rem;
            margin: 0 -0.5rem;
            border-bottom: 1px solid var(--theme-border-secondary);
            border-radius: 8px;
            cursor: pointer;
            transition: all 0.2s ease;
          }

          .legend-item:hover {
            background: var(--theme-bg-tertiary);
          }

          .legend-item.active {
            background: var(--theme-bg-tertiary);
          }

          .legend-item.active .legend-name {
            font-weight: 600;
          }

          .legend-item.dimmed {
            opacity: 0.5;
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
            transition: transform 0.2s ease, box-shadow 0.2s ease;
          }

          .legend-item.active .legend-dot {
            transform: scale(1.3);
            box-shadow: 0 0 8px currentColor;
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

        {chartData.length > 0 ? (
          <>
            {/* Main content - side by side */}
            <div className="chart-body">
              {/* Chart */}
              <div className="chart-side">
                <div className="chart-wrapper">
                  <ResponsiveContainer width="100%" height="100%">
                    <PieChart>
                      <Pie
                        data={chartData}
                        cx="50%"
                        cy="50%"
                        innerRadius="70%"
                        outerRadius="95%"
                        paddingAngle={2}
                        dataKey="value"
                        animationBegin={0}
                        animationDuration={800}
                        animationEasing="ease-out"
                        onMouseEnter={(_, index) => setActiveIndex(index)}
                        onMouseLeave={() => setActiveIndex(undefined)}
                      >
                        {chartData.map((entry, index) => {
                          const isActive = activeIndex === index;
                          const isDimmed = activeIndex !== undefined && activeIndex !== index;
                          return (
                            <Cell
                              key={`cell-${index}`}
                              fill={entry.color}
                              stroke="var(--theme-chart-border, #1a1a2e)"
                              strokeWidth={isActive ? 3 : 2}
                              style={{
                                cursor: 'pointer',
                                transition: 'all 0.2s ease',
                                opacity: isDimmed ? 0.5 : 1,
                                filter: isActive ? 'brightness(1.15)' : 'none',
                                transform: isActive ? 'scale(1.02)' : 'scale(1)',
                                transformOrigin: 'center'
                              }}
                            />
                          );
                        })}
                      </Pie>
                      <Tooltip content={<CustomTooltip />} />
                    </PieChart>
                  </ResponsiveContainer>
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
                {chartData.map((item, index) => {
                  const percentage = totalValue > 0 ? (item.originalValue / totalValue) * 100 : 0;
                  const isActive = activeIndex === index;
                  const isDimmed = activeIndex !== undefined && activeIndex !== index;
                  return (
                    <div
                      key={item.name}
                      className={`legend-item ${isActive ? 'active' : ''} ${isDimmed ? 'dimmed' : ''}`}
                      onMouseEnter={() => setActiveIndex(index)}
                      onMouseLeave={() => setActiveIndex(undefined)}
                    >
                      <div className="legend-row">
                        <div className="legend-label">
                          <span
                            className="legend-dot"
                            style={{ backgroundColor: item.color }}
                          />
                          <span className="legend-name">{item.name}</span>
                        </div>
                        <span className="legend-value">{percentage.toFixed(1)}%</span>
                      </div>
                      <div className="legend-bar-track">
                        <div
                          className="legend-bar-fill"
                          style={{
                            width: `${percentage}%`,
                            backgroundColor: item.color
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
              <PieChartIcon size={24} />
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
