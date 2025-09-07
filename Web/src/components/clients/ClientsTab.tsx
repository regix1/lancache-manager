import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { Card } from '../ui/Card';
import { CacheInfoTooltip } from '../common/Tooltip';

const ClientsTab: React.FC = () => {
  const { clientStats } = useData();

  return (
    <Card>
      <h2 className="text-xl font-semibold mb-4 flex items-center gap-2">
        Client Statistics
        <CacheInfoTooltip />
      </h2>
      
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className="text-left text-xs text-gray-400 uppercase tracking-wider border-b border-gray-700">
              <th className="pb-3">Client IP</th>
              <th className="pb-3">Total Downloads</th>
              <th className="pb-3">Total Data</th>
              <th className="pb-3">Cache Hits</th>
              <th className="pb-3">Cache Misses</th>
              <th className="pb-3">Hit Rate</th>
              <th className="pb-3">Last Activity</th>
            </tr>
          </thead>
          <tbody className="text-sm">
            {clientStats.length > 0 ? (
              clientStats.map((client, idx) => (
                <tr key={idx} className="border-t border-gray-700">
                  <td className="py-3 text-white font-medium">{client.clientIp}</td>
                  <td className="py-3">{client.totalDownloads}</td>
                  <td className="py-3">{formatBytes(client.totalBytes)}</td>
                  <td className="py-3 text-green-400">{formatBytes(client.totalCacheHitBytes)}</td>
                  <td className="py-3 text-yellow-400">{formatBytes(client.totalCacheMissBytes)}</td>
                  <td className="py-3">
                    <div className="flex items-center space-x-2">
                      <div className="w-24 bg-gray-700 rounded-full h-2">
                        <div 
                          className="bg-green-500 h-2 rounded-full"
                          style={{ width: `${client.cacheHitPercent}%` }}
                        />
                      </div>
                      <span className="text-xs">{formatPercent(client.cacheHitPercent)}</span>
                    </div>
                  </td>
                  <td className="py-3 text-gray-400">
                    {formatDateTime(client.lastSeen)}
                  </td>
                </tr>
              ))
            ) : (
              <tr>
                <td colSpan={7} className="py-8 text-center text-gray-500">
                  No client data available
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </Card>
  );
};

export default ClientsTab;