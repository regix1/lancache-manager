import React from 'react';
import { useData } from '../../contexts/DataContext';
import { formatBytes, formatPercent, formatDateTime } from '../../utils/formatters';

const TopClientsTable = () => {
  const { clientStats } = useData();

  return (
    <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
      <h3 className="text-lg font-semibold text-white mb-4">Top Clients</h3>
      {clientStats.length > 0 ? (
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
              {clientStats.slice(0, 10).map((client, idx) => (
                <tr key={idx} className="border-t border-gray-700">
                  <td className="py-3 text-white">{client.clientIp}</td>
                  <td className="py-3 text-gray-300">{formatBytes(client.totalBytes)}</td>
                  <td className="py-3 text-green-400">{formatBytes(client.totalCacheHitBytes)}</td>
                  <td className="py-3 text-red-400">{formatBytes(client.totalCacheMissBytes)}</td>
                  <td className="py-3">
                    <span className={`px-2 py-1 rounded text-xs ${
                      client.cacheHitPercent > 50 
                        ? 'bg-green-900 text-green-300' 
                        : 'bg-red-900 text-red-300'
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
          No client data available
        </div>
      )}
    </div>
  );
};

export default TopClientsTable;