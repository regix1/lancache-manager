import React, { useMemo, memo } from 'react';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { CacheInfoTooltip } from '../common/Tooltip';
import { Card } from '../ui/Card';

interface TopClientsTableProps {
  clientStats?: any[];
  downloads?: any[];
  timeRange?: string;
  customStartDate?: Date | null;
  customEndDate?: Date | null;
}

const TopClientsTable: React.FC<TopClientsTableProps> = memo(
  ({ clientStats = [], timeRange = 'live', customStartDate, customEndDate }) => {
    const timeRangeLabel = useMemo(() => {
      if (timeRange === 'custom' && customStartDate && customEndDate) {
        const options: Intl.DateTimeFormatOptions = { month: 'short', day: 'numeric' };
        const start = customStartDate.toLocaleDateString(undefined, options);
        const end = customEndDate.toLocaleDateString(undefined, options);
        return `${start} - ${end}`;
      }

      const labels: Record<string, string> = {
        '15m': 'Last 15 Minutes',
        '30m': 'Last 30 Minutes',
        '1h': 'Last Hour',
        '6h': 'Last 6 Hours',
        '12h': 'Last 12 Hours',
        '24h': 'Last 24 Hours',
        '7d': 'Last 7 Days',
        '30d': 'Last 30 Days',
        '90d': 'Last 90 Days',
        live: 'Live Data'
      };
      return labels[timeRange] || 'Live Data';
    }, [timeRange, customStartDate, customEndDate]);

    const displayClients = useMemo(() => clientStats.slice(0, 10), [clientStats]);

    return (
      <Card>
        <div className="flex items-center justify-between mb-4">
          <h3 className="text-lg font-semibold text-themed-primary flex items-center gap-2">
            Top Clients
            <CacheInfoTooltip />
          </h3>
          <span className="text-xs text-themed-muted">{timeRangeLabel}</span>
        </div>

        {displayClients.length > 0 ? (
          <div className="overflow-x-auto -mx-2 px-2">
            <table className="w-full min-w-[600px]">
              <thead>
                <tr className="text-left text-xs text-themed-muted uppercase tracking-wider">
                  <th className="pb-3 min-w-[120px]">Client IP</th>
                  <th className="pb-3 hidden sm:table-cell">Total Downloaded</th>
                  <th className="pb-3 hidden md:table-cell">Cache Hits</th>
                  <th className="pb-3 hidden md:table-cell">Cache Misses</th>
                  <th className="pb-3 min-w-[100px]">Hit Rate</th>
                  <th className="pb-3 hidden lg:table-cell">Last Seen</th>
                </tr>
              </thead>
              <tbody className="text-sm">
                {displayClients.map((client, idx) => (
                  <tr
                    key={`${client.clientIp}-${idx}`}
                    className="hover:bg-themed-hover transition-colors"
                  >
                    <td className="py-3 text-themed-primary whitespace-nowrap">{client.clientIp}</td>
                    <td className="py-3 text-themed-secondary hidden sm:table-cell whitespace-nowrap">{formatBytes(client.totalBytes)}</td>
                    <td className="py-3 cache-hit hidden md:table-cell whitespace-nowrap">{formatBytes(client.totalCacheHitBytes)}</td>
                    <td className="py-3 cache-miss hidden md:table-cell whitespace-nowrap">{formatBytes(client.totalCacheMissBytes)}</td>
                    <td className="py-3">
                      <span
                        className={`px-2 py-1 rounded text-xs hit-rate-badge ${
                          client.cacheHitPercent > 50 ? 'high' : 'warning'
                        }`}
                      >
                        {formatPercent(client.cacheHitPercent)}
                      </span>
                    </td>
                    <td className="py-3 text-themed-muted hidden lg:table-cell whitespace-nowrap">{formatDateTime(client.lastSeen)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="flex items-center justify-center h-32 text-themed-muted">
            No client data available for selected time range
          </div>
        )}
      </Card>
    );
  }
);

TopClientsTable.displayName = 'TopClientsTable';

export default TopClientsTable;
