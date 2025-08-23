import React, { useState } from 'react';
import { Trash2, Database, RefreshCw, HardDrive, AlertTriangle } from 'lucide-react';
import clsx from 'clsx';
import { formatBytes } from '../utils/formatters';
import api from '../services/api';

export default function Management({ cacheInfo, darkMode, onRefresh }) {
  const [isClearing, setIsClearing] = useState(false);
  const [isResetting, setIsResetting] = useState(false);

  const handleClearCache = async (service = null) => {
    const message = service 
      ? `Are you sure you want to clear the ${service} cache?`
      : 'Are you sure you want to clear ALL cache? This cannot be undone.';
    
    if (!confirm(message)) return;
    
    setIsClearing(true);
    try {
      await api.clearCache(service);
      await onRefresh();
      alert('Cache cleared successfully');
    } catch (error) {
      console.error('Error clearing cache:', error);
      alert('Failed to clear cache');
    } finally {
      setIsClearing(false);
    }
  };

  const handleResetDatabase = async () => {
    if (!confirm('Are you sure you want to reset the database? This will delete ALL statistics and cannot be undone.')) {
      return;
    }
    
    setIsResetting(true);
    try {
      await api.resetDatabase();
      await onRefresh();
      alert('Database reset successfully');
    } catch (error) {
      console.error('Error resetting database:', error);
      alert('Failed to reset database');
    } finally {
      setIsResetting(false);
    }
  };

  return (
    <div className="space-y-6">
      {/* Cache Management */}
      <div className={clsx(
        'rounded-lg shadow-lg p-6',
        darkMode ? 'bg-gray-800' : 'bg-white'
      )}>
        <div className="flex items-center gap-2 mb-4">
          <HardDrive className="w-5 h-5 text-blue-500" />
          <h2 className="text-xl font-bold">Cache Management</h2>
        </div>
        
        {cacheInfo && (
          <>
            {/* Storage Overview */}
            <div className="mb-6">
              <div className="flex justify-between mb-2">
                <span>Storage Usage</span>
                <span className="font-semibold">
                  {formatBytes(cacheInfo.usedCacheSize)} / {formatBytes(cacheInfo.totalCacheSize)}
                </span>
              </div>
              <div className={clsx(
                'w-full h-4 rounded-full',
                darkMode ? 'bg-gray-700' : 'bg-gray-200'
              )}>
                <div 
                  className={clsx(
                    'h-4 rounded-full transition-all duration-500',
                    cacheInfo.usagePercent > 90 
                      ? 'bg-red-500'
                      : cacheInfo.usagePercent > 70
                      ? 'bg-yellow-500'
                      : 'bg-green-500'
                  )}
                  style={{ width: `${cacheInfo.usagePercent}%` }}
                />
              </div>
              <div className="flex justify-between mt-1 text-sm text-gray-500">
                <span>{cacheInfo.usagePercent.toFixed(1)}% used</span>
                <span>{formatBytes(cacheInfo.freeCacheSize)} free</span>
              </div>
            </div>

            {/* Service Sizes */}
            <div className="mb-6">
              <h3 className="font-semibold mb-3">Cache by Service</h3>
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
                {Object.entries(cacheInfo.serviceSizes || {}).map(([service, size]) => (
                  <div
                    key={service}
                    className={clsx(
                      'p-4 rounded-lg',
                      darkMode ? 'bg-gray-700' : 'bg-gray-50'
                    )}
                  >
                    <div className="flex items-center justify-between mb-2">
                      <span className="font-semibold text-lg">{service.toUpperCase()}</span>
                      <button
                        onClick={() => handleClearCache(service)}
                        disabled={isClearing}
                        className={clsx(
                          'p-2 rounded transition-colors',
                          'hover:bg-red-500 hover:text-white',
                          darkMode ? 'text-gray-300' : 'text-gray-600'
                        )}
                      >
                        <Trash2 className="w-4 h-4" />
                      </button>
                    </div>
                    <p className="text-2xl font-bold">{formatBytes(size)}</p>
                  </div>
                ))}
              </div>
            </div>
          </>
        )}

        {/* Actions */}
        <div className="border-t pt-6">
          <h3 className="font-semibold mb-3">Actions</h3>
          <div className="flex flex-wrap gap-3">
            <button
              onClick={() => handleClearCache()}
              disabled={isClearing}
              className={clsx(
                'flex items-center gap-2 px-4 py-2 rounded-lg transition-colors',
                'bg-red-500 text-white hover:bg-red-600',
                'disabled:opacity-50 disabled:cursor-not-allowed'
              )}
            >
              {isClearing ? (
                <RefreshCw className="w-5 h-5 animate-spin" />
              ) : (
                <Trash2 className="w-5 h-5" />
              )}
              <span>Clear All Cache</span>
            </button>
            
            <button
              onClick={handleResetDatabase}
              disabled={isResetting}
              className={clsx(
                'flex items-center gap-2 px-4 py-2 rounded-lg transition-colors',
                'bg-yellow-500 text-white hover:bg-yellow-600',
                'disabled:opacity-50 disabled:cursor-not-allowed'
              )}
            >
              {isResetting ? (
                <RefreshCw className="w-5 h-5 animate-spin" />
              ) : (
                <Database className="w-5 h-5" />
              )}
              <span>Reset Database</span>
            </button>
          </div>
        </div>
      </div>

      {/* Warning Card */}
      <div className={clsx(
        'rounded-lg shadow-lg p-6',
        darkMode ? 'bg-yellow-900/20 border border-yellow-700' : 'bg-yellow-50 border border-yellow-200'
      )}>
        <div className="flex gap-3">
          <AlertTriangle className="w-5 h-5 text-yellow-500 flex-shrink-0 mt-0.5" />
          <div>
            <h3 className="font-semibold mb-2">Important Information</h3>
            <ul className="space-y-1 text-sm text-gray-600 dark:text-gray-400">
              <li>• Clearing cache will remove downloaded game files</li>
              <li>• Clients will need to re-download content from the internet</li>
              <li>• Database reset will clear all statistics and history</li>
              <li>• These actions cannot be undone</li>
            </ul>
          </div>
        </div>
      </div>
    </div>
  );
}