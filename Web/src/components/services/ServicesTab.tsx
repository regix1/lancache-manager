import React from 'react';
import { useStats } from '../../contexts/StatsContext';
import { formatBytes, formatPercent } from '../../utils/formatters';
import { Card } from '../ui/Card';
import { CacheInfoTooltip } from '@components/ui/Tooltip';
import { getServiceColorClass } from '../../utils/serviceColors';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';

interface ServiceRowProps {
  service: {
    service: string;
    totalDownloads: number;
    totalBytes: number;
    totalCacheHitBytes: number;
    totalCacheMissBytes: number;
    cacheHitPercent: number;
    lastActivityUtc: string;
  };
}

const ServiceRow: React.FC<ServiceRowProps> = ({ service }) => {
  const formattedLastActivity = useFormattedDateTime(service.lastActivityUtc);

  return (
    <tr className="border-t border-themed-secondary">
      <td
        className={`py-3 font-medium text-sm ${getServiceColorClass(service.service)}`}
      >
        {service.service}
      </td>
      <td className="py-3 text-themed-secondary hidden sm:table-cell">
        {service.totalDownloads}
      </td>
      <td className="py-3 text-themed-secondary text-sm">
        {formatBytes(service.totalBytes)}
      </td>
      <td className="py-3 cache-hit hidden md:table-cell text-sm">
        {formatBytes(service.totalCacheHitBytes)}
      </td>
      <td className="py-3 cache-miss hidden md:table-cell text-sm">
        {formatBytes(service.totalCacheMissBytes)}
      </td>
      <td className="py-3">
        <div className="flex flex-col sm:flex-row sm:items-center space-y-1 sm:space-y-0 sm:space-x-2">
          <div className="w-full sm:w-16 lg:w-24 progress-track rounded-full h-2">
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
      <td className="py-3 text-themed-muted text-xs hidden lg:table-cell">
        {formattedLastActivity}
      </td>
    </tr>
  );
};

const ServicesTab: React.FC = () => {
  const { serviceStats } = useStats();

  return (
    <Card>
      <h2 className="text-lg sm:text-xl font-semibold mb-4 flex items-center gap-2 text-themed-primary">
        Service Statistics
        <CacheInfoTooltip />
      </h2>

      <div className="overflow-x-auto">
        <table className="w-full mobile-table">
          <thead>
            <tr className="text-left text-xs text-themed-muted uppercase tracking-wider border-b border-themed-secondary">
              <th className="pb-3 min-w-[80px]">Service</th>
              <th className="pb-3 hidden sm:table-cell">Total Downloads</th>
              <th className="pb-3 min-w-[80px]">Total Data</th>
              <th className="pb-3 hidden md:table-cell">Cache Hits</th>
              <th className="pb-3 hidden md:table-cell">Cache Misses</th>
              <th className="pb-3 min-w-[100px]">Hit Rate</th>
              <th className="pb-3 hidden lg:table-cell">Last Activity</th>
            </tr>
          </thead>
          <tbody className="text-sm">
            {serviceStats.length > 0 ? (
              serviceStats.map((service, idx) => (
                <ServiceRow key={idx} service={service} />
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
