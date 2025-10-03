import React, { useEffect, useRef, useState, useMemo } from 'react';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2, Info } from 'lucide-react';
import { formatBytes } from '../../utils/formatters';
import { Card } from '../ui/Card';
import Chart from 'chart.js/auto';

interface EnhancedServiceChartProps {
  serviceStats: any[];
  timeRange?: string;
}

const EnhancedServiceChart: React.FC<EnhancedServiceChartProps> = React.memo(({ serviceStats }) => {
  const [activeTab, setActiveTab] = useState(0);
  const [chartSize, setChartSize] = useState(100);
  const [chartKey, setChartKey] = useState(0); // Force re-render key
  const chartRef = useRef<HTMLCanvasElement>(null);
  const chartInstance = useRef<Chart | null>(null);
  const prevDataRef = useRef<string>('');

  const tabs = [
    { name: 'Service Distribution', id: 'service' },
    { name: 'Cache Hit Ratio', id: 'hit-ratio' },
    { name: 'Bandwidth Saved', id: 'bandwidth' }
  ];

  // Function to get chart colors from CSS variables with fallbacks
  const getChartColors = () => {
    // Use requestAnimationFrame to ensure CSS variables are available
    const computedStyle = getComputedStyle(document.documentElement);
    const colors = [
      computedStyle.getPropertyValue('--theme-chart-1').trim(),
      computedStyle.getPropertyValue('--theme-chart-2').trim(),
      computedStyle.getPropertyValue('--theme-chart-3').trim(),
      computedStyle.getPropertyValue('--theme-chart-4').trim(),
      computedStyle.getPropertyValue('--theme-chart-5').trim(),
      computedStyle.getPropertyValue('--theme-chart-6').trim(),
      computedStyle.getPropertyValue('--theme-chart-7').trim(),
      computedStyle.getPropertyValue('--theme-chart-8').trim()
    ];
    
    // Fallback colors if CSS variables aren't loaded yet
    const fallbacks = [
      '#3b82f6', '#10b981', '#f59e0b', '#ef4444',
      '#8b5cf6', '#06b6d4', '#f97316', '#ec4899'
    ];
    
    return colors.map((color, i) => color || fallbacks[i]);
  };

  const getServiceDistributionData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) {
      // Keep previous data if we had it before
      return { labels: [], data: [], colors: [] };
    }

    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    if (totalBytes === 0) return { labels: [], data: [], colors: [] };

    const chartColors = getChartColors();

    const sorted = serviceStats
      .map((s) => ({
        name: s.service,
        value: s.totalBytes,
        percentage: (s.totalBytes / totalBytes) * 100
      }))
      .sort((a, b) => b.value - a.value);

    return {
      labels: sorted.map((s) => s.name),
      data: sorted.map((s) => s.value),
      colors: sorted.map((_, i) => chartColors[i % chartColors.length])
    };
  }, [serviceStats]);

  const getCacheHitRatioData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return { labels: [], data: [], colors: [] };

    const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
    const total = totalHits + totalMisses;

    if (total === 0) return { labels: [], data: [], colors: [] };

    const computedStyle = getComputedStyle(document.documentElement);
    const hitColor = computedStyle.getPropertyValue('--theme-chart-cache-hit').trim() || '#10b981';
    const missColor =
      computedStyle.getPropertyValue('--theme-chart-cache-miss').trim() || '#f59e0b';

    return {
      labels: ['Cache Hits', 'Cache Misses'],
      data: [totalHits, totalMisses],
      colors: [hitColor, missColor]
    };
  }, [serviceStats]);

  const getBandwidthSavedData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return { labels: [], data: [], colors: [] };

    // Calculate bandwidth saved per service (cache hits only)
    const servicesWithSavings = serviceStats
      .map((s) => ({
        name: s.service,
        value: s.totalCacheHitBytes || 0,
        percentage: 0
      }))
      .filter((s) => s.value > 0)
      .sort((a, b) => b.value - a.value);

    const totalSaved = servicesWithSavings.reduce((sum, s) => sum + s.value, 0);

    if (totalSaved === 0) return { labels: [], data: [], colors: [] };

    // Update percentages
    servicesWithSavings.forEach((s) => {
      s.percentage = (s.value / totalSaved) * 100;
    });

    const chartColors = getChartColors();

    return {
      labels: servicesWithSavings.map((s) => s.name),
      data: servicesWithSavings.map((s) => s.value),
      colors: servicesWithSavings.map((_, i) => chartColors[i % chartColors.length])
    };
  }, [serviceStats]);

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

  // Get chart description and stats based on active tab
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
            }, // Rough estimate assuming 50GB average game size
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

  // Listen for theme changes
  useEffect(() => {
    const handleThemeChange = () => {
      // Delay chart recreation to ensure CSS variables are updated
      setTimeout(() => {
        if (chartInstance.current) {
          chartInstance.current.destroy();
          chartInstance.current = null;
          // Force re-render by updating key
          setChartKey(prev => prev + 1);
        }
      }, 50);
    };

    window.addEventListener('themechange', handleThemeChange);
    return () => window.removeEventListener('themechange', handleThemeChange);
  }, []);
  
  // Force initial render after mount
  useEffect(() => {
    // Small delay to ensure DOM is ready and CSS variables are available
    const timer = setTimeout(() => {
      setChartKey(prev => prev + 1);
    }, 100);
    return () => clearTimeout(timer);
  }, []);

  useEffect(() => {
    if (!chartRef.current || chartData.labels.length === 0) return;

    // Check if data actually changed to prevent unnecessary animations
    const currentDataString = JSON.stringify({
      labels: chartData.labels,
      data: chartData.data
    });

    const dataChanged = currentDataString !== prevDataRef.current;
    prevDataRef.current = currentDataString;

    // Destroy existing chart
    if (chartInstance.current) {
      chartInstance.current.destroy();
    }

    // Get colors from CSS variables
    const computedStyle = getComputedStyle(document.documentElement);
    const borderColor = computedStyle.getPropertyValue('--theme-chart-border').trim() || '#1f2937';
    const textColor = computedStyle.getPropertyValue('--theme-chart-text').trim() || '#9ca3af';
    const titleColor = computedStyle.getPropertyValue('--theme-text-primary').trim() || '#ffffff';

    // Create new chart
    const ctx = chartRef.current.getContext('2d');
    if (!ctx) return;

    chartInstance.current = new Chart(ctx, {
      type: 'doughnut',
      data: {
        labels: chartData.labels,
        datasets: [
          {
            data: chartData.data,
            backgroundColor: chartData.colors,
            borderColor: borderColor,
            borderWidth: 2,
            borderRadius: 0,
            spacing: 0
          }
        ]
      },
      options: {
        responsive: true,
        maintainAspectRatio: true,
        aspectRatio: 1,
        layout: {
          padding: 0
        },
        animation: {
          // Smooth animations for all transitions
          animateRotate: true,
          animateScale: dataChanged,
          duration: 1200,
          easing: 'easeInOutQuart',
          delay: (context) => {
            // Stagger animation for each segment
            return context.dataIndex * 50;
          }
        },
        transitions: {
          active: {
            animation: {
              duration: 400,
              easing: 'easeOutQuart'
            }
          },
          resize: {
            animation: {
              duration: 400,
              easing: 'easeInOutQuart'
            }
          },
          show: {
            animations: {
              colors: {
                from: 'transparent'
              },
              visible: {
                duration: 400
              }
            }
          },
          hide: {
            animations: {
              colors: {
                to: 'transparent'
              },
              visible: {
                duration: 200
              }
            }
          }
        },
        plugins: {
          legend: {
            display: false
          },
          tooltip: {
            backgroundColor: 'rgba(0, 0, 0, 0.9)',
            titleColor: titleColor,
            bodyColor: textColor,
            borderColor: borderColor,
            borderWidth: 1,
            cornerRadius: 8,
            padding: 12,
            displayColors: true,
            animation: {
              duration: 200,
              easing: 'easeOutQuart'
            },
            callbacks: {
              label: (context) => {
                const value = context.raw as number;
                const total = context.dataset.data.reduce(
                  (a, b) => (a as number) + (b as number),
                  0
                ) as number;
                const percentage = ((value / total) * 100).toFixed(1);

                // Different labels based on the chart type
                const tabId = tabs[activeTab]?.id;
                if (tabId === 'bandwidth') {
                  return `${context.label}: ${formatBytes(value)} saved (${percentage}%)`;
                } else if (tabId === 'hit-ratio') {
                  return `${context.label}: ${formatBytes(value)} (${percentage}%)`;
                } else {
                  return `${context.label}: ${formatBytes(value)} (${percentage}%)`;
                }
              }
            }
          }
        },
        cutout: '50%', // Makes the donut hole consistent
        radius: '90%' // Ensures the chart uses most of the available space
      }
    });

    return () => {
      if (chartInstance.current) {
        chartInstance.current.destroy();
      }
    };
  }, [chartData, chartSize, activeTab, chartKey]);

  // Calculate the actual chart container height
  const chartContainerHeight = 200 + (chartSize - 100) * 2;

  return (
    <Card padding="none">
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
        {chartData.labels.length > 0 ? (
          <>
            <div
              className="flex justify-center items-center transition-all duration-500 ease-in-out"
              style={{
                height: `${chartContainerHeight}px`,
                width: '100%'
              }}
            >
              <div
                className="transition-all duration-500 ease-in-out"
                style={{
                  width: `${Math.min(chartContainerHeight, 400)}px`,
                  height: `${Math.min(chartContainerHeight, 400)}px`,
                  transform: 'scale(1)',
                  opacity: 1
                }}
              >
                <canvas
                  key={chartKey}
                  ref={chartRef}
                  className="transition-opacity duration-300"
                  style={{
                    maxHeight: '100%',
                    maxWidth: '100%',
                    opacity: 1
                  }}
                />
              </div>
            </div>

            {chartData.labels.length > 0 && (
              <div className="mt-4 flex flex-wrap justify-center gap-3">
                {chartData.labels.map((label, index) => {
                const value = chartData.data[index];
                const total = chartData.data.reduce((a, b) => a + b, 0);
                const percentage = ((value / total) * 100).toFixed(1);

                return (
                  <div
                    key={label}
                    className="flex items-center space-x-1 transition-all duration-300 hover:scale-105"
                    style={{
                      animation: `fadeInUp 0.5s ease-out ${index * 0.1}s both`
                    }}
                  >
                    <div
                      className="w-3 h-3 rounded transition-transform duration-300 hover:scale-125"
                      style={{ backgroundColor: chartData.colors[index] }}
                    />
                    <span className="text-xs text-themed-muted">{label}:</span>
                    <span className="text-xs text-themed-primary font-medium">{percentage}%</span>
                  </div>
                );
                })}
              </div>
            )}

            {/* Chart description and stats */}
            <div
              className="mt-6 pt-4 border-t"
              style={{ borderColor: 'var(--theme-border-primary)' }}
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
    </Card>
  );
}, (prevProps, nextProps) => {
  // Only re-render if serviceStats actually changed
  return JSON.stringify(prevProps.serviceStats) === JSON.stringify(nextProps.serviceStats);
});

EnhancedServiceChart.displayName = 'EnhancedServiceChart';

export default EnhancedServiceChart;
