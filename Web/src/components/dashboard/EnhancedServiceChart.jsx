import React, { useState, useEffect } from 'react';
import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer, Legend } from 'recharts';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2 } from 'lucide-react';

const EnhancedServiceChart = ({ dashboardStats }) => {
  const { serviceStats, clientStats, mockMode } = useData();
  const [activeTab, setActiveTab] = useState(0);
  const [chartSize, setChartSize] = useState(100); // Percentage of default size
  const [cacheEffectiveness, setCacheEffectiveness] = useState(null);
  const [timelineData, setTimelineData] = useState(null);

  // Fetch cache effectiveness and timeline data
  useEffect(() => {
    if (!mockMode) {
      fetchCacheEffectiveness();
      fetchTimelineData();
      const interval = setInterval(() => {
        fetchCacheEffectiveness();
        fetchTimelineData();
      }, 30000);
      return () => clearInterval(interval);
    } else {
      // Set mock cache effectiveness data
      setCacheEffectiveness({
        overall: {
          hitBytes: 875000000000,
          missBytes: 125000000000,
          hitPercentage: 87.5,
          bandwidthSaved: 875000000000
        }
      });
      // Set mock timeline data
      setTimelineData([
        { hour: '18:00', totalBytes: 150000000000 },
        { hour: '19:00', totalBytes: 180000000000 },
        { hour: '20:00', totalBytes: 200000000000 },
        { hour: '21:00', totalBytes: 220000000000 }
      ]);
    }
  }, [mockMode]);

  const fetchCacheEffectiveness = async () => {
    try {
      const apiUrl = import.meta.env.VITE_API_URL || '';
      const response = await fetch(`${apiUrl}/api/stats/cache-effectiveness?period=24h`);
      if (response.ok) {
        const data = await response.json();
        setCacheEffectiveness(data);
      }
    } catch (error) {
      console.error('Failed to fetch cache effectiveness:', error);
    }
  };

  const fetchTimelineData = async () => {
    try {
      const apiUrl = import.meta.env.VITE_API_URL || '';
      const response = await fetch(`${apiUrl}/api/stats/timeline?period=24h&interval=hourly`);
      if (response.ok) {
        const data = await response.json();
        setTimelineData(data.timeline || []);
      }
    } catch (error) {
      console.error('Failed to fetch timeline data:', error);
    }
  };

  // Define all tabs - Hourly Activity available for both real and mock data
  const tabs = [
    { name: 'Service Distribution', id: 'service' },
    { name: 'Cache Hit Ratio', id: 'hit-ratio' },
    { name: 'Bandwidth Saved', id: 'bandwidth' },
    { name: 'Top Services', id: 'top-services' },
    { name: 'Hourly Activity', id: 'hourly' }
  ];

  const handlePrevTab = () => {
    setActiveTab(prev => (prev - 1 + tabs.length) % tabs.length);
  };

  const handleNextTab = () => {
    setActiveTab(prev => (prev + 1) % tabs.length);
  };

  const adjustSize = (delta) => {
    setChartSize(prev => Math.max(60, Math.min(120, prev + delta)));
  };

  // Mock data generators
  const getMockServiceDistributionData = () => {
    return [
      { name: 'Steam', value: 450000000000, percentage: 45 },
      { name: 'Epic Games', value: 250000000000, percentage: 25 },
      { name: 'Battle.net', value: 150000000000, percentage: 15 },
      { name: 'Origin', value: 100000000000, percentage: 10 },
      { name: 'Other (3)', value: 50000000000, percentage: 5 }
    ];
  };

  const getMockCacheHitRatioData = () => {
    return [
      { name: 'Cache Hits', value: 875000000000, percentage: 87.5 },
      { name: 'Cache Misses', value: 125000000000, percentage: 12.5 }
    ];
  };

  const getMockBandwidthSavedData = () => {
    return [
      { name: 'Steam', value: 380000000000, percentage: 84.4 },
      { name: 'Epic Games', value: 187500000000, percentage: 75.0 },
      { name: 'Battle.net', value: 135000000000, percentage: 90.0 },
      { name: 'Origin', value: 72500000000, percentage: 72.5 },
      { name: 'Riot', value: 100000000000, percentage: 95.0 }
    ];
  };

  const getMockTopServicesData = () => {
    return [
      { name: 'Steam', value: 450000000000, percentage: 84.4 },
      { name: 'Epic Games', value: 250000000000, percentage: 75.0 },
      { name: 'Battle.net', value: 150000000000, percentage: 90.0 },
      { name: 'Origin', value: 100000000000, percentage: 72.5 },
      { name: 'Riot', value: 50000000000, percentage: 95.0 }
    ];
  };

  // Process data for Hourly Activity
  const getHourlyActivityData = () => {
    if (mockMode) {
      // Mock data for demonstration
      const peakBytes = 750000000000;
      const offPeakBytes = 250000000000;
      
      return [
        { name: 'Peak Hours', value: peakBytes, percentage: 75 },
        { name: 'Off-Peak Hours', value: offPeakBytes, percentage: 25 }
      ];
    }
    
    // For real data, check if we have timeline data from the API
    if (timelineData && timelineData.length > 0) {
      // Process real timeline data
      const now = new Date();
      const currentHour = now.getHours();
      
      let peakBytes = 0;
      let offPeakBytes = 0;
      
      timelineData.forEach((point, index) => {
        const hour = (currentHour - (23 - index) + 24) % 24;
        const isPeak = (hour >= 18 && hour <= 23) || (hour >= 12 && hour <= 14);
        
        if (isPeak) {
          peakBytes += point.totalBytes || 0;
        } else {
          offPeakBytes += point.totalBytes || 0;
        }
      });
      
      const total = peakBytes + offPeakBytes;
      if (total === 0) return [];
      
      return [
        { name: 'Peak Hours', value: peakBytes, percentage: (peakBytes / total) * 100 },
        { name: 'Off-Peak Hours', value: offPeakBytes, percentage: (offPeakBytes / total) * 100 }
      ];
    }
    
    // No data available
    return [];
  };

  // Process real data for Service Distribution
  const getServiceDistributionData = () => {
    if (mockMode) return getMockServiceDistributionData();
    
    const totalBytes = serviceStats.reduce((sum, s) => sum + s.totalBytes, 0);
    if (totalBytes === 0) return [];
    
    const processedData = [];
    let otherBytes = 0;
    let otherCount = 0;

    serviceStats.forEach(s => {
      const percentage = (s.totalBytes / totalBytes) * 100;
      if (percentage > 5) {
        processedData.push({
          name: s.service,
          value: s.totalBytes,
          percentage: percentage
        });
      } else if (s.totalBytes > 0) {
        otherBytes += s.totalBytes;
        otherCount++;
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
  };

  // Process data for Cache Hit Ratio
  const getCacheHitRatioData = () => {
    if (mockMode) return getMockCacheHitRatioData();
    
    if (cacheEffectiveness?.overall) {
      const { hitBytes, missBytes } = cacheEffectiveness.overall;
      return [
        { name: 'Cache Hits', value: hitBytes, percentage: cacheEffectiveness.overall.hitPercentage },
        { name: 'Cache Misses', value: missBytes, percentage: 100 - cacheEffectiveness.overall.hitPercentage }
      ];
    }
    
    // Fallback to service stats
    const totalHits = serviceStats.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
    const totalMisses = serviceStats.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
    const total = totalHits + totalMisses;
    
    if (total === 0) return [];
    
    return [
      { name: 'Cache Hits', value: totalHits, percentage: (totalHits / total) * 100 },
      { name: 'Cache Misses', value: totalMisses, percentage: (totalMisses / total) * 100 }
    ];
  };

  // Process data for Bandwidth Saved
  const getBandwidthSavedData = () => {
    if (mockMode) return getMockBandwidthSavedData();
    
    if (dashboardStats?.serviceBreakdown) {
      return dashboardStats.serviceBreakdown.map(s => ({
        name: s.service,
        value: s.bytes * 0.8, // Assume 80% hit rate for saved bandwidth
        percentage: s.percentage * 0.8
      }));
    }
    
    return serviceStats.map(s => ({
      name: s.service,
      value: s.totalCacheHitBytes,
      percentage: s.cacheHitPercent
    })).filter(s => s.value > 0).sort((a, b) => b.value - a.value);
  };

  // Process data for Top Services (top 5 only)
  const getTopServicesData = () => {
    if (mockMode) return getMockTopServicesData();
    
    return serviceStats
      .filter(s => s.totalBytes > 0)
      .sort((a, b) => b.totalBytes - a.totalBytes)
      .slice(0, 5)
      .map(s => ({
        name: s.service,
        value: s.totalBytes,
        percentage: s.cacheHitPercent
      }));
  };

  // Get data based on active tab
  const getChartData = () => {
    const currentTab = tabs[activeTab];
    if (!currentTab) return [];
    
    switch(currentTab.id) {
      case 'service':
        return getServiceDistributionData();
      case 'hit-ratio':
        return getCacheHitRatioData();
      case 'bandwidth':
        return getBandwidthSavedData();
      case 'top-services':
        return getTopServicesData();
      case 'hourly':
        return getHourlyActivityData();
      default:
        return getServiceDistributionData();
    }
  };

  // Custom tooltip
  const CustomTooltip = ({ active, payload }) => {
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
  };

  // Custom label function
  const renderLabel = ({ name, value, percent, cx, cy, midAngle, innerRadius, outerRadius, index }) => {
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
  };

  const chartData = getChartData();
  const chartHeight = 300 * (chartSize / 100);
  const outerRadius = 80 * (chartSize / 100);
  const currentTab = tabs[activeTab];

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
          {currentTab?.id === 'hit-ratio' && cacheEffectiveness && (
            <div className="mt-4 pt-4 border-t border-gray-700">
              <div className="text-center">
                <p className="text-2xl font-bold text-green-400">
                  {formatPercent(cacheEffectiveness.overall?.hitPercentage || 0)}
                </p>
                <p className="text-xs text-gray-500">Overall Cache Hit Rate</p>
                <p className="text-sm text-gray-400 mt-1">
                  Saved {formatBytes(cacheEffectiveness.overall?.bandwidthSaved || 0)} of bandwidth
                </p>
              </div>
            </div>
          )}
        </>
      ) : (
        <div className="flex items-center justify-center" style={{ height: chartHeight }}>
          <p className="text-gray-500">No data available</p>
        </div>
      )}
    </div>
  );
};

export default EnhancedServiceChart;