import React from 'react';
import { Users, Server, TrendingUp, Clock } from 'lucide-react';
import clsx from 'clsx';
import { formatBytes, formatDate, getServiceIcon, getServiceColor } from '../utils/formatters';

export default function Statistics({ clientStats, serviceStats, darkMode }) {
  return (
    <div className="space-y-6">
      {/* Client Statistics */}
      <div className={clsx(
        'rounded-lg shadow-lg p-6',
        darkMode ? 'bg-gray-800' : 'bg-white'
      )}>
        <div className="flex items-center gap-2 mb-4">
          <Users className="w-5 h-5 text-blue-500" />
          <h2 className="text-xl font-bold">Client Statistics</h2>
        </div>
        
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead>
              <tr className={clsx(
                'border-b',
                darkMode ? 'border-gray-700' : 'border-gray-200'
              )}>
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
              {clientStats?.map(client => (
                <tr
                  key={client.clientIp}
                  className={clsx(
                    'border-b transition-colors',
                    darkMode
                      ? 'border-gray-700 hover:bg-gray-700'
                      : 'border-gray-100 hover:bg-gray-50'
                  )}
                >
                  <td className="p-3 font-mono font-semibold">{client.clientIp}</td>
                  <td className="p-3">
                    <span className="px-2 py-1 bg-blue-500 text-white rounded text-sm">
                      {client.totalDownloads}
                    </span>
                  </td>
                  <td className="p-3 text-green-500 font-semibold">
                    {formatBytes(client.totalCacheHitBytes)}
                  </td>
                  <td className="p-3 text-red-500 font-semibold">
                    {formatBytes(client.totalCacheMissBytes)}
                  </td>
                  <td className="p-3 font-bold">{formatBytes(client.totalBytes)}</td>
                  <td className="p-3">
                    <div className="flex items-center gap-2">
                      <div className={clsx(
                        'w-20 h-2 rounded-full',
                        darkMode ? 'bg-gray-600' : 'bg-gray-200'
                      )}>
                        <div
                          className="h-2 rounded-full bg-gradient-to-r from-green-500 to-blue-500"
                          style={{ width: `${client.cacheHitPercent}%` }}
                        />
                      </div>
                      <span className="text-sm font-semibold">
                        {client.cacheHitPercent.toFixed(1)}%
                      </span>
                    </div>
                  </td>
                  <td className="p-3 text-sm">
                    <div className="flex items-center gap-1">
                      <Clock className="w-3 h-3" />
                      {formatDate(client.lastSeen)}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          
          {(!clientStats || clientStats.length === 0) && (
            <div className="text-center py-8 text-gray-500">
              No client statistics available
            </div>
          )}
        </div>
      </div>

      {/* Service Statistics */}
      <div className={clsx(
        'rounded-lg shadow-lg p-6',
        darkMode ? 'bg-gray-800' : 'bg-white'
      )}>
        <div className="flex items-center gap-2 mb-4">
          <Server className="w-5 h-5 text-purple-500" />
          <h2 className="text-xl font-bold">Service Statistics</h2>
        </div>
        
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {serviceStats?.map(service => (
            <div
              key={service.service}
              className={clsx(
                'p-4 rounded-lg border-l-4',
                darkMode ? 'bg-gray-700' : 'bg-gray-50'
              )}
              style={{ borderLeftColor: getServiceColor(service.service) }}
            >
              <div className="flex items-center justify-between mb-3">
                <div className="flex items-center gap-2">
                  <span className="text-2xl">{getServiceIcon(service.service)}</span>
                  <h3 className="font-bold text-lg">{service.service.toUpperCase()}</h3>
                </div>
                <TrendingUp className="w-5 h-5 opacity-50" />
              </div>
              
              <div className="space-y-2">
                <div className="flex justify-between text-sm">
                  <span className="text-gray-500">Downloads:</span>
                  <span className="font-semibold">{service.totalDownloads}</span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-500">Cache Hit:</span>
                  <span className="text-green-500 font-semibold">
                    {formatBytes(service.totalCacheHitBytes)}
                  </span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-500">Cache Miss:</span>
                  <span className="text-red-500 font-semibold">
                    {formatBytes(service.totalCacheMissBytes)}
                  </span>
                </div>
                <div className="flex justify-between text-sm">
                  <span className="text-gray-500">Total:</span>
                  <span className="font-bold">{formatBytes(service.totalBytes)}</span>
                </div>
                
                <div className="pt-2 border-t border-gray-600">
                  <div className="flex justify-between mb-1">
                    <span className="text-sm text-gray-500">Hit Rate:</span>
                    <span className="text-sm font-semibold text-green-500">
                      {service.cacheHitPercent.toFixed(1)}%
                    </span>
                  </div>
                  <div className={clsx(
                    'w-full h-2 rounded-full',
                    darkMode ? 'bg-gray-600' : 'bg-gray-200'
                  )}>
                    <div
                      className="h-2 rounded-full bg-gradient-to-r from-green-500 to-blue-500"
                      style={{ width: `${service.cacheHitPercent}%` }}
                    />
                  </div>
                </div>
                
                <div className="text-xs text-gray-500 pt-2">
                  Last activity: {formatDate(service.lastActivity)}
                </div>
              </div>
            </div>
          ))}
        </div>
        
        {(!serviceStats || serviceStats.length === 0) && (
          <div className="text-center py-8 text-gray-500">
            No service statistics available
          </div>
        )}
      </div>
    </div>
  );
}