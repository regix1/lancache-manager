import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { Card } from '../ui/Card';
import { CacheInfoTooltip } from '../common/Tooltip';

const ServicesTab: React.FC = () => {
  const { serviceStats } = useData();

  const getServiceColor = (service: string): string => {
    const colors: Record<string, string> = {
      steam: 'service-steam',
      epic: 'service-epic',
      origin: 'service-origin',
      blizzard: 'service-blizzard',
      wsus: 'service-wsus',
      riot: 'service-riot'
    };
    return colors[service.toLowerCase()] || 'text-themed-muted';
  };

  return (
    <Card>
      <h2 className="text-xl font-semibold mb-4 flex items-center gap-2 text-themed-primary">
        Service Statistics
        <CacheInfoTooltip />
      </h2>

      <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className="text-left text-xs text-themed-muted uppercase tracking-wider border-b border-themed-secondary">
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
                <tr key={idx} className="border-t border-themed-secondary">
                  <td className={`py-3 font-medium ${getServiceColor(service.service)}`}>
                    {service.service}
                  </td>
                  <td className="py-3 text-themed-secondary">{service.totalDownloads}</td>
                  <td className="py-3 text-themed-secondary">{formatBytes(service.totalBytes)}</td>
                  <td className="py-3 cache-hit">{formatBytes(service.totalCacheHitBytes)}</td>
                  <td className="py-3 cache-miss">{formatBytes(service.totalCacheMissBytes)}</td>
                  <td className="py-3">
                    <div className="flex items-center space-x-2">
                      <div className="w-24 progress-track rounded-full h-2">
                        <div
                          className="progress-bar-high h-2 rounded-full"
                          style={{ width: `${service.cacheHitPercent}%` }}
                        />
                      </div>
                      <span className="text-xs text-themed-secondary">
                        {formatPercent(service.cacheHitPercent)}
                      </span>
                    </div>
                  </td>
                  <td className="py-3 text-themed-muted">{formatDateTime(service.lastActivity)}</td>
                </tr>
              ))
            ) : (
              <tr>
                <td colSpan={7} className="py-8 text-center text-themed-muted">
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
