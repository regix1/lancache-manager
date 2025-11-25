import React, { useMemo, memo, useState } from 'react';
import { formatBytes, formatPercent } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { CacheInfoTooltip } from '@components/ui/Tooltip';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';

interface TopClientsTableProps {
  clientStats?: any[];
  downloads?: any[];
  timeRange?: string;
  customStartDate?: Date | null;
  customEndDate?: Date | null;
}

type SortOption = 'total' | 'hits' | 'misses' | 'hitRate';

interface TopClientRowProps {
  client: {
    clientIp: string;
    totalBytes: number;
    totalCacheHitBytes: number;
    totalCacheMissBytes: number;
    cacheHitPercent: number;
    lastActivityUtc: string;
  };
}

const TopClientRow: React.FC<TopClientRowProps> = ({ client }) => {
  const formattedLastActivity = useFormattedDateTime(client.lastActivityUtc);

  return (
    <tr className="hover:bg-themed-hover transition-colors">
      <td className="py-3 text-themed-primary whitespace-nowrap">
        {client.clientIp}
      </td>
      <td className="py-3 text-themed-secondary hidden sm:table-cell whitespace-nowrap">
        {formatBytes(client.totalBytes)}
      </td>
      <td className="py-3 cache-hit hidden md:table-cell whitespace-nowrap">
        {formatBytes(client.totalCacheHitBytes)}
      </td>
      <td className="py-3 cache-miss hidden md:table-cell whitespace-nowrap">
        {formatBytes(client.totalCacheMissBytes)}
      </td>
      <td className="py-3">
        <span
          className={`px-2 py-1 rounded text-xs hit-rate-badge whitespace-nowrap ${
            client.cacheHitPercent > 50 ? 'high' : 'warning'
          }`}
        >
          {formatPercent(client.cacheHitPercent)}
        </span>
      </td>
      <td className="py-3 text-themed-muted hidden lg:table-cell whitespace-nowrap">
        {formattedLastActivity}
      </td>
    </tr>
  );
};

const TopClientsTable: React.FC<TopClientsTableProps> = memo(
  ({ clientStats = [], timeRange = 'live', customStartDate, customEndDate }) => {
    const [sortBy, setSortBy] = useState<SortOption>('total');
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

    const sortedClients = useMemo(() => {
      const sorted = [...clientStats];

      switch (sortBy) {
        case 'total':
          sorted.sort((a, b) => (b.totalBytes || 0) - (a.totalBytes || 0));
          break;
        case 'hits':
          sorted.sort((a, b) => (b.totalCacheHitBytes || 0) - (a.totalCacheHitBytes || 0));
          break;
        case 'misses':
          sorted.sort((a, b) => (b.totalCacheMissBytes || 0) - (a.totalCacheMissBytes || 0));
          break;
        case 'hitRate':
          sorted.sort((a, b) => (b.cacheHitPercent || 0) - (a.cacheHitPercent || 0));
          break;
      }

      return sorted;
    }, [clientStats, sortBy]);

    const displayClients = useMemo(() => sortedClients.slice(0, 10), [sortedClients]);

    return (
      <Card>
        <div className="flex items-center justify-between mb-4 flex-wrap gap-2">
          <h3 className="text-lg font-semibold text-themed-primary flex items-center gap-2">
            Top Clients
            <CacheInfoTooltip />
          </h3>
          <div className="flex items-center gap-2">
            <EnhancedDropdown
              options={[
                { value: 'total', label: 'Total Downloaded' },
                { value: 'hits', label: 'Cache Hits' },
                { value: 'misses', label: 'Cache Misses' },
                { value: 'hitRate', label: 'Hit Rate' }
              ]}
              value={sortBy}
              onChange={(value) => setSortBy(value as SortOption)}
              className="w-48"
            />
            <span className="text-xs text-themed-muted whitespace-nowrap">{timeRangeLabel}</span>
          </div>
        </div>

        {displayClients.length > 0 ? (
          <div className="overflow-x-auto -mx-2 px-2">
            <table className="w-full">
              <thead>
                <tr className="text-left text-xs text-themed-muted uppercase tracking-wider">
                  <th className="pb-3">Client IP</th>
                  <th className="pb-3 hidden sm:table-cell">Total</th>
                  <th className="pb-3 hidden md:table-cell">Hits</th>
                  <th className="pb-3 hidden md:table-cell">Misses</th>
                  <th className="pb-3">Hit Rate</th>
                  <th className="pb-3 hidden lg:table-cell">Last Seen</th>
                </tr>
              </thead>
              <tbody className="text-sm">
                {displayClients.map((client, idx) => (
                  <TopClientRow key={`${client.clientIp}-${idx}`} client={client} />
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
  },
  (prevProps, nextProps) => {
    // Only re-render if clientStats or timeRange changed
    return (
      JSON.stringify(prevProps.clientStats) === JSON.stringify(nextProps.clientStats) &&
      prevProps.timeRange === nextProps.timeRange &&
      prevProps.customStartDate?.getTime() === nextProps.customStartDate?.getTime() &&
      prevProps.customEndDate?.getTime() === nextProps.customEndDate?.getTime()
    );
  }
);

TopClientsTable.displayName = 'TopClientsTable';

export default TopClientsTable;
