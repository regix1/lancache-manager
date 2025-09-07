import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { Card } from '../ui/Card';
import { CacheInfoTooltip } from '../common/Tooltip';

const ServicesTab: React.FC = () => {
  const { serviceStats } = useData();

  const getServiceColor = (service: string): string => {
    const colors: Record<string, string> = {
      steam: 'text-blue-400',
      epic: 'text-purple-400',
      origin: 'text-orange-400',
      blizzard: 'text-cyan-400',
      wsus: 'text-green-400',
      riot: 'text-red-400'
    };
    return colors[service.toLowerCase()] || 'text-gray-400';
  };

  return (
    <Card>
      <h2 className="text-xl font-semibold mb-4 flex items-center gap-2">
        Service Statistics
        <CacheInfoTooltip />
      </h2>
      
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className="text-left text-xs text-gray-400 uppercase tracking-wider border-b border-gray-700">
              <th className="pb-3">Service</th>
              <th className="pb-3">Total Downloads</th>
              <th className="pb-3">Total Data</th>
              <th className="pb-3">Cache Hits</th>
              <th className="pb-3">Cache Misses</th>
              <th className="pb-3">Hit Rate</th>
              <th className="pb-3">Last Activity</th>
            </tr>
          </thead>
          <tbody className="text-sm">
            {serviceStats.length > 0 ? (
              serviceStats.map((service, idx) => (
                <tr key={idx} className="border-t border-gray-700">
                  <td className={`py-3 font-medium ${getServiceColor(service.service)}`}>
                    {service.service}
                  </td>
                  <td className="py-3">{service.totalDownloads}</td>
                  <td className="py-3">{formatBytes(service.totalBytes)}</td>
                  <td className="py-3 text-green-400">{formatBytes(service.totalCacheHitBytes)}</td>
                  <td className="py-3 text-yellow-400">{formatBytes(service.totalCacheMissBytes)}</td>
                  <td className="py-3">
                    <div className="flex items-center space-x-2">
                      <div className="w-24 bg-gray-700 rounded-full h-2">
                        <div 
                          className="bg-green-500 h-2 rounded-full"
                          style={{ width: `${service.cacheHitPercent}%` }}
                        />
                      </div>
                      <span className="text-xs">{formatPercent(service.cacheHitPercent)}</span>
                    </div>
                  </td>
                  <td className="py-3 text-gray-400">
                    {formatDateTime(service.lastActivity)}
                  </td>
                </tr>
              ))
            ) : (
              <tr>
                <td colSpan={7} className="py-8 text-center text-gray-500">
                  No service data available
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </Card>
  );
};

export default ServicesTab;