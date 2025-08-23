import React from 'react';
import { HardDrive, Database, Server, Users, TrendingUp, TrendingDown } from 'lucide-react';
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip, Legend } from 'recharts';
import clsx from 'clsx';
import { formatBytes, formatDate, getServiceColor, getServiceIcon } from '../utils/formatters';

export default function Dashboard({ downloads, clientStats, serviceStats, cacheInfo, darkMode }) {
  const activeDownloads = downloads?.filter(d => d.isActive) || [];
  const recentDownloads = downloads?.slice(0, 10) || [];

  const CustomTooltip = ({ active, payload }) => {
    if (active && payload && payload[0]) {
      return (
        <div className={clsx(
          'p-2 rounded shadow-lg',
          darkMode ? 'bg-gray-800 text-white' : 'bg-white text-gray-900'
        )}>
          <p className="font-semibold">{payload[0].name}</p>
          <p>{formatBytes(payload[0].value)}</p>
        </div>
      );
    }
    return null;
  };

  return (
    <div className="space-y-6">
      {/* Stats Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        <div className={clsx(
          'p-6 rounded-lg shadow-lg transition-all duration-200',
          darkMode ? 'bg-gray-800' : 'bg-white'
        )}>
          <div className="flex items-center justify-between">
            <div>
              <p className={clsx('text-sm', darkMode ? 'text-gray-400' : 'text-gray-600')}>
                Total Cache
              </p>
              <p className="text-2xl font-bold">{formatBytes(cacheInfo?.totalCacheSize || 0)}</p>
              <p className="text-xs text-blue-500 mt-1">
                {cacheInfo?.totalFiles?.toLocaleString() || 0} files
              </p>
            </div>
            <HardDrive className="w-8 h-8 text-blue-500 opacity-50" />
          </div>
        </div>

        <div className={clsx(
          'p-6 rounded-lg shadow-lg transition-all duration-200',
          darkMode ? 'bg-gray-800' : 'bg-white'
        )}>
          <div className="flex items-center justify-between">
            <div>
              <p className={clsx('text-sm', darkMode ? 'text-gray-400' : 'text-gray-600')}>
                Used Space
              </p>
              <p className="text-2xl font-bold">{formatBytes(cacheInfo?.usedCacheSize || 0)}</p>
              <p className="text-xs text-green-500 mt-1">
                {cacheInfo?.usagePercent?.toFixed(1) || 0}% utilized
              </p>
            </div>
            <Database className="w-8 h-8 text-green-500 opacity-50" />
          </div>
        </div>

        <div className={clsx(
          'p-6 rounded-lg shadow-lg transition-all duration-200',
          darkMode ? 'bg-gray-800' : 'bg-white'
        )}>
          <div className="flex items-center justify-between">
            <div>
              <p className={clsx('text-sm', darkMode ? 'text-gray-400' : 'text-gray-600')}>
                Active Downloads
              </p>
              <p className="text-2xl font-bold">{activeDownloads.length}</p>
              <p className="text-xs text-purple-500 mt-1">
                {downloads?.length || 0} total
              </p>
            </div>
            <Server className="w-8 h-8 text-purple-500 opacity-50" />
          </div>
        </div>

        <div className={clsx(
          'p-6 rounded-lg shadow-lg transition-all duration-200',
          darkMode ? 'bg-gray-800' : 'bg-white'
        )}>
          <div className="flex items-center justify-between">
            <div>
              <p className={clsx('text-sm', darkMode ? 'text-gray-400' : 'text-gray-600')}>
                Active Clients
              </p>
              <p className="text-2xl font-bold">{clientStats?.length || 0}</p>
              <p className="text-xs text-yellow-500 mt-1">
                {serviceStats?.length || 0} services
              </p>
            </div>
            <Users className="w-8 h-8 text-yellow-500 opacity-50" />
          </div>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Service Distribution Chart */}
        <div className={clsx(
          'p-6 rounded-lg shadow-lg',
          darkMode ? 'bg-gray-800' : 'bg-white'
        )}>
          <h2 className="text-xl font-bold mb-4">Service Distribution</h2>
          {serviceStats && serviceStats.length > 0 ? (
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
                <Tooltip content={<CustomTooltip />} />
              </PieChart>
            </ResponsiveContainer>
          ) : (
            <div className="h-[300px] flex items-center justify-center text-gray-500">
              No data available
            </div>
          )}
        </div>

        {/* Recent Downloads */}
        <div className={clsx(
          'p-6 rounded-lg shadow-lg',
          darkMode ? 'bg-gray-800' : 'bg-white'
        )}>
          <h2 className="text-xl font-bold mb-4">Recent Downloads</h2>
          <div className="space-y-2 max-h-[300px] overflow-y-auto">
            {recentDownloads.length > 0 ? (
              recentDownloads.map(download => (
                <div
                  key={download.id}
                  className={clsx(
                    'p-3 rounded-lg transition-all duration-200',
                    darkMode ? 'bg-gray-700 hover:bg-gray-600' : 'bg-gray-50 hover:bg-gray-100'
                  )}
                >
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                      <span className="text-2xl">{getServiceIcon(download.service)}</span>
                      <div>
                        <p className="font-semibold">{download.service.toUpperCase()}</p>
                        <p className={clsx('text-sm', darkMode ? 'text-gray-400' : 'text-gray-600')}>
                          {download.clientIp} • {formatDate(download.startTime)}
                        </p>
                      </div>
                    </div>
                    <div className="text-right">
                      <p className="font-semibold">{formatBytes(download.totalBytes)}</p>
                      <div className="flex items-center gap-2 text-sm">
                        <span className="text-green-500 flex items-center gap-1">
                          <TrendingDown className="w-3 h-3" />
                          {download.cacheHitPercent.toFixed(1)}%
                        </span>
                        {download.isActive && (
                          <span className="px-2 py-0.5 bg-green-500 text-white text-xs rounded">
                            Active
                          </span>
                        )}
                      </div>
                    </div>
                  </div>
                  <div className="mt-2">
                    <div className={clsx(
                      'w-full h-2 rounded-full',
                      darkMode ? 'bg-gray-600' : 'bg-gray-200'
                    )}>
                      <div
                        className="h-2 rounded-full bg-gradient-to-r from-green-500 to-blue-500 transition-all duration-300"
                        style={{ width: `${download.cacheHitPercent}%` }}
                      />
                    </div>
                  </div>
                </div>
              ))
            ) : (
              <div className="text-center text-gray-500 py-8">
                No downloads yet
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Top Clients */}
      <div className={clsx(
        'p-6 rounded-lg shadow-lg',
        darkMode ? 'bg-gray-800' : 'bg-white'
      )}>
        <h2 className="text-xl font-bold mb-4">Top Clients</h2>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {clientStats?.slice(0, 6).map(client => (
            <div
              key={client.clientIp}
              className={clsx(
                'p-4 rounded-lg',
                darkMode ? 'bg-gray-700' : 'bg-gray-50'
              )}
            >
              <div className="flex items-center justify-between mb-2">
                <p className="font-semibold">{client.clientIp}</p>
                <span className="text-sm text-blue-500">{client.totalDownloads} downloads</span>
              </div>
              <div className="space-y-1">
                <div className="flex justify-between text-sm">
                  <span>Total Traffic:</span>
                  <span className="font-semibold">{formatBytes(client.totalBytes)}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span>Hit Rate:</span>
                  <span className="text-green-500 font-semibold">
                    {client.cacheHitPercent.toFixed(1)}%
                  </span>
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}