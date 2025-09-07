import React, { useEffect, useRef, useState, useMemo } from 'react';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2, Info } from 'lucide-react';
import { formatBytes } from '../../utils/formatters';
import { Card } from '../ui/Card';
import Chart from 'chart.js/auto';

interface EnhancedServiceChartProps {
  serviceStats: any[];
  timeRange?: string;
}

const EnhancedServiceChart: React.FC<EnhancedServiceChartProps> = ({ 
  serviceStats
}) => {
  const [activeTab, setActiveTab] = useState(0);
  const [chartSize, setChartSize] = useState(100);
  const chartRef = useRef<HTMLCanvasElement>(null);
  const chartInstance = useRef<Chart | null>(null);
  const prevDataRef = useRef<string>('');

  const tabs = [
    { name: 'Service Distribution', id: 'service' },
    { name: 'Cache Hit Ratio', id: 'hit-ratio' },
    { name: 'Bandwidth Saved', id: 'bandwidth' }
  ];

  // Function to get chart colors from CSS variables
  const getChartColors = () => {
    const computedStyle = getComputedStyle(document.documentElement);
    return [
      computedStyle.getPropertyValue('--theme-chart-1').trim() || '#3b82f6',
      computedStyle.getPropertyValue('--theme-chart-2').trim() || '#10b981',
      computedStyle.getPropertyValue('--theme-chart-3').trim() || '#f59e0b',
      computedStyle.getPropertyValue('--theme-chart-4').trim() || '#ef4444',
      computedStyle.getPropertyValue('--theme-chart-5').trim() || '#8b5cf6',
      computedStyle.getPropertyValue('--theme-chart-6').trim() || '#06b6d4',
      computedStyle.getPropertyValue('--theme-chart-7').trim() || '#f97316',
      computedStyle.getPropertyValue('--theme-chart-8').trim() || '#ec4899'
    ];
  };

  const getServiceDistributionData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return { labels: [], data: [], colors: [] };
    
    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    if (totalBytes === 0) return { labels: [], data: [], colors: [] };
    
    const chartColors = getChartColors();
    
    const sorted = serviceStats
      .map(s => ({
        name: s.service,
        value: s.totalBytes,
        percentage: (s.totalBytes / totalBytes) * 100
      }))
      .sort((a, b) => b.value - a.value);
    
    return {
      labels: sorted.map(s => s.name),
      data: sorted.map(s => s.value),
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
    const missColor = computedStyle.getPropertyValue('--theme-chart-cache-miss').trim() || '#f59e0b';
    
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
      .map(s => ({
        name: s.service,
        value: s.totalCacheHitBytes || 0,
        percentage: 0
      }))
      .filter(s => s.value > 0)
      .sort((a, b) => b.value - a.value);
    
    const totalSaved = servicesWithSavings.reduce((sum, s) => sum + s.value, 0);
    
    if (totalSaved === 0) return { labels: [], data: [], colors: [] };
    
    // Update percentages
    servicesWithSavings.forEach(s => {
      s.percentage = (s.value / totalSaved) * 100;
    });
    
    const chartColors = getChartColors();
    
    return {
      labels: servicesWithSavings.map(s => s.name),
      data: servicesWithSavings.map(s => s.value),
      colors: servicesWithSavings.map((_, i) => chartColors[i % chartColors.length])
    };
  }, [serviceStats]);

  const chartData = useMemo(() => {
    switch(tabs[activeTab]?.id) {
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
    
    switch(tabId) {
      case 'service':
        return {
          title: 'Total Data by Service',
          description: 'Shows the distribution of all data transferred across different gaming services',
          stats: [
            { label: 'Total Data', value: formatBytes(totalBytes) },
            { label: 'Services', value: serviceStats.length },
            { label: 'Hit Ratio', value: `${hitRatio}%` }
          ]
        };
      case 'hit-ratio':
        return {
          title: 'Cache Performance',
          description: 'Ratio of data served from cache (hits) vs downloaded from internet (misses)',
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
            { label: 'Downloads Avoided', value: Math.round(totalHits / (50 * 1024 * 1024 * 1024)) || '0' }, // Rough estimate assuming 50GB average game size
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
      // Force re-render of chart with new colors
      if (chartInstance.current) {
        chartInstance.current.destroy();
        chartInstance.current = null;
      }
    };

    window.addEventListener('themechange', handleThemeChange);
    return () => window.removeEventListener('themechange', handleThemeChange);
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

    // Create new chart
    const ctx = chartRef.current.getContext('2d');
    if (!ctx) return;

    chartInstance.current = new Chart(ctx, {
      type: 'doughnut',
      data: {
        labels: chartData.labels,
        datasets: [{
          data: chartData.data,
          backgroundColor: chartData.colors,
          borderColor: borderColor,
          borderWidth: 2,
          borderRadius: 0,
          spacing: 0
        }]
      },
      options: {
        responsive: true,
        maintainAspectRatio: true,
        aspectRatio: 1,
        layout: {
          padding: 0
        },
        animation: {
          // Only animate when data actually changes or on initial load
          animateRotate: dataChanged,
          animateScale: false,
          duration: dataChanged ? 750 : 0
        },
        plugins: {
          legend: {
            display: false
          },
          tooltip: {
            backgroundColor: 'rgba(0, 0, 0, 0.8)',
            titleColor: '#ffffff',
            bodyColor: textColor,
            borderColor: borderColor,
            borderWidth: 1,
            cornerRadius: 6,
            padding: 12,
            displayColors: true,
            callbacks: {
              label: (context) => {
                const value = context.raw as number;
                const total = context.dataset.data.reduce((a, b) => (a as number) + (b as number), 0) as number;
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
  }, [chartData, chartSize, activeTab]);

  // Calculate the actual chart container height
  const chartContainerHeight = 200 + (chartSize - 100) * 2;

  return (
    <Card padding="none">
      <div className="p-6 pb-4">
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center">
            <button
              onClick={() => setActiveTab((prev) => (prev - 1 + tabs.length) % tabs.length)}
              className="p-1 hover:bg-gray-700 rounded transition-colors mr-2"
            >
              <ChevronLeft className="w-4 h-4 text-gray-400" />
            </button>
            
            <div className="w-48 text-center">
              <h3 className="text-lg font-semibold text-white truncate">
                {tabs[activeTab]?.name}
              </h3>
            </div>
            
            <button
              onClick={() => setActiveTab((prev) => (prev + 1) % tabs.length)}
              className="p-1 hover:bg-gray-700 rounded transition-colors ml-2"
            >
              <ChevronRight className="w-4 h-4 text-gray-400" />
            </button>
          </div>

          <div className="flex items-center space-x-2">
            <button
              onClick={() => setChartSize(Math.max(60, chartSize - 10))}
              className="p-1 hover:bg-gray-700 rounded transition-colors"
            >
              <Minimize2 className="w-4 h-4 text-gray-400" />
            </button>
            
            <span className="text-xs text-gray-500">{chartSize}%</span>
            
            <button
              onClick={() => setChartSize(Math.min(140, chartSize + 10))}
              className="p-1 hover:bg-gray-700 rounded transition-colors"
            >
              <Maximize2 className="w-4 h-4 text-gray-400" />
            </button>
          </div>
        </div>

        <div className="flex space-x-1">
          {tabs.map((_, index) => (
            <button
              key={index}
              onClick={() => setActiveTab(index)}
              className={`h-1 flex-1 rounded-full transition-colors ${
                index === activeTab ? 'bg-blue-500' : 'bg-gray-700'
              }`}
            />
          ))}
        </div>
      </div>

      <div className="px-6 pb-6">
        {chartData.labels.length > 0 ? (
          <>
            <div 
              className="flex justify-center items-center"
              style={{ 
                height: `${chartContainerHeight}px`,
                width: '100%'
              }}
            >
              <div style={{ 
                width: `${Math.min(chartContainerHeight, 400)}px`,
                height: `${Math.min(chartContainerHeight, 400)}px`
              }}>
                <canvas 
                  ref={chartRef}
                  style={{ 
                    maxHeight: '100%',
                    maxWidth: '100%'
                  }}
                />
              </div>
            </div>

            <div className="mt-4 flex flex-wrap justify-center gap-3">
              {chartData.labels.map((label, index) => {
                const value = chartData.data[index];
                const total = chartData.data.reduce((a, b) => a + b, 0);
                const percentage = ((value / total) * 100).toFixed(1);
                
                return (
                  <div key={label} className="flex items-center space-x-1">
                    <div 
                      className="w-3 h-3 rounded"
                      style={{ backgroundColor: chartData.colors[index] }}
                    />
                    <span className="text-xs text-gray-400">{label}:</span>
                    <span className="text-xs text-white font-medium">{percentage}%</span>
                  </div>
                );
              })}
            </div>

            {/* Chart description and stats */}
            <div className="mt-6 pt-4 border-t border-gray-700">
              <div className="flex items-start gap-2 mb-3">
                <Info className="w-4 h-4 text-gray-500 mt-0.5 flex-shrink-0" />
                <div className="flex-1">
                  <h4 className="text-sm font-medium text-gray-200 mb-1">
                    {getChartInfo.title}
                  </h4>
                  <p className="text-xs text-gray-400 leading-relaxed">
                    {getChartInfo.description}
                  </p>
                </div>
              </div>
              
              {getChartInfo.stats.length > 0 && (
                <div className="grid grid-cols-3 gap-4 mt-4">
                  {getChartInfo.stats.map((stat, index) => (
                    <div key={index} className="text-center">
                      <div className="text-xs text-gray-500 mb-0.5">{stat.label}</div>
                      <div className="text-sm font-semibold text-gray-200">{stat.value}</div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </>
        ) : (
          <div className="flex items-center justify-center h-48">
            <p className="text-gray-500">No data available</p>
          </div>
        )}
      </div>
    </Card>
  );
};

export default EnhancedServiceChart;