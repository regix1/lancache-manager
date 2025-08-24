import React from 'react';
import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer, Legend } from 'recharts';
import { useData } from '../../contexts/DataContext';
import { formatBytes } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';

const ServiceDistributionChart = () => {
  const { serviceStats } = useData();

  // Calculate total bytes across all services
  const totalBytes = serviceStats.reduce((sum, s) => sum + s.totalBytes, 0);

  // Filter services and group small ones
  const processedData = [];
  let otherBytes = 0;
  let otherCount = 0;
  const otherServices = [];

  serviceStats.forEach(s => {
    const percentage = (s.totalBytes / totalBytes) * 100;
    if (percentage > 5) {
      processedData.push({
        name: s.service,
        value: s.totalBytes,
        hitRate: s.cacheHitPercent,
        percentage: percentage
      });
    } else if (s.totalBytes > 0) {
      otherBytes += s.totalBytes;
      otherCount++;
      otherServices.push(s.service);
    }
  });

  // Add "Other" category if there are small services
  if (otherCount > 0 && otherBytes > 0) {
    const otherPercentage = (otherBytes / totalBytes) * 100;
    processedData.push({
      name: otherCount === 1 ? otherServices[0] : `Other (${otherCount} services)`,
      value: otherBytes,
      hitRate: null,
      percentage: otherPercentage,
      tooltip: otherCount > 1 ? `Includes: ${otherServices.join(', ')}` : null
    });
  }

  // Sort by value descending
  const serviceChartData = processedData.sort((a, b) => b.value - a.value);

  // Custom tooltip to show additional info
  const CustomTooltip = ({ active, payload }) => {
    if (active && payload && payload[0]) {
      const data = payload[0].payload;
      return (
        <div className="bg-gray-800 p-3 rounded-lg border border-gray-600 shadow-lg">
          <p className="text-white font-medium">{data.name}</p>
          <p className="text-gray-300">{formatBytes(data.value)}</p>
          <p className="text-gray-400">{data.percentage.toFixed(1)}%</p>
          {data.hitRate !== null && (
            <p className="text-green-400">Cache Hit: {data.hitRate.toFixed(1)}%</p>
          )}
          {data.tooltip && (
            <p className="text-xs text-gray-500 mt-1">{data.tooltip}</p>
          )}
        </div>
      );
    }
    return null;
  };

  // Custom label function
  const renderLabel = ({ name, value, percent, cx, cy, midAngle, innerRadius, outerRadius, index }) => {
    // Don't show label for very small slices
    if (percent < 0.03) return null;
    
    const RADIAN = Math.PI / 180;
    const radius = outerRadius + 25;
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
  };

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <h3 className="text-lg font-semibold text-white mb-4">Service Distribution</h3>
      {serviceChartData.length > 0 ? (
        <>
          <ResponsiveContainer width="100%" height={300}>
            <PieChart>
              <Pie
                data={serviceChartData}
                cx="50%"
                cy="50%"
                labelLine={false}
                label={renderLabel}
                outerRadius={80}
                fill="#8884d8"
                dataKey="value"
              >
                {serviceChartData.map((entry, index) => (
                  <Cell 
                    key={`cell-${index}`} 
                    fill={CHART_COLORS[index % CHART_COLORS.length]} 
                  />
                ))}
              </Pie>
              <Tooltip content={<CustomTooltip />} />
            </PieChart>
          </ResponsiveContainer>
          
          {/* Legend showing only services over 5% */}
          <div className="mt-4 flex flex-wrap justify-center gap-3">
            {serviceChartData.map((entry, index) => (
              <div key={entry.name} className="flex items-center space-x-2">
                <div 
                  className="w-3 h-3 rounded"
                  style={{ backgroundColor: CHART_COLORS[index % CHART_COLORS.length] }}
                />
                <span className="text-xs text-gray-400">
                  {entry.name}: {entry.percentage.toFixed(1)}%
                </span>
              </div>
            ))}
          </div>
        </>
      ) : (
        <div className="flex items-center justify-center h-[300px] text-gray-500">
          No data available
        </div>
      )}
    </div>
  );
};

export default ServiceDistributionChart;