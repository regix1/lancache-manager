import React, { useState, useEffect, memo, useMemo, useCallback } from 'react';
import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer } from 'recharts';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2 } from 'lucide-react';

const EnhancedServiceChart = memo(({ serviceStats, timeRange = '24h' }) => {
  const { mockMode } = useData();
  const [activeTab, setActiveTab] = useState(0);
  const [chartSize, setChartSize] = useState(100); // Percentage of default size

  // Define all tabs
  const tabs = [
    { name: 'Service Distribution', id: 'service' },
    { name: 'Cache Hit Ratio', id: 'hit-ratio' },
    { name: 'Bandwidth Saved', id: 'bandwidth' }
  ];

  const handlePrevTab = useCallback(() => {
    setActiveTab(prev => (prev - 1 + tabs.length) % tabs.length);
  }, [tabs.length]);

  const handleNextTab = useCallback(() => {
    setActiveTab(prev => (prev + 1) % tabs.length);
  }, [tabs.length]);

  const adjustSize = useCallback((delta) => {
    setChartSize(prev => Math.max(60, Math.min(120, prev + delta)));
  }, []);

  // Process data for Service Distribution (total data transferred)
  const getServiceDistributionData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return [];
    
    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    if (totalBytes === 0) return [];
    
    const processedData = [];
    let otherBytes = 0;
    let otherCount = 0;

    serviceStats.forEach(s => {
      if (s.totalBytes > 0) {
        const percentage = (s.totalBytes / totalBytes) * 100;
        if (percentage > 5) {
          processedData.push({
            name: s.service,
            value: s.totalBytes,
            percentage: percentage
          });
        } else {
          otherBytes += s.totalBytes;
          otherCount++;
        }
      }
    });

    if (otherCount > 0 && otherBytes > 0) {
      const otherPercentage = (otherBytes / totalBytes) * 100;
      processedData.push({
        name: `Other (${otherCount})`,
        value: otherBytes,
        percentage: otherPercentage
      });
    }

    return processedData.sort((a, b) => b.value - a.value);
  }, [serviceStats]);

  // Process data for Cache Hit Ratio (hits vs misses)
  const getCacheHitRatioData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return [];
    
    const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
    const total = totalHits + totalMisses;
    
    if (total === 0) return [];
    
    return [
      { name: 'Cache Hits', value: totalHits, percentage: (totalHits / total) * 100 },
      { name: 'Cache Misses', value: totalMisses, percentage: (totalMisses / total) * 100 }
    ];
  }, [serviceStats]);

  // Process data for Bandwidth Saved (cache hits by service)
  const getBandwidthSavedData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return [];
    
    const totalSaved = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    
    if (totalSaved === 0) return [];
    
    const processedData = [];
    let otherBytes = 0;
    let otherCount = 0;

    serviceStats.forEach(s => {
      if (s.totalCacheHitBytes > 0) {
        const savedPercentage = (s.totalCacheHitBytes / totalSaved) * 100;
        if (savedPercentage > 5) {
          processedData.push({
            name: s.service,
            value: s.totalCacheHitBytes,
            percentage: savedPercentage
          });
        } else {
          otherBytes += s.totalCacheHitBytes;
          otherCount++;
        }
      }
    });

    if (otherCount > 0 && otherBytes > 0) {
      processedData.push({
        name: `Other (${otherCount})`,
        value: otherBytes,
        percentage: (otherBytes / totalSaved) * 100
      });
    }
    
    return processedData.sort((a, b) => b.value - a.value);
  }, [serviceStats]);

  // Get data based on active tab
  const chartData = useMemo(() => {
    const currentTab = tabs[activeTab];
    if (!currentTab) return [];
    
    switch(currentTab.id) {
      case 'service':
        return getServiceDistributionData;
      case 'hit-ratio':
        return getCacheHitRatioData;
      case 'bandwidth':
        return getBandwidthSavedData;
      default:
        return getServiceDistributionData;
    }
  }, [activeTab, getServiceDistributionData, getCacheHitRatioData, getBandwidthSavedData, tabs]);

  // Custom tooltip
  const CustomTooltip = useCallback(({ active, payload }) => {
    if (active && payload && payload[0]) {
      const data = payload[0].payload;
      return (
        <div className="bg-gray-800 p-3 rounded-lg border border-gray-600 shadow-lg">
          <p className="text-white font-medium">{data.name}</p>
          <p className="text-gray-300">{formatBytes(data.value)}</p>
          <p className="text-gray-400">{data.percentage.toFixed(1)}%</p>
        </div>
      );
    }
    return null;
  }, []);

  // Custom label function
  const renderLabel = useCallback(({ name, value, percent, cx, cy, midAngle, innerRadius, outerRadius, index }) => {
    if (percent < 0.03) return null;
    
    const RADIAN = Math.PI / 180;
    const radius = outerRadius * (chartSize / 100) + 25;
    const x = cx + radius * Math.cos(-midAngle * RADIAN);
    const y = cy + radius * Math.sin(-midAngle * RADIAN);
    
    return (
      <text 
        x={x} 
        y={y} 
        fill="white" 
        textAnchor={x > cx ? 'start' : 'end'} 
        dominantBaseline="central"
        className="text-xs"
        style={{ fontSize: `${Math.max(10, 12 * (chartSize / 100))}px` }}
      >
        {`${name} ${(percent * 100).toFixed(1)}%`}
      </text>
    );
  }, [chartSize]);

  const chartHeight = 300 * (chartSize / 100);
  const outerRadius = 80 * (chartSize / 100);
  const currentTab = tabs[activeTab];

  // Calculate overall stats
  const overallStats = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return { hitRate: 0, totalSaved: 0 };
    
    const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
    const total = totalHits + totalMisses;
    
    return {
      hitRate: total > 0 ? (totalHits / total) * 100 : 0,
      totalSaved: totalHits
    };
  }, [serviceStats]);

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      {/* Header with tabs - Fixed width layout */}
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center">
          <button
            onClick={handlePrevTab}
            className="p-1 hover:bg-gray-700 rounded transition-colors mr-2"
            aria-label="Previous tab"
          >
            <ChevronLeft className="w-4 h-4 text-gray-400" />
          </button>
          
          <div className="w-48 text-center">
            <h3 className="text-lg font-semibold text-white truncate">
              {currentTab?.name || 'Service Distribution'}
            </h3>
          </div>
          
          <button
            onClick={handleNextTab}
            className="p-1 hover:bg-gray-700 rounded transition-colors ml-2"
            aria-label="Next tab"
          >
            <ChevronRight className="w-4 h-4 text-gray-400" />
          </button>
        </div>

        {/* Size control */}
        <div className="flex items-center space-x-2">
          <button
            onClick={() => adjustSize(-10)}
            className="p-1 hover:bg-gray-700 rounded transition-colors"
            aria-label="Decrease size"
          >
            <Minimize2 className="w-4 h-4 text-gray-400" />
          </button>
          
          <span className="text-xs text-gray-500">{chartSize}%</span>
          
          <button
            onClick={() => adjustSize(10)}
            className="p-1 hover:bg-gray-700 rounded transition-colors"
            aria-label="Increase size"
          >
            <Maximize2 className="w-4 h-4 text-gray-400" />
          </button>
        </div>
      </div>

      {/* Tab indicators */}
      <div className="flex space-x-1 mb-4">
        {tabs.map((tab, index) => (
          <button
            key={tab.id}
            onClick={() => setActiveTab(index)}
            className={`h-1 flex-1 rounded-full transition-colors ${
              index === activeTab ? 'bg-blue-500' : 'bg-gray-700'
            }`}
            aria-label={`Go to ${tab.name}`}
          />
        ))}
      </div>

      {/* Chart */}
      {chartData.length > 0 ? (
        <>
          <ResponsiveContainer width="100%" height={chartHeight}>
            <PieChart>
              <Pie
                data={chartData}
                cx="50%"
                cy="50%"
                labelLine={false}
                label={renderLabel}
                outerRadius={outerRadius}
                fill="#8884d8"
                dataKey="value"
                animationBegin={0}
                animationDuration={400}
              >
                {chartData.map((entry, index) => (
                  <Cell 
                    key={`cell-${index}`} 
                    fill={
                      currentTab?.id === 'hit-ratio' 
                        ? (index === 0 ? '#10b981' : '#f59e0b')
                        : CHART_COLORS[index % CHART_COLORS.length]
                    } 
                  />
                ))}
              </Pie>
              <Tooltip content={<CustomTooltip />} />
            </PieChart>
          </ResponsiveContainer>
          
          {/* Legend */}
          <div className="mt-4 flex flex-wrap justify-center gap-3">
            {chartData.map((entry, index) => (
              <div key={entry.name} className="flex items-center space-x-2">
                <div 
                  className="w-3 h-3 rounded"
                  style={{ 
                    backgroundColor: currentTab?.id === 'hit-ratio' 
                      ? (index === 0 ? '#10b981' : '#f59e0b')
                      : CHART_COLORS[index % CHART_COLORS.length]
                  }}
                />
                <span className="text-xs text-gray-400">
                  {entry.name}: {formatBytes(entry.value)}
                </span>
              </div>
            ))}
          </div>

          {/* Additional stats for specific tabs */}
          {currentTab?.id === 'hit-ratio' && (
            <div className="mt-4 pt-4 border-t border-gray-700">
              <div className="text-center">
                <p className="text-2xl font-bold text-green-400 transition-all duration-500">
                  {formatPercent(overallStats.hitRate)}
                </p>
                <p className="text-xs text-gray-500">Overall Cache Hit Rate</p>
                <p className="text-sm text-gray-400 mt-1">
                  Saved {formatBytes(overallStats.totalSaved)} of bandwidth
                </p>
              </div>
            </div>
          )}
          
          {/* Additional context for bandwidth saved */}
          {currentTab?.id === 'bandwidth' && (
            <div className="mt-4 pt-4 border-t border-gray-700">
              <div className="text-center">
                <p className="text-xs text-gray-500">
                  Internet bandwidth saved by serving from cache
                </p>
                <p className="text-sm text-gray-400 mt-1">
                  Total saved: {formatBytes(overallStats.totalSaved)}
                </p>
              </div>
            </div>
          )}
          
          {/* Additional context for service distribution */}
          {currentTab?.id === 'service' && (
            <div className="mt-4 pt-4 border-t border-gray-700">
              <div className="text-center">
                <p className="text-xs text-gray-500">
                  Total data transferred (hits + misses)
                </p>
                <p className="text-sm text-gray-400 mt-1">
                  Total: {formatBytes(chartData.reduce((sum, d) => sum + d.value, 0))}
                </p>
              </div>
            </div>
          )}
        </>
      ) : (
        <div className="flex items-center justify-center" style={{ height: chartHeight }}>
          <p className="text-gray-500">No data available for selected time range</p>
        </div>
      )}
    </div>
  );
}, (prevProps, nextProps) => {
  // Custom comparison for memo - only re-render if data actually changed
  return JSON.stringify(prevProps.serviceStats) === JSON.stringify(nextProps.serviceStats) &&
         prevProps.timeRange === nextProps.timeRange;
});

EnhancedServiceChart.displayName = 'EnhancedServiceChart';

export default EnhancedServiceChart;