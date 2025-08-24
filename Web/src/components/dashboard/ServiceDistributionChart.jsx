import React from 'react';
import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer } from 'recharts';
import { useData } from '../../contexts/DataContext';
import { formatBytes } from '../../utils/formatters';
import { CHART_COLORS } from '../../utils/constants';

const ServiceDistributionChart = () => {
  const { serviceStats } = useData();

  const serviceChartData = serviceStats.map(s => ({
    name: s.service,
    value: s.totalBytes,
    hitRate: s.cacheHitPercent
  }));

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <h3 className="text-lg font-semibold text-white mb-4">Service Distribution</h3>
      {serviceChartData.length > 0 ? (
        <ResponsiveContainer width="100%" height={300}>
          <PieChart>
            <Pie
              data={serviceChartData}
              cx="50%"
              cy="50%"
              labelLine={false}
              label={({ name, percent }) => `${name} ${(percent * 100).toFixed(0)}%`}
              outerRadius={80}
              fill="#8884d8"
              dataKey="value"
            >
              {serviceChartData.map((entry, index) => (
                <Cell key={`cell-${index}`} fill={CHART_COLORS[index % CHART_COLORS.length]} />
              ))}
            </Pie>
            <Tooltip formatter={(value) => formatBytes(value)} />
          </PieChart>
        </ResponsiveContainer>
      ) : (
        <div className="flex items-center justify-center h-[300px] text-gray-500">
          No data available
        </div>
      )}
    </div>
  );
};

export default ServiceDistributionChart;