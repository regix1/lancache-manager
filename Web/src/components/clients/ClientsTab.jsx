import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';
import { CacheInfoTooltip } from '../common/Tooltip';

const ClientsTab = () => {
  const { clientStats } = useData();

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <h2 className="text-xl font-semibold mb-4">
        Client Statistics
        <span className="ml-2">
          <CacheInfoTooltip />
        </span>
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
            {clientStats.map((client, idx) => (
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
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
};

export default ClientsTab;