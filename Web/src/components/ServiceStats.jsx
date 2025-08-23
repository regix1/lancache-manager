import React from 'react';
import { Activity } from 'lucide-react';
import { formatBytes, getServiceIcon, getServiceColor } from '../utils/formatters';

export default function ServiceStats({ serviceStats, darkMode }) {
  return (
    <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6`}>
      <h2 className="text-xl font-bold mb-4">Service Statistics</h2>
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        {serviceStats.map((service) => (
          <div
            key={service.service}
            className={`p-4 rounded-lg ${darkMode ? 'bg-dark-bg' : 'bg-gray-50'}`}
            style={{ borderLeft: `4px solid ${getServiceColor(service.service)}` }}
          >
            <div className="flex items-center justify-between mb-3">
              <div className="flex items-center space-x-2">
                <span className="text-2xl">{getServiceIcon(service.service)}</span>
                <h3 className="font-bold text-lg">{service.service.toUpperCase()}</h3>
              </div>
              <Activity className="w-5 h-5 opacity-50" />
            </div>
            <div className="space-y-2 text-sm">
              <div className="flex justify-between">
                <span className="opacity-70">Downloads:</span>
                <span className="font-semibold">{service.totalDownloads}</span>
              </div>
              <div className="flex justify-between">
                <span className="opacity-70">Cache Hit:</span>
                <span className="text-accent-green">{formatBytes(service.totalCacheHitBytes)}</span>
              </div>
              <div className="flex justify-between">
                <span className="opacity-70">Cache Miss:</span>
                <span className="text-accent-red">{formatBytes(service.totalCacheMissBytes)}</span>
              </div>
              <div className="flex justify-between">
                <span className="opacity-70">Total:</span>
                <span className="font-bold">{formatBytes(service.totalBytes)}</span>
              </div>
              <div className="pt-2">
                <div className="flex justify-between mb-1">
                  <span className="opacity-70">Hit Rate:</span>
                  <span className="font-semibold">{service.cacheHitPercent.toFixed(1)}%</span>
                </div>
                <div className="w-full bg-gray-700 rounded-full h-2">
                  <div
                    className="bg-gradient-to-r from-green-500 to-blue-500 h-2 rounded-full"
                    style={{ width: `${service.cacheHitPercent}%` }}
                  />
                </div>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}