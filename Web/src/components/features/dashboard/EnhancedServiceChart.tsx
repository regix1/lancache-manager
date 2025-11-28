import React, { useState, useMemo, useCallback, useEffect } from 'react';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2, Info } from 'lucide-react';
import { VictoryPie, VictoryTooltip } from 'victory';
import { formatBytes } from '@utils/formatters';
import { Card } from '@components/ui/Card';

interface EnhancedServiceChartProps {
  serviceStats: any[];
  timeRange?: string;
}

// Custom hook to get theme colors reliably
const useThemeColors = () => {
  const [colors, setColors] = useState(() => getColorsFromCSS());

  useEffect(() => {
    const updateColors = () => {
      // Small delay to ensure CSS variables are updated
      requestAnimationFrame(() => {
        setColors(getColorsFromCSS());
      });
    };

    // Listen for theme changes
    window.addEventListener('themechange', updateColors);

    // Also observe for class changes on html element (theme toggle)
    const observer = new MutationObserver((mutations) => {
      mutations.forEach((mutation) => {
        if (mutation.attributeName === 'class' || mutation.attributeName === 'data-theme') {
          updateColors();
        }
      });
    });

    observer.observe(document.documentElement, { attributes: true });

    return () => {
      window.removeEventListener('themechange', updateColors);
      observer.disconnect();
    };
  }, []);

  return colors;
};

function getColorsFromCSS() {
  const computedStyle = getComputedStyle(document.documentElement);

  return {
    steam: computedStyle.getPropertyValue('--theme-steam').trim() || '#10b981',
    epic: computedStyle.getPropertyValue('--theme-epic').trim() || '#8b5cf6',
    origin: computedStyle.getPropertyValue('--theme-origin').trim() || '#fb923c',
    blizzard: computedStyle.getPropertyValue('--theme-blizzard').trim() || '#3b82f6',
    wsus: computedStyle.getPropertyValue('--theme-wsus').trim() || '#06b6d4',
    riot: computedStyle.getPropertyValue('--theme-riot').trim() || '#ef4444',
    xboxlive: computedStyle.getPropertyValue('--theme-xboxlive').trim() || '#107c10',
    nintendo: computedStyle.getPropertyValue('--theme-nintendo').trim() || '#e60012',
    uplay: computedStyle.getPropertyValue('--theme-uplay').trim() || '#0070ff',
    default: computedStyle.getPropertyValue('--theme-text-secondary').trim() || '#6b7280',
    cacheHit: computedStyle.getPropertyValue('--theme-chart-cache-hit').trim() || '#10b981',
    cacheMiss: computedStyle.getPropertyValue('--theme-chart-cache-miss').trim() || '#f59e0b',
    textPrimary: computedStyle.getPropertyValue('--theme-text-primary').trim() || '#ffffff',
    textMuted: computedStyle.getPropertyValue('--theme-chart-text').trim() || '#9ca3af',
    bgCard: computedStyle.getPropertyValue('--theme-bg-card').trim() || '#1f2937',
    border: computedStyle.getPropertyValue('--theme-chart-border').trim() || '#374151'
  };
}

// Custom tooltip label function for Victory
const getTooltipLabel = (datum: any, tabId: string) => {
  const value = datum.y;
  const percentage = datum.percentage?.toFixed(1) || '0';

  if (tabId === 'bandwidth') {
    return `${datum.x}\n${formatBytes(value)} saved\n${percentage}%`;
  }
  return `${datum.x}\n${formatBytes(value)}\n${percentage}%`;
};

const EnhancedServiceChart: React.FC<EnhancedServiceChartProps> = React.memo(
  ({ serviceStats }) => {
    const [activeTab, setActiveTab] = useState(0);
    const [chartSize, setChartSize] = useState(100);
    const colors = useThemeColors();

    // Touch/swipe handling
    const touchStartX = React.useRef<number>(0);
    const touchEndX = React.useRef<number>(0);
    const containerRef = React.useRef<HTMLDivElement>(null);

    const tabs = [
      { name: 'Service Distribution', id: 'service' },
      { name: 'Cache Hit Ratio', id: 'hit-ratio' },
      { name: 'Bandwidth Saved', id: 'bandwidth' }
    ];

    const getServiceColor = useCallback((serviceName: string) => {
      const serviceLower = serviceName.toLowerCase();
      switch (serviceLower) {
        case 'steam': return colors.steam;
        case 'epic':
        case 'epicgames': return colors.epic;
        case 'origin':
        case 'ea': return colors.origin;
        case 'blizzard':
        case 'battle.net':
        case 'battlenet': return colors.blizzard;
        case 'wsus':
        case 'windows': return colors.wsus;
        case 'riot':
        case 'riotgames': return colors.riot;
        case 'xboxlive':
        case 'xbox': return colors.xboxlive;
        case 'nintendo': return colors.nintendo;
        case 'uplay':
        case 'ubisoft': return colors.uplay;
        default: return colors.default;
      }
    }, [colors]);

    const getServiceDistributionData = useMemo(() => {
      if (!serviceStats || serviceStats.length === 0) {
        return [];
      }

      const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
      if (totalBytes === 0) return [];

      return serviceStats
        .map((s) => ({
          name: s.service,
          value: s.totalBytes,
          percentage: (s.totalBytes / totalBytes) * 100,
          color: getServiceColor(s.service)
        }))
        .sort((a, b) => b.value - a.value);
    }, [serviceStats, getServiceColor]);

    const getCacheHitRatioData = useMemo(() => {
      if (!serviceStats || serviceStats.length === 0) return [];

      const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
      const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
      const total = totalHits + totalMisses;

      if (total === 0) return [];

      return [
        {
          name: 'Cache Hits',
          value: totalHits,
          percentage: (totalHits / total) * 100,
          color: colors.cacheHit
        },
        {
          name: 'Cache Misses',
          value: totalMisses,
          percentage: (totalMisses / total) * 100,
          color: colors.cacheMiss
        }
      ];
    }, [serviceStats, colors]);

    const getBandwidthSavedData = useMemo(() => {
      if (!serviceStats || serviceStats.length === 0) return [];

      const servicesWithSavings = serviceStats
        .map((s) => ({
          name: s.service,
          value: s.totalCacheHitBytes || 0,
          percentage: 0,
          color: getServiceColor(s.service)
        }))
        .filter((s) => s.value > 0)
        .sort((a, b) => b.value - a.value);

      const totalSaved = servicesWithSavings.reduce((sum, s) => sum + s.value, 0);
      if (totalSaved === 0) return [];

      return servicesWithSavings.map((s) => ({
        ...s,
        percentage: (s.value / totalSaved) * 100
      }));
    }, [serviceStats, getServiceColor]);

    const chartData = useMemo(() => {
      switch (tabs[activeTab]?.id) {
        case 'service':
          return getServiceDistributionData;
        case 'hit-ratio':
          return getCacheHitRatioData;
        case 'bandwidth':
          return getBandwidthSavedData;
        default:
          return getServiceDistributionData;
      }
    }, [activeTab, getServiceDistributionData, getCacheHitRatioData, getBandwidthSavedData]);

    const getChartInfo = useMemo(() => {
      const tabId = tabs[activeTab]?.id;
      const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
      const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
      const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
      const hitRatio = totalBytes > 0 ? ((totalHits / totalBytes) * 100).toFixed(1) : '0';

      switch (tabId) {
        case 'service':
          return {
            title: 'Total Data by Service',
            description:
              'Shows the distribution of all data transferred across different gaming services',
            stats: [
              { label: 'Total Data', value: formatBytes(totalBytes) },
              { label: 'Services', value: serviceStats.length },
              { label: 'Hit Ratio', value: `${hitRatio}%` }
            ]
          };
        case 'hit-ratio':
          return {
            title: 'Cache Performance',
            description:
              'Ratio of data served from cache (hits) vs downloaded from internet (misses)',
            stats: [
              { label: 'Cache Hits', value: formatBytes(totalHits) },
              { label: 'Cache Misses', value: formatBytes(totalMisses) },
              { label: 'Efficiency', value: `${hitRatio}%` }
            ]
          };
        case 'bandwidth':
          return {
            title: 'Internet Bandwidth Saved',
            description: 'Amount of internet bandwidth saved by serving cached content locally',
            stats: [
              { label: 'Total Saved', value: formatBytes(totalHits) },
              {
                label: 'Downloads Avoided',
                value: Math.round(totalHits / (50 * 1024 * 1024 * 1024)) || '0'
              },
              { label: 'Cache Efficiency', value: `${hitRatio}%` }
            ]
          };
        default:
          return {
            title: '',
            description: '',
            stats: []
          };
      }
    }, [activeTab, serviceStats]);

    // Swipe handlers
    const handleTouchStart = useCallback((e: React.TouchEvent) => {
      touchEndX.current = 0;
      touchStartX.current = e.touches[0].clientX;
    }, []);

    const handleTouchMove = useCallback((e: React.TouchEvent) => {
      touchEndX.current = e.touches[0].clientX;
    }, []);

    const handleTouchEnd = useCallback(() => {
      if (!touchStartX.current || !touchEndX.current) return;

      const minSwipeDistance = 50;
      const swipeDistance = touchStartX.current - touchEndX.current;

      if (Math.abs(swipeDistance) > minSwipeDistance) {
        if (swipeDistance > 0) {
          setActiveTab((prev) => (prev + 1) % tabs.length);
        } else {
          setActiveTab((prev) => (prev - 1 + tabs.length) % tabs.length);
        }
      }

      touchStartX.current = 0;
      touchEndX.current = 0;
    }, [tabs.length]);

    const chartContainerHeight = 250 + (chartSize - 100) * 2;

    return (
      <Card padding="none">
        <div
          ref={containerRef}
          onTouchStart={handleTouchStart}
          onTouchMove={handleTouchMove}
          onTouchEnd={handleTouchEnd}
        >
          <div className="p-6 pb-4">
            <div className="flex items-center justify-between mb-4">
              <div className="flex items-center">
                <button
                  onClick={() => setActiveTab((prev) => (prev - 1 + tabs.length) % tabs.length)}
                  className="p-1 rounded-lg transition-colors mr-2"
                  style={{ color: 'var(--theme-text-muted)' }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                >
                  <ChevronLeft className="w-4 h-4" />
                </button>

                <div className="w-48 text-center">
                  <h3 className="text-lg font-semibold text-themed-primary truncate">
                    {tabs[activeTab]?.name}
                  </h3>
                </div>

                <button
                  onClick={() => setActiveTab((prev) => (prev + 1) % tabs.length)}
                  className="p-1 rounded-lg transition-colors ml-2"
                  style={{ color: 'var(--theme-text-muted)' }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                >
                  <ChevronRight className="w-4 h-4" />
                </button>
              </div>

              <div className="flex items-center space-x-2">
                <button
                  onClick={() => setChartSize(Math.max(60, chartSize - 10))}
                  className="p-1 rounded-lg transition-colors"
                  style={{ color: 'var(--theme-text-muted)' }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                >
                  <Minimize2 className="w-4 h-4" />
                </button>

                <span className="text-xs text-themed-muted">{chartSize}%</span>

                <button
                  onClick={() => setChartSize(Math.min(140, chartSize + 10))}
                  className="p-1 rounded-lg transition-colors"
                  style={{ color: 'var(--theme-text-muted)' }}
                  onMouseEnter={(e) =>
                    (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
                  }
                  onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
                >
                  <Maximize2 className="w-4 h-4" />
                </button>
              </div>
            </div>

            <div className="flex space-x-1">
              {tabs.map((_, index) => (
                <button
                  key={index}
                  onClick={() => setActiveTab(index)}
                  className="h-1 flex-1 rounded-full transition-all duration-300 ease-out hover:h-1.5"
                  style={{
                    backgroundColor:
                      index === activeTab ? 'var(--theme-primary)' : 'var(--theme-bg-hover)',
                    transform: index === activeTab ? 'scaleY(1.5)' : 'scaleY(1)',
                    opacity: index === activeTab ? 1 : 0.5
                  }}
                />
              ))}
            </div>
          </div>

          <div className="px-6 pb-6">
            {chartData.length > 0 ? (
              <>
                <div
                  className="flex justify-center items-center transition-all duration-500 ease-in-out"
                  style={{
                    height: `${chartContainerHeight}px`,
                    width: '100%',
                    touchAction: 'pan-y',
                    overflow: 'visible',
                    position: 'relative',
                    zIndex: 1
                  }}
                  onTouchStart={handleTouchStart}
                  onTouchMove={handleTouchMove}
                  onTouchEnd={handleTouchEnd}
                >
                  <svg
                    viewBox="0 0 400 400"
                    style={{
                      width: '100%',
                      height: '100%',
                      maxWidth: 400,
                      overflow: 'visible'
                    }}
                  >
                    <VictoryPie
                      standalone={false}
                      data={chartData.map((d) => ({
                        x: d.name,
                        y: d.value,
                        percentage: d.percentage
                      }))}
                      colorScale={chartData.map((d) => d.color)}
                      innerRadius={70}
                      padAngle={2}
                      animate={{ duration: 800, easing: 'cubicOut' }}
                      style={{
                        data: {
                          stroke: colors.border,
                          strokeWidth: 2
                        }
                      }}
                      labelComponent={
                        <VictoryTooltip
                          constrainToVisibleArea={false}
                          flyoutStyle={{
                            fill: 'rgba(0, 0, 0, 0.9)',
                            stroke: colors.border,
                            strokeWidth: 1
                          }}
                          style={{
                            fill: colors.textPrimary,
                            fontSize: 20
                          }}
                          cornerRadius={8}
                          flyoutPadding={16}
                          pointerLength={10}
                          pointerWidth={14}
                        />
                      }
                      labels={({ datum }) =>
                        getTooltipLabel(datum, tabs[activeTab]?.id || 'service')
                      }
                      width={400}
                      height={400}
                    />
                  </svg>
                </div>

                {chartData.length > 0 && (
                  <div
                    className="mt-4 flex flex-wrap justify-center gap-3"
                    onTouchStart={handleTouchStart}
                    onTouchMove={handleTouchMove}
                    onTouchEnd={handleTouchEnd}
                  >
                    {chartData.map((item, index) => (
                      <div
                        key={item.name}
                        className="flex items-center space-x-1 transition-all duration-300 hover:scale-105"
                        style={{
                          animation: `fadeInUp 0.5s ease-out ${index * 0.1}s both`
                        }}
                      >
                        <div
                          className="w-3 h-3 rounded transition-transform duration-300 hover:scale-125"
                          style={{ backgroundColor: item.color }}
                        />
                        <span className="text-xs text-themed-muted">{item.name}:</span>
                        <span className="text-xs text-themed-primary font-medium">
                          {item.percentage.toFixed(1)}%
                        </span>
                      </div>
                    ))}
                  </div>
                )}

                {/* Chart description and stats */}
                <div
                  className="mt-6 pt-4 border-t"
                  style={{ borderColor: 'var(--theme-border-primary)' }}
                  onTouchStart={handleTouchStart}
                  onTouchMove={handleTouchMove}
                  onTouchEnd={handleTouchEnd}
                >
                  <div className="flex items-start gap-2 mb-3">
                    <Info className="w-4 h-4 text-themed-muted mt-0.5 flex-shrink-0" />
                    <div className="flex-1">
                      <h4 className="text-sm font-medium text-themed-secondary mb-1">
                        {getChartInfo.title}
                      </h4>
                      <p className="text-xs text-themed-muted leading-relaxed">
                        {getChartInfo.description}
                      </p>
                    </div>
                  </div>

                  {getChartInfo.stats.length > 0 && (
                    <div className="grid grid-cols-3 gap-4 mt-4">
                      {getChartInfo.stats.map((stat, index) => (
                        <div
                          key={index}
                          className="text-center transition-all duration-300 hover:transform hover:scale-110"
                          style={{
                            animation: `fadeIn 0.6s ease-out ${0.4 + index * 0.1}s both`
                          }}
                        >
                          <div className="text-xs text-themed-muted mb-0.5">{stat.label}</div>
                          <div className="text-sm font-semibold text-themed-secondary transition-colors duration-200 hover:text-themed-primary">
                            {stat.value}
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              </>
            ) : (
              <div className="flex items-center justify-center h-48">
                <p className="text-themed-muted">No data available</p>
              </div>
            )}
          </div>
        </div>
      </Card>
    );
  },
  (prevProps, nextProps) => {
    return (
      prevProps.timeRange === nextProps.timeRange &&
      JSON.stringify(prevProps.serviceStats) === JSON.stringify(nextProps.serviceStats)
    );
  }
);

EnhancedServiceChart.displayName = 'EnhancedServiceChart';

export default EnhancedServiceChart;
