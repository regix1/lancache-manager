import React from 'react';
import { useStats } from '@contexts/StatsContext';
import { formatBytes, formatPercent } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { Card } from '@components/ui/Card';
import { CacheInfoTooltip } from '@components/ui/Tooltip';

interface ClientRowProps {
  client: {
    clientIp: string;
    totalDownloads: number;
    totalBytes: number;
    totalCacheHitBytes: number;
    totalCacheMissBytes: number;
    cacheHitPercent: number;
    lastActivityUtc: string;
  };
}

const ClientRow: React.FC<ClientRowProps> = ({ client }) => {
  const formattedLastActivity = useFormattedDateTime(client.lastActivityUtc);

  return (
    <tr className="hover:bg-themed-hover transition-colors">
      <td className="py-3 text-themed-primary font-medium text-sm whitespace-nowrap">
        {client.clientIp}
      </td>
      <td className="py-3 text-themed-secondary hidden sm:table-cell whitespace-nowrap">
        {client.totalDownloads}
      </td>
      <td className="py-3 text-themed-secondary text-sm whitespace-nowrap">
        {formatBytes(client.totalBytes)}
      </td>
      <td className="py-3 cache-hit hidden md:table-cell text-sm whitespace-nowrap">
        {formatBytes(client.totalCacheHitBytes)}
      </td>
      <td className="py-3 cache-miss hidden md:table-cell text-sm whitespace-nowrap">
        {formatBytes(client.totalCacheMissBytes)}
      </td>
      <td className="py-3">
        <div className="flex flex-col sm:flex-row sm:items-center space-y-1 sm:space-y-0 sm:space-x-2">
          <div className="w-full sm:w-16 lg:w-24 progress-track rounded-full h-2">
            <div
              className="progress-bar-high h-2 rounded-full"
              style={{ width: `${client.cacheHitPercent}%` }}
            />
          </div>
          <span className="text-xs text-themed-secondary">
            {formatPercent(client.cacheHitPercent)}
          </span>
        </div>
      </td>
      <td className="py-3 text-themed-muted text-xs hidden lg:table-cell whitespace-nowrap">
        {formattedLastActivity}
      </td>
    </tr>
  );
};

const ClientsTab: React.FC = () => {
  const { clientStats } = useStats();

  return (
    <div className="space-y-6">
      <h2 className="text-xl sm:text-2xl font-bold text-themed-primary tracking-tight hidden md:block">
        Clients
      </h2>

      <Card>
        <h3 className="text-lg font-semibold mb-4 flex items-center gap-2 text-themed-primary">
          Client Statistics
          <CacheInfoTooltip />
        </h3>

        <div className="overflow-x-auto -mx-2 px-2">
          <table className="w-full mobile-table min-w-[600px]">
            <thead>
              <tr className="text-left text-xs text-themed-muted uppercase tracking-wider">
                <th className="pb-3 min-w-[120px]">Client IP</th>
                <th className="pb-3 hidden sm:table-cell">Total Downloads</th>
                <th className="pb-3 min-w-[80px]">Total Data</th>
                <th className="pb-3 hidden md:table-cell">Cache Hits</th>
                <th className="pb-3 hidden md:table-cell">Cache Misses</th>
                <th className="pb-3 min-w-[100px]">Hit Rate</th>
                <th className="pb-3 hidden lg:table-cell">Last Activity</th>
              </tr>
            </thead>
            <tbody className="text-sm">
              {clientStats.length > 0 ? (
                clientStats.map((client, idx) => (
                  <ClientRow key={idx} client={client} />
                ))
              ) : (
                <tr>
                  <td colSpan={7} className="py-8 text-center text-themed-muted">
                    No client data available
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
};

export default ClientsTab;
