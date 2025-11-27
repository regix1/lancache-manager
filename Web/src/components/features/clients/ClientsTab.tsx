import React from 'react';
import { useStats } from '@contexts/StatsContext';
import { formatBytes, formatPercent } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { Card } from '@components/ui/Card';
import { CacheInfoTooltip } from '@components/ui/Tooltip';

interface ClientData {
  clientIp: string;
  totalDownloads: number;
  totalBytes: number;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  cacheHitPercent: number;
  lastActivityUtc: string;
}

interface ClientRowProps {
  client: ClientData;
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

// Mobile card layout for each client
const ClientCard: React.FC<ClientRowProps> = ({ client }) => {
  const formattedLastActivity = useFormattedDateTime(client.lastActivityUtc);

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
          <span className="text-themed-muted text-xs">Downloads</span>
          <p className="text-themed-secondary">{client.totalDownloads}</p>
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

        {/* Mobile: Card Layout */}
        <div className="md:hidden space-y-3">
          {clientStats.length > 0 ? (
            clientStats.map((client, idx) => (
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
                <th className="pb-3">Cache Hits</th>
                <th className="pb-3">Cache Misses</th>
                <th className="pb-3">Hit Rate</th>
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
