import React, { useMemo, memo } from 'react';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { CacheInfoTooltip } from '../common/Tooltip';
import { Card } from '../ui/Card';

interface TopClientsTableProps {
  clientStats?: any[];
  downloads?: any[];
  timeRange?: string;
}


const TopClientsTable: React.FC<TopClientsTableProps> = memo(({ 
  clientStats = [], 
  timeRange = '24h' 
}) => {
  const timeRangeLabel = useMemo(() => {
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
      'all': 'All Time'
    };
    return labels[timeRange] || 'Last 24 Hours';
  }, [timeRange]);

  const displayClients = useMemo(() => clientStats.slice(0, 10), [clientStats]);

  return (
    <Card>
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-lg font-semibold text-white flex items-center gap-2">
          Top Clients
          <CacheInfoTooltip />
        </h3>
        <span className="text-xs text-gray-500">
          {timeRangeLabel}
        </span>
      </div>
      
      {displayClients.length > 0 ? (
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead>
              <tr className="text-left text-xs text-gray-400 uppercase tracking-wider">
                <th className="pb-3">Client IP</th>
                <th className="pb-3">Total Downloaded</th>
                <th className="pb-3">Cache Hits</th>
                <th className="pb-3">Cache Misses</th>
                <th className="pb-3">Hit Rate</th>
                <th className="pb-3">Last Seen</th>
              </tr>
            </thead>
            <tbody className="text-sm">
              {displayClients.map((client, idx) => (
                <tr key={`${client.clientIp}-${idx}`} className="border-t border-gray-700">
                  <td className="py-3 text-white">{client.clientIp}</td>
                  <td className="py-3 text-gray-300">{formatBytes(client.totalBytes)}</td>
                  <td className="py-3 text-green-400">{formatBytes(client.totalCacheHitBytes)}</td>
                  <td className="py-3 text-yellow-400">{formatBytes(client.totalCacheMissBytes)}</td>
                  <td className="py-3">
                    <span className={`px-2 py-1 rounded text-xs ${
                      client.cacheHitPercent > 50 
                        ? 'bg-green-900 text-green-300' 
                        : 'bg-yellow-900 text-yellow-300'
                    }`}>
                      {formatPercent(client.cacheHitPercent)}
                    </span>
                  </td>
                  <td className="py-3 text-gray-400">
                    {formatDateTime(client.lastSeen)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="flex items-center justify-center h-32 text-gray-500">
          No client data available for selected time range
        </div>
      )}
    </Card>
  );
});

TopClientsTable.displayName = 'TopClientsTable';

export default TopClientsTable;