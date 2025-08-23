import React from 'react';
import { HardDrive, Database, Server, Users, Download } from 'lucide-react';
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from 'recharts';
import { formatBytes, formatDate, getServiceIcon, getServiceColor } from '../utils/formatters';

export default function Dashboard({ cacheInfo, clientStats, serviceStats, downloads, darkMode }) {
  return (
    <div className="space-y-6">
      {cacheInfo && (
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          <div className={`p-6 rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg`}>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm opacity-70">Total Cache Size</p>
                <p className="text-2xl font-bold">{formatBytes(cacheInfo.totalCacheSize)}</p>
              </div>
              <HardDrive className="w-8 h-8 text-accent-blue opacity-50" />
            </div>
          </div>
          <div className={`p-6 rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg`}>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm opacity-70">Used Space</p>
                <p className="text-2xl font-bold">{formatBytes(cacheInfo.usedCacheSize)}</p>
                <p className="text-xs text-accent-green">
                  {cacheInfo.usagePercent.toFixed(1)}% utilized
                </p>
              </div>
              <Database className="w-8 h-8 text-accent-green opacity-50" />
            </div>
          </div>
          <div className={`p-6 rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg`}>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm opacity-70">Total Files</p>
                <p className="text-2xl font-bold">{cacheInfo.totalFiles.toLocaleString()}</p>
              </div>
              <Server className="w-8 h-8 text-accent-purple opacity-50" />
            </div>
          </div>
          <div className={`p-6 rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg`}>
            <div className="flex items-center justify-between">
              <div>
                <p className="text-sm opacity-70">Active Clients</p>
                <p className="text-2xl font-bold">{clientStats.length}</p>
              </div>
              <Users className="w-8 h-8 text-accent-yellow opacity-50" />
            </div>
          </div>
        </div>
      )}

      <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6`}>
        <h2 className="text-xl font-bold mb-4 flex items-center">
          <Download className="w-5 h-5 mr-2" />
          Recent Downloads
        </h2>
        <div className="space-y-2 max-h-96 overflow-y-auto">
          {downloads.slice(0, 10).map((download) => (
            <div
              key={download.id}
              className={`p-4 rounded-lg ${
                darkMode ? 'bg-dark-bg hover:bg-dark-border' : 'bg-gray-50 hover:bg-gray-100'
              } transition-all cursor-pointer`}
            >
              <div className="flex items-center justify-between">
                <div className="flex items-center space-x-3">
                  <span className="text-2xl">{getServiceIcon(download.service)}</span>
                  <div>
                    <p className="font-semibold">{download.service.toUpperCase()}</p>
                    <p className="text-sm opacity-70">
                      {download.clientIp} • {formatDate(download.startTime)}
                    </p>
                  </div>
                </div>
                <div className="text-right">
                  <p className="font-semibold">{formatBytes(download.totalBytes)}</p>
                  <div className="flex items-center space-x-2 text-sm">
                    <span className="text-accent-green">
                      ↓ {download.cacheHitPercent.toFixed(1)}%
                    </span>
                    <span className="text-accent-red">
                      ↑ {(100 - download.cacheHitPercent).toFixed(1)}%
                    </span>
                  </div>
                </div>
              </div>
              <div className="mt-2">
                <div className="w-full bg-gray-700 rounded-full h-2">
                  <div
                    className="bg-gradient-to-r from-green-500 to-blue-500 h-2 rounded-full transition-all"
                    style={{ width: `${download.cacheHitPercent}%` }}
                  />
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6`}>
          <h2 className="text-xl font-bold mb-4">Service Distribution</h2>
          <ResponsiveContainer width="100%" height={300}>
            <PieChart>
              <Pie
                data={serviceStats}
                dataKey="totalBytes"
                nameKey="service"
                cx="50%"
                cy="50%"
                outerRadius={100}
                label={(entry) => `${entry.service}: ${formatBytes(entry.totalBytes)}`}
              >
                {serviceStats.map((entry, index) => (
                  <Cell key={`cell-${index}`} fill={getServiceColor(entry.service)} />
                ))}
              </Pie>
              <Tooltip formatter={(value) => formatBytes(value)} />
            </PieChart>
          </ResponsiveContainer>
        </div>

        <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6`}>
          <h2 className="text-xl font-bold mb-4">Top Clients</h2>
          <div className="space-y-3">
            {clientStats.slice(0, 5).map((client) => (
              <div
                key={client.clientIp}
                className={`p-3 rounded-lg ${
                  darkMode ? 'bg-dark-bg' : 'bg-gray-50'
                }`}
              >
                <div className="flex items-center justify-between">
                  <div>
                    <p className="font-semibold">{client.clientIp}</p>
                    <p className="text-sm opacity-70">
                      {client.totalDownloads} downloads
                    </p>
                  </div>
                  <div className="text-right">
                    <p className="font-semibold">{formatBytes(client.totalBytes)}</p>
                    <p className="text-sm text-accent-green">
                      {client.cacheHitPercent.toFixed(1)}% hit rate
                    </p>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}