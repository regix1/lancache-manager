import React, { useState, useEffect, memo, useMemo, useCallback } from 'react';
import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer } from 'recharts';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2 } from 'lucide-react';

const EnhancedServiceChart = memo(({ serviceStats, timeRange = '24h', onSizeChange }) => {
  const { mockMode } = useData();
  const [activeTab, setActiveTab] = useState(0);
  const [chartSize, setChartSize] = useState(100);

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
    const newSize = Math.max(60, Math.min(140, chartSize + delta));
    setChartSize(newSize);
    if (onSizeChange) {
      onSizeChange(newSize);
    }
  }, [chartSize, onSizeChange]);

  // Process data for Service Distribution
  const getServiceDistributionData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return [];
    
    const knownServices = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'];
    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    if (totalBytes === 0) return [];
    
    const processedData = [];
    let otherBytes = 0;
    let otherCount = 0;

    serviceStats.forEach(s => {
      if (s.totalBytes > 0) {
        const percentage = (s.totalBytes / totalBytes) * 100;
        if (knownServices.includes(s.service.toLowerCase()) || percentage > 5) {
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

  // Process data for Cache Hit Ratio
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

  // Process data for Bandwidth Saved
  const getBandwidthSavedData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return [];
    
    const knownServices = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'];
    const totalSaved = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    
    if (totalSaved === 0) return [];
    
    const processedData = [];
    let otherBytes = 0;
    let otherCount = 0;

    serviceStats.forEach(s => {
      if (s.totalCacheHitBytes > 0) {
        const savedPercentage = (s.totalCacheHitBytes / totalSaved) * 100;
        if (knownServices.includes(s.service.toLowerCase()) || savedPercentage > 5) {
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

  // Custom label function - only for Cache Hit Ratio
  const renderLabel = useCallback(({ name, value, percent, cx, cy, midAngle, innerRadius, outerRadius, index }) => {
    if (tabs[activeTab]?.id !== 'hit-ratio') return null;
    if (percent < 0.05) return null;
    
    const RADIAN = Math.PI / 180;
    const radius = innerRadius + (outerRadius - innerRadius) * 0.7;
    const x = cx + radius * Math.cos(-midAngle * RADIAN);
    const y = cy + radius * Math.sin(-midAngle * RADIAN);
    
    return (
      <text 
        x={x} 
        y={y} 
        fill="white" 
        textAnchor="middle" 
        dominantBaseline="middle"
        className="pointer-events-none"
        style={{ 
          fontSize: '13px',
          fontWeight: 'bold',
          textShadow: '0 1px 2px rgba(0, 0, 0, 0.7)'
        }}
      >
        <tspan x={x} dy="-0.2em">{name}</tspan>
        <tspan x={x} dy="1.2em">{(percent * 100).toFixed(1)}%</tspan>
      </text>
    );
  }, [activeTab, tabs]);

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

  // Calculate dimensions based on chart size
  const dimensions = useMemo(() => {
    const baseHeight = 200;
    const baseRadius = 65;
    
    return {
      chartHeight: baseHeight + (chartSize - 100) * 1,
      outerRadius: baseRadius + (chartSize - 100) * 0.3
    };
  }, [chartSize]);

  return (
    <div className="bg-gray-800 rounded-lg border border-gray-700 h-full overflow-hidden">
      {/* Fixed header section */}
      <div className="p-6 pb-4">
        {/* Navigation and size controls */}
        <div className="flex items-center justify-between mb-4">
          <div className="flex items-center">
            <button
              onClick={handlePrevTab}
              className="p-1 hover:bg-gray-700 rounded transition-colors mr-2"
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
            >
              <ChevronRight className="w-4 h-4 text-gray-400" />
            </button>
          </div>

          <div className="flex items-center space-x-2">
            <button
              onClick={() => adjustSize(-10)}
              className="p-1 hover:bg-gray-700 rounded transition-colors"
            >
              <Minimize2 className="w-4 h-4 text-gray-400" />
            </button>
            
            <span className="text-xs text-gray-500">{chartSize}%</span>
            
            <button
              onClick={() => adjustSize(10)}
              className="p-1 hover:bg-gray-700 rounded transition-colors"
            >
              <Maximize2 className="w-4 h-4 text-gray-400" />
            </button>
          </div>
        </div>

        {/* Tab indicators */}
        <div className="flex space-x-1">
          {tabs.map((tab, index) => (
            <button
              key={tab.id}
              onClick={() => setActiveTab(index)}
              className={`h-1 flex-1 rounded-full transition-colors ${
                index === activeTab ? 'bg-blue-500' : 'bg-gray-700'
              }`}
            />
          ))}
        </div>
      </div>

      {/* Scrollable content area */}
      <div className="overflow-y-auto" style={{ maxHeight: 'calc(100% - 120px)' }}>
        <div className="px-6 pb-6">
          {chartData.length > 0 ? (
            <>
              {/* Chart container with fixed height */}
              <div style={{ height: `${dimensions.chartHeight}px`, width: '100%' }}>
                <ResponsiveContainer width="100%" height="100%">
                  <PieChart margin={{ top: 10, right: 10, bottom: 10, left: 10 }}>
                    <Pie
                      data={chartData}
                      cx="50%"
                      cy="50%"
                      labelLine={false}
                      label={renderLabel}
                      outerRadius={dimensions.outerRadius}
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
              </div>

              {/* Legend */}
              <div className="mt-4 flex flex-wrap justify-center gap-3">
                {chartData.slice(0, chartSize >= 100 ? 6 : 4).map((entry, index) => (
                  <div key={entry.name} className="flex items-center space-x-1">
                    <div 
                      className="w-3 h-3 rounded"
                      style={{ 
                        backgroundColor: currentTab?.id === 'hit-ratio' 
                          ? (index === 0 ? '#10b981' : '#f59e0b')
                          : CHART_COLORS[index % CHART_COLORS.length]
                      }}
                    />
                    <span className="text-xs text-gray-400">{entry.name}:</span>
                    <span className="text-xs text-white font-medium">{entry.percentage.toFixed(1)}%</span>
                    <span className="text-xs text-gray-500">({formatBytes(entry.value)})</span>
                  </div>
                ))}
              </div>

              {/* Tab-specific stats with proper spacing */}
              {currentTab?.id === 'hit-ratio' && (
                <div className="mt-6 pt-4 border-t border-gray-700">
                  <div className="text-center">
                    <p className={`font-bold text-green-400 ${
                      chartSize >= 100 ? 'text-2xl' : 'text-xl'
                    }`}>
                      {formatPercent(overallStats.hitRate)}
                    </p>
                    <p className="text-xs text-gray-500 mt-1">Overall Cache Hit Rate</p>
                    <p className="text-sm text-gray-400 mt-2">
                      Saved {formatBytes(overallStats.totalSaved)} of bandwidth
                    </p>
                  </div>
                </div>
              )}
              
              {currentTab?.id === 'bandwidth' && (
                <div className="mt-6 text-center">
                  <p className="text-xs text-gray-500">Internet bandwidth saved by serving from cache</p>
                  <p className="text-sm text-gray-400 mt-2">
                    Total saved: {formatBytes(overallStats.totalSaved)}
                  </p>
                </div>
              )}
              
              {currentTab?.id === 'service' && (
                <div className="mt-6 text-center">
                  <p className="text-xs text-gray-500">Total data transferred (hits + misses)</p>
                  <p className="text-sm text-gray-400 mt-2">
                    Total: {formatBytes(chartData.reduce((sum, d) => sum + d.value, 0))}
                  </p>
                </div>
              )}
            </>
          ) : (
            <div className="flex items-center justify-center h-48">
              <p className="text-gray-500">No data available for selected time range</p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}, (prevProps, nextProps) => {
  return JSON.stringify(prevProps.serviceStats) === JSON.stringify(nextProps.serviceStats) &&
         prevProps.timeRange === nextProps.timeRange;
});

EnhancedServiceChart.displayName = 'EnhancedServiceChart';

export default EnhancedServiceChart;