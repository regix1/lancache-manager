import React, { useEffect, useRef, useState, useMemo } from 'react';
import { ChevronLeft, ChevronRight, Maximize2, Minimize2 } from 'lucide-react';
import { formatBytes } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';
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

  const tabs = [
    { name: 'Service Distribution', id: 'service' },
    { name: 'Cache Hit Ratio', id: 'hit-ratio' },
    { name: 'Bandwidth Saved', id: 'bandwidth' }
  ];

  const getServiceDistributionData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return { labels: [], data: [], colors: [] };
    
    const totalBytes = serviceStats.reduce((sum, s) => sum + (s.totalBytes || 0), 0);
    if (totalBytes === 0) return { labels: [], data: [], colors: [] };
    
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
      colors: sorted.map((_, i) => CHART_COLORS[i % CHART_COLORS.length])
    };
  }, [serviceStats]);

  const getCacheHitRatioData = useMemo(() => {
    if (!serviceStats || serviceStats.length === 0) return { labels: [], data: [], colors: [] };
    
    const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
    const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
    const total = totalHits + totalMisses;
    
    if (total === 0) return { labels: [], data: [], colors: [] };
    
    return {
      labels: ['Cache Hits', 'Cache Misses'],
      data: [totalHits, totalMisses],
      colors: ['#10b981', '#f59e0b']
    };
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

  useEffect(() => {
    if (!chartRef.current || chartData.labels.length === 0) return;

    // Destroy existing chart
    if (chartInstance.current) {
      chartInstance.current.destroy();
    }

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
          borderColor: '#1f2937',
          borderWidth: 2
        }]
      },
      options: {
        responsive: true,
        maintainAspectRatio: true,
        plugins: {
          legend: {
            display: false
          },
          tooltip: {
            callbacks: {
              label: (context) => {
                const value = context.raw as number;
                const total = context.dataset.data.reduce((a, b) => (a as number) + (b as number), 0) as number;
                const percentage = ((value / total) * 100).toFixed(1);
                return `${context.label}: ${formatBytes(value)} (${percentage}%)`;
              }
            }
          }
        }
      }
    });

    return () => {
      if (chartInstance.current) {
        chartInstance.current.destroy();
      }
    };
  }, [chartData, chartSize]);

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
            <div className="flex justify-center" style={{ height: `${200 + (chartSize - 100) * 2}px` }}>
              <canvas 
                ref={chartRef}
                style={{ 
                  maxHeight: '100%',
                  maxWidth: '100%'
                }}
              />
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