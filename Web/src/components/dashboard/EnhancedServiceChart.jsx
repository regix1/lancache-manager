import React, { useState, useEffect, memo, useMemo, useCallback } from 'react';
import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer } from 'recharts';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';
import { ChevronLeft, ChevronRight, ChevronUp, ChevronDown } from 'lucide-react';

const EnhancedServiceChart = memo(({ serviceStats, timeRange = '24h' }) => {
  const { mockMode } = useData();
  const [activeTab, setActiveTab] = useState(0);
  const [isExpanded, setIsExpanded] = useState(true);

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

  // Process data for Service Distribution (total data transferred)
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
        // Always show known services, only group unknown services into "Other"
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
    
    const knownServices = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'];
    const totalSaved = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    
    if (totalSaved === 0) return [];
    
    const processedData = [];
    let otherBytes = 0;
    let otherCount = 0;

    serviceStats.forEach(s => {
      if (s.totalCacheHitBytes > 0) {
        const savedPercentage = (s.totalCacheHitBytes / totalSaved) * 100;
        // Always show known services, only group unknown services into "Other"
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

  // Custom label function - now more responsive
  const renderLabel = useCallback(({ name, value, percent, cx, cy, midAngle, innerRadius, outerRadius, index }) => {
    if (percent < 0.03) return null;
    
    const RADIAN = Math.PI / 180;
    const radius = outerRadius + 20;
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
      >
        {`${name} ${(percent * 100).toFixed(1)}%`}
      </text>
    );
  }, []);

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
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700 h-full flex flex-col">
      {/* Header with tabs - Fixed width layout */}
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center">
          <button
            onClick={handlePrevTab}
            className="p-1 hover:bg-gray-700 rounded transition-colors mr-2 chart-nav-button"
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
            className="p-1 hover:bg-gray-700 rounded transition-colors ml-2 chart-nav-button"
            aria-label="Next tab"
          >
            <ChevronRight className="w-4 h-4 text-gray-400" />
          </button>
        </div>

        {/* Expand/Collapse control */}
        <button
          onClick={() => setIsExpanded(!isExpanded)}
          className="p-1 hover:bg-gray-700 rounded transition-colors"
          aria-label={isExpanded ? "Collapse" : "Expand"}
        >
          {isExpanded ? (
            <ChevronUp className="w-4 h-4 text-gray-400" />
          ) : (
            <ChevronDown className="w-4 h-4 text-gray-400" />
          )}
        </button>
      </div>

      {/* Tab indicators */}
      <div className="flex space-x-1 mb-4">
        {tabs.map((tab, index) => (
          <button
            key={tab.id}
            onClick={() => setActiveTab(index)}
            className={`h-1 flex-1 rounded-full tab-indicator ${
              index === activeTab ? 'bg-blue-500' : 'bg-gray-700'
            }`}
            aria-label={`Go to ${tab.name}`}
          />
        ))}
      </div>

      {/* Chart content area - collapsible */}
      {isExpanded && (
        <div className="flex-1 flex flex-col">
          {chartData.length > 0 ? (
            <>
              {/* Responsive container with dynamic height */}
              <div style={{ height: '250px', width: '100%' }}>
                <ResponsiveContainer width="100%" height="100%">
                  <PieChart>
                    <Pie
                      data={chartData}
                      cx="50%"
                      cy="50%"
                      labelLine={false}
                      label={renderLabel}
                      outerRadius={75}
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
              
              {/* Legend - now more compact */}
              <div className="mt-2 flex flex-wrap justify-center gap-2">
                {chartData.slice(0, 4).map((entry, index) => (
                  <div key={entry.name} className="flex items-center space-x-1 animated-badge">
                    <div 
                      className="w-2 h-2 rounded"
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
                {chartData.length > 4 && (
                  <span className="text-xs text-gray-500">+{chartData.length - 4} more</span>
                )}
              </div>

              {/* Additional stats for specific tabs - now more compact */}
              {currentTab?.id === 'hit-ratio' && (
                <div className="mt-3 pt-3 border-t border-gray-700">
                  <div className="text-center">
                    <p className="text-xl font-bold text-green-400 smooth-number">
                      {formatPercent(overallStats.hitRate)}
                    </p>
                    <p className="text-xs text-gray-500">Overall Cache Hit Rate</p>
                  </div>
                </div>
              )}
              
              {/* Additional context for other tabs - more compact */}
              {currentTab?.id === 'bandwidth' && (
                <div className="mt-3 text-center">
                  <p className="text-xs text-gray-400 smooth-number">
                    Total saved: {formatBytes(overallStats.totalSaved)}
                  </p>
                </div>
              )}
              
              {currentTab?.id === 'service' && (
                <div className="mt-3 text-center">
                  <p className="text-xs text-gray-400 smooth-number">
                    Total: {formatBytes(chartData.reduce((sum, d) => sum + d.value, 0))}
                  </p>
                </div>
              )}
            </>
          ) : (
            <div className="flex-1 flex items-center justify-center">
              <p className="text-gray-500">No data available for selected time range</p>
            </div>
          )}
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