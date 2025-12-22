import React, { useState, useMemo } from 'react';
import { useStats } from '@contexts/StatsContext';
import { formatBytes, formatPercent, formatSpeed } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { Card } from '@components/ui/Card';
import { CacheInfoTooltip } from '@components/ui/Tooltip';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ArrowUpDown } from 'lucide-react';

type SortOption = 'ip' | 'downloads' | 'totalData' | 'hits' | 'misses' | 'hitRate' | 'avgSpeed' | 'lastActivity';
type SortDirection = 'asc' | 'desc';

interface ClientData {
  clientIp: string;
  totalDownloads: number;
  totalBytes: number;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  cacheHitPercent: number;
  lastActivityUtc: string;
  averageBytesPerSecond?: number;
}

interface ClientRowProps {
  client: ClientData;
}

// Check if speed is meaningful (not just total bytes / 1 second)
const isSpeedMeaningful = (speed: number | undefined, totalBytes: number): boolean => {
  if (!speed || speed <= 0) return false;
  // If speed is within 5% of total bytes, it's likely a ~1 second download
  const ratio = speed / totalBytes;
  return ratio < 0.95 || ratio > 1.05;
};

const ClientRow: React.FC<ClientRowProps> = ({ client }) => {
  const formattedLastActivity = useFormattedDateTime(client.lastActivityUtc);
  const showSpeed = isSpeedMeaningful(client.averageBytesPerSecond, client.totalBytes);

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
      <td className="py-3 text-themed-secondary text-sm hidden lg:table-cell whitespace-nowrap">
        {showSpeed ? formatSpeed(client.averageBytesPerSecond) : '-'}
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

// Mobile card layout for each client
const ClientCard: React.FC<ClientRowProps> = ({ client }) => {
  const formattedLastActivity = useFormattedDateTime(client.lastActivityUtc);
  const showSpeed = isSpeedMeaningful(client.averageBytesPerSecond, client.totalBytes);

  return (
    <div
      className="p-4 rounded-lg border"
      style={{
        backgroundColor: 'var(--theme-bg-secondary)',
        borderColor: 'var(--theme-border-primary)'
      }}
    >
      {/* Header: IP and Hit Rate */}
      <div className="flex items-center justify-between mb-3">
        <span className="text-themed-primary font-medium">{client.clientIp}</span>
        <div className="flex items-center gap-2">
          <div className="w-16 progress-track rounded-full h-2">
            <div
              className="progress-bar-high h-2 rounded-full"
              style={{ width: `${client.cacheHitPercent}%` }}
            />
          </div>
          <span className="text-xs text-themed-secondary font-medium">
            {formatPercent(client.cacheHitPercent)}
          </span>
        </div>
      </div>

      {/* Stats Grid */}
      <div className="grid grid-cols-2 gap-2 text-sm">
        <div>
          <span className="text-themed-muted text-xs">Total Data</span>
          <p className="text-themed-secondary">{formatBytes(client.totalBytes)}</p>
        </div>
        <div>
          <span className="text-themed-muted text-xs">Avg Download Speed</span>
          <p className="text-themed-secondary">{showSpeed ? formatSpeed(client.averageBytesPerSecond) : '-'}</p>
        </div>
        <div>
          <span className="text-themed-muted text-xs">Cache Hits</span>
          <p className="cache-hit">{formatBytes(client.totalCacheHitBytes)}</p>
        </div>
        <div>
          <span className="text-themed-muted text-xs">Cache Misses</span>
          <p className="cache-miss">{formatBytes(client.totalCacheMissBytes)}</p>
        </div>
      </div>

      {/* Footer: Last Activity */}
      <div className="mt-3 pt-2 border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
        <span className="text-themed-muted text-xs">Last Activity: {formattedLastActivity}</span>
      </div>
    </div>
  );
};

const sortOptions = [
  { value: 'totalData', label: 'Total Data' },
  { value: 'avgSpeed', label: 'Avg Download Speed' },
  { value: 'downloads', label: 'Total Downloads' },
  { value: 'hits', label: 'Cache Hits' },
  { value: 'misses', label: 'Cache Misses' },
  { value: 'hitRate', label: 'Hit Rate' },
  { value: 'lastActivity', label: 'Last Activity' },
  { value: 'ip', label: 'Client IP' }
];

const directionOptions = [
  { value: 'desc', label: 'Descending' },
  { value: 'asc', label: 'Ascending' }
];

const ClientsTab: React.FC = () => {
  const { clientStats } = useStats();
  const [sortBy, setSortBy] = useState<SortOption>('totalData');
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc');

  const sortedClients = useMemo(() => {
    const sorted = [...clientStats];
    const multiplier = sortDirection === 'desc' ? -1 : 1;

    sorted.sort((a, b) => {
      switch (sortBy) {
        case 'ip':
          return multiplier * a.clientIp.localeCompare(b.clientIp);
        case 'downloads':
          return multiplier * ((a.totalDownloads || 0) - (b.totalDownloads || 0));
        case 'totalData':
          return multiplier * ((a.totalBytes || 0) - (b.totalBytes || 0));
        case 'avgSpeed':
          return multiplier * ((a.averageBytesPerSecond || 0) - (b.averageBytesPerSecond || 0));
        case 'hits':
          return multiplier * ((a.totalCacheHitBytes || 0) - (b.totalCacheHitBytes || 0));
        case 'misses':
          return multiplier * ((a.totalCacheMissBytes || 0) - (b.totalCacheMissBytes || 0));
        case 'hitRate':
          return multiplier * ((a.cacheHitPercent || 0) - (b.cacheHitPercent || 0));
        case 'lastActivity':
          return multiplier * (new Date(a.lastActivityUtc).getTime() - new Date(b.lastActivityUtc).getTime());
        default:
          return 0;
      }
    });

    return sorted;
  }, [clientStats, sortBy, sortDirection]);

  return (
    <div className="space-y-6">
      <h2 className="text-xl sm:text-2xl font-bold text-themed-primary tracking-tight hidden md:block">
        Clients
      </h2>

      <Card>
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 mb-4">
          <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
            Client Statistics
            <CacheInfoTooltip />
          </h3>
          <div className="flex items-center gap-2">
            <ArrowUpDown size={14} className="text-themed-muted hidden sm:block" />
            <EnhancedDropdown
              options={sortOptions}
              value={sortBy}
              onChange={(value) => setSortBy(value as SortOption)}
              prefix="Sort:"
              className="w-40 sm:w-44"
              cleanStyle
            />
            <EnhancedDropdown
              options={directionOptions}
              value={sortDirection}
              onChange={(value) => setSortDirection(value as SortDirection)}
              className="w-32 sm:w-36"
              cleanStyle
            />
          </div>
        </div>

        {/* Mobile: Card Layout */}
        <div className="md:hidden space-y-3">
          {sortedClients.length > 0 ? (
            sortedClients.map((client, idx) => (
              <ClientCard key={idx} client={client} />
            ))
          ) : (
            <p className="py-8 text-center text-themed-muted">No client data available</p>
          )}
        </div>

        {/* Desktop: Table Layout */}
        <div className="hidden md:block overflow-x-auto -mx-2 px-2">
          <table className="w-full">
            <thead>
              <tr className="text-left text-xs text-themed-muted uppercase tracking-wider">
                <th className="pb-3">Client IP</th>
                <th className="pb-3">Total Downloads</th>
                <th className="pb-3">Total Data</th>
                <th className="pb-3 hidden lg:table-cell">Avg Download Speed</th>
                <th className="pb-3">Cache Hits</th>
                <th className="pb-3">Cache Misses</th>
                <th className="pb-3">Hit Rate</th>
                <th className="pb-3 hidden lg:table-cell">Last Activity</th>
              </tr>
            </thead>
            <tbody className="text-sm">
              {sortedClients.length > 0 ? (
                sortedClients.map((client, idx) => (
                  <ClientRow key={idx} client={client} />
                ))
              ) : (
                <tr>
                  <td colSpan={8} className="py-8 text-center text-themed-muted">
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
