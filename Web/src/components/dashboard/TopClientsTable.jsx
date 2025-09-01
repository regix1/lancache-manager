import React, { useMemo } from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { CacheInfoTooltip } from '../common/Tooltip';

const TopClientsTable = ({ clientStats = [], downloads = [], timeRange = '24h' }) => {
  // For real data, use clientStats directly. For mock data, calculate from downloads
  const { mockMode } = useData();
  
  const calculatedClientStats = useMemo(() => {
    // In real mode, use the clientStats as provided by the API
    if (!mockMode) {
      return clientStats;
    }
    
    // In mock mode, calculate from filtered downloads
    if (!downloads || downloads.length === 0) return clientStats;
    
    // Group downloads by client IP
    const clientMap = {};
    
    downloads.forEach(download => {
      if (!clientMap[download.clientIp]) {
        clientMap[download.clientIp] = {
          clientIp: download.clientIp,
          totalCacheHitBytes: 0,
          totalCacheMissBytes: 0,
          totalBytes: 0,
          downloadCount: 0,
          lastSeen: download.startTime
        };
      }
      
      const client = clientMap[download.clientIp];
      client.totalCacheHitBytes += download.cacheHitBytes || 0;
      client.totalCacheMissBytes += download.cacheMissBytes || 0;
      client.totalBytes += download.totalBytes || 0;
      client.downloadCount += 1;
      
      // Update last seen if this download is more recent
      if (new Date(download.startTime) > new Date(client.lastSeen)) {
        client.lastSeen = download.startTime;
      }
    });
    
    // Convert to array and calculate hit percentages
    const clientArray = Object.values(clientMap).map(client => ({
      ...client,
      cacheHitPercent: client.totalBytes > 0 
        ? (client.totalCacheHitBytes / client.totalBytes) * 100 
        : 0
    }));
    
    // Sort by total bytes descending
    return clientArray.sort((a, b) => b.totalBytes - a.totalBytes);
  }, [downloads, clientStats, mockMode]);

  // Get time range label for the table header
  const getTimeRangeLabel = () => {
    const labels = {
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
  };

  // Show top 10 clients
  const displayClients = calculatedClientStats.slice(0, 10);

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-lg font-semibold text-white">
          Top Clients
          <span className="ml-2">
            <CacheInfoTooltip />
          </span>
        </h3>
        <span className="text-xs text-gray-500">
          {getTimeRangeLabel()}
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
    </div>
  );
};

export default TopClientsTable;