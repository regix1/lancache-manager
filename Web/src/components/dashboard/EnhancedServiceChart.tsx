import React, { useState, useMemo } from 'react';
import * as Recharts from 'recharts';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2 } from 'lucide-react';
import { formatBytes } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';
import { Card } from '../ui/Card';

const { PieChart, Pie, Cell, Tooltip, ResponsiveContainer } = Recharts;

interface EnhancedServiceChartProps {
  serviceStats: any[];
  timeRange?: string;
}

const EnhancedServiceChart: React.FC<EnhancedServiceChartProps> = ({ 
  serviceStats
}) => {
  const [activeTab, setActiveTab] = useState(0);
  const [chartSize, setChartSize] = useState(100);

  const tabs = [
    { name: 'Service Distribution', id: 'service' },
    { name: 'Cache Hit Ratio', id: 'hit-ratio' },
    { name: 'Bandwidth Saved', id: 'bandwidth' }
  ];

  const getServiceDistributionData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return [];
    
    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    if (totalBytes === 0) return [];
    
    return serviceStats.map(s => ({
      name: s.service,
      value: s.totalBytes,
      percentage: (s.totalBytes / totalBytes) * 100
    })).sort((a, b) => b.value - a.value);
  }, [serviceStats]);

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

  const chartData = useMemo(() => {
    switch(tabs[activeTab]?.id) {
      case 'service':
        return getServiceDistributionData;
      case 'hit-ratio':
        return getCacheHitRatioData;
      case 'bandwidth':
        return getServiceDistributionData; // Simplified for now
      default:
        return getServiceDistributionData;
    }
  }, [activeTab, getServiceDistributionData, getCacheHitRatioData]);

  const CustomTooltip = ({ active, payload }: any) => {
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
        {chartData.length > 0 ? (
          <>
            <div style={{ height: `${200 + (chartSize - 100) * 2}px` }}>
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie
                    data={chartData}
                    cx="50%"
                    cy="50%"
                    outerRadius={65 + (chartSize - 100) * 0.3}
                    fill="#8884d8"
                    dataKey="value"
                  >
                    {chartData.map((_, index) => (
                      <Cell 
                        key={`cell-${index}`} 
                        fill={tabs[activeTab]?.id === 'hit-ratio' 
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

            <div className="mt-4 flex flex-wrap justify-center gap-3">
              {chartData.map((entry, index) => (
                <div key={entry.name} className="flex items-center space-x-1">
                  <div 
                    className="w-3 h-3 rounded"
                    style={{ 
                      backgroundColor: tabs[activeTab]?.id === 'hit-ratio' 
                        ? (index === 0 ? '#10b981' : '#f59e0b')
                        : CHART_COLORS[index % CHART_COLORS.length]
                    }}
                  />
                  <span className="text-xs text-gray-400">{entry.name}:</span>
                  <span className="text-xs text-white font-medium">{entry.percentage.toFixed(1)}%</span>
                </div>
              ))}
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