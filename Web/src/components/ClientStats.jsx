import React from 'react';
import { formatBytes, formatDate } from '../utils/formatters';

export default function ClientStats({ clientStats, darkMode }) {
  return (
    <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6 mb-6`}>
      <h2 className="text-xl font-bold mb-4">Client Statistics</h2>
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className={`border-b ${darkMode ? 'border-dark-border' : 'border-gray-200'}`}>
              <th className="text-left p-3">Client IP</th>
              <th className="text-left p-3">Total Downloads</th>
              <th className="text-left p-3">Cache Hit</th>
              <th className="text-left p-3">Cache Miss</th>
              <th className="text-left p-3">Total Traffic</th>
              <th className="text-left p-3">Hit Rate</th>
              <th className="text-left p-3">Last Seen</th>
            </tr>
          </thead>
          <tbody>
            {clientStats.map((client) => (
              <tr
                key={client.clientIp}
                className={`border-b ${
                  darkMode ? 'border-dark-border hover:bg-dark-bg' : 'border-gray-100 hover:bg-gray-50'
                } transition-all`}
              >
                <td className="p-3 font-semibold">{client.clientIp}</td>
                <td className="p-3">{client.totalDownloads}</td>
                <td className="p-3 text-accent-green">{formatBytes(client.totalCacheHitBytes)}</td>
                <td className="p-3 text-accent-red">{formatBytes(client.totalCacheMissBytes)}</td>
                <td className="p-3 font-semibold">{formatBytes(client.totalBytes)}</td>
                <td className="p-3">
                  <div className="flex items-center space-x-2">
                    <div className="w-24 bg-gray-700 rounded-full h-2">
                      <div
                        className="bg-gradient-to-r from-green-500 to-blue-500 h-2 rounded-full"
                        style={{ width: `${client.cacheHitPercent}%` }}
                      />
                    </div>
                    <span className="text-sm">{client.cacheHitPercent.toFixed(1)}%</span>
                  </div>
                </td>
                <td className="p-3 text-sm">{formatDate(client.lastSeen)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}