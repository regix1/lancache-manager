import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';

const ServicesTab = () => {
  const { serviceStats } = useData();

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        {serviceStats.map((service, idx) => (
          <div key={idx} className="bg-gray-800 rounded-lg p-6 border border-gray-700">
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-lg font-semibold capitalize">{service.service}</h3>
              <span className="px-2 py-1 bg-blue-900 text-blue-300 rounded text-xs">
                {service.totalDownloads} downloads
              </span>
            </div>
            <div className="space-y-3">
              <div>
                <div className="flex justify-between text-sm mb-1">
                  <span className="text-gray-400">Total Data</span>
                  <span className="text-white font-medium">{formatBytes(service.totalBytes)}</span>
                </div>
              </div>
              <div>
                <div className="flex justify-between text-sm mb-1">
                  <span className="text-gray-400">Cache Hit Rate</span>
                  <span className="text-white font-medium">{formatPercent(service.cacheHitPercent)}</span>
                </div>
                <div className="w-full bg-gray-700 rounded-full h-2">
                  <div 
                    className="bg-gradient-to-r from-green-500 to-green-600 h-2 rounded-full"
                    style={{ width: `${service.cacheHitPercent}%` }}
                  />
                </div>
              </div>
              <div className="pt-2 border-t border-gray-700">
                <div className="text-xs text-gray-400">
                  Last Activity: {formatDateTime(service.lastActivity)}
                </div>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

export default ServicesTab;