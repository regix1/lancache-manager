import React from 'react';
import { Settings, Trash2, Database, Zap } from 'lucide-react';
import { formatBytes } from '../utils/formatters';

export default function Management({ cacheInfo, connection, darkMode, clearCache, resetDatabase }) {
  return (
    <div className="space-y-6">
      <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6`}>
        <h2 className="text-xl font-bold mb-4 flex items-center">
          <Settings className="w-5 h-5 mr-2" />
          Cache Management
        </h2>
        
        {cacheInfo && (
          <div className="mb-6">
            <h3 className="font-semibold mb-3">Cache Storage</h3>
            <div className="space-y-3">
              <div>
                <div className="flex justify-between mb-1">
                  <span>Storage Usage</span>
                  <span>{formatBytes(cacheInfo.usedCacheSize)} / {formatBytes(cacheInfo.totalCacheSize)}</span>
                </div>
                <div className="w-full bg-gray-700 rounded-full h-3">
                  <div
                    className={`h-3 rounded-full transition-all ${
                      cacheInfo.usagePercent > 90 
                        ? 'bg-accent-red' 
                        : cacheInfo.usagePercent > 70 
                          ? 'bg-accent-yellow' 
                          : 'bg-accent-green'
                    }`}
                    style={{ width: `${cacheInfo.usagePercent}%` }}
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 md:grid-cols-3 gap-3 mt-4">
                {Object.entries(cacheInfo.serviceSizes).map(([service, size]) => (
                  <div
                    key={service}
                    className={`p-3 rounded-lg ${darkMode ? 'bg-dark-bg' : 'bg-gray-50'}`}
                  >
                    <div className="flex items-center justify-between">
                      <span className="font-semibold">{service.toUpperCase()}</span>
                      <button
                        onClick={() => clearCache(service)}
                        className="p-1 hover:bg-accent-red hover:text-white rounded transition-all"
                      >
                        <Trash2 className="w-4 h-4" />
                      </button>
                    </div>
                    <p className="text-lg font-bold">{formatBytes(size)}</p>
                  </div>
                ))}
              </div>
            </div>
          </div>
        )}

        <div className="space-y-3">
          <h3 className="font-semibold">Actions</h3>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            <button
              onClick={() => clearCache()}
              className="flex items-center justify-center space-x-2 p-3 bg-accent-red text-white rounded-lg hover:bg-red-600 transition-all"
            >
              <Trash2 className="w-5 h-5" />
              <span>Clear All Cache</span>
            </button>
            <button
              onClick={resetDatabase}
              className="flex items-center justify-center space-x-2 p-3 bg-accent-yellow text-white rounded-lg hover:bg-yellow-600 transition-all"
            >
              <Database className="w-5 h-5" />
              <span>Reset Database</span>
            </button>
          </div>
        </div>
      </div>

      <div className={`rounded-xl ${darkMode ? 'bg-dark-surface' : 'bg-white'} shadow-lg p-6`}>
        <h2 className="text-xl font-bold mb-4">System Information</h2>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <h3 className="font-semibold mb-2">Connection Status</h3>
            <div className="flex items-center space-x-2">
              <div className={`w-3 h-3 rounded-full ${connection ? 'bg-accent-green' : 'bg-accent-red'} animate-pulse`} />
              <span>{connection ? 'Connected' : 'Disconnected'}</span>
            </div>
          </div>
          <div>
            <h3 className="font-semibold mb-2">Data Refresh</h3>
            <div className="flex items-center space-x-2">
              <Zap className="w-4 h-4 text-accent-yellow" />
              <span>Real-time updates via WebSocket</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}