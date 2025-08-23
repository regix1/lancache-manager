import React, { useState, useEffect } from 'react';
import { Database, HardDrive, Trash2, AlertTriangle, RotateCcw, FileText } from 'lucide-react';
import axios from 'axios';

function Management() {
  const [cacheInfo, setCacheInfo] = useState(null);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState('');

  useEffect(() => {
    loadCacheInfo();
  }, []);

  const loadCacheInfo = async () => {
    try {
      const response = await axios.get('/api/management/cache');
      setCacheInfo(response.data);
    } catch (error) {
      console.error('Error loading cache info:', error);
    }
  };

  const handleClearCache = async (service = null) => {
    const confirmMsg = service 
      ? `Are you sure you want to clear the cache for ${service}?`
      : 'Are you sure you want to clear ALL cache? This will delete all cached game files!';
    
    if (!window.confirm(confirmMsg)) return;
    
    setLoading(true);
    try {
      await axios.delete(`/api/management/cache${service ? `?service=${service}` : ''}`);
      setMessage(`Cache cleared for ${service || 'all services'}`);
      await loadCacheInfo();
    } catch (error) {
      setMessage('Error clearing cache');
    }
    setLoading(false);
    setTimeout(() => setMessage(''), 5000);
  };

  const handleResetDatabase = async () => {
    if (!window.confirm('Are you sure you want to reset the database? This will delete all download history and statistics!')) return;
    
    setLoading(true);
    try {
      await axios.delete('/api/management/database');
      setMessage('Database reset successfully');
      await loadCacheInfo();
    } catch (error) {
      setMessage('Error resetting database');
    }
    setLoading(false);
    setTimeout(() => setMessage(''), 5000);
  };

  const handleResetLogs = async () => {
    const confirmed = window.confirm(
      '⚠️ RESET LOG POSITION\n\n' +
      'This will:\n' +
      '• Clear all download history\n' +
      '• Reset statistics\n' +
      '• Start monitoring from the current end of the log file\n' +
      '• Only track NEW downloads going forward\n\n' +
      'The application will need to restart. Continue?'
    );
    
    if (!confirmed) return;
    
    setLoading(true);
    try {
      const response = await axios.post('/api/management/reset-logs');
      setMessage(response.data.message);
      
      if (response.data.requiresRestart) {
        setTimeout(() => {
          setMessage('Restarting... Please refresh the page in a few seconds.');
        }, 2000);
      }
    } catch (error) {
      setMessage('Error resetting logs');
    }
    setLoading(false);
  };

  const handleProcessAllLogs = async () => {
    const confirmed = window.confirm(
      '⚠️ PROCESS ENTIRE LOG FILE\n\n' +
      'WARNING: This will process your ENTIRE log file from the beginning!\n\n' +
      'This can:\n' +
      '• Take a VERY long time (10+ minutes for large logs)\n' +
      '• Create thousands of database entries\n' +
      '• Use significant CPU and memory\n' +
      '• Process months/years of historical data\n\n' +
      'Only do this if you really want to import ALL historical data.\n\n' +
      'Continue?'
    );
    
    if (!confirmed) return;
    
    const reallyConfirmed = window.confirm(
      '⚠️ FINAL WARNING\n\n' +
      'Are you ABSOLUTELY SURE you want to process the entire log file?\n\n' +
      'This cannot be easily undone and may take a long time!'
    );
    
    if (!reallyConfirmed) return;
    
    setLoading(true);
    try {
      const response = await axios.post('/api/management/process-all-logs');
      const { logSizeMB, estimatedTimeMinutes } = response.data;
      
      setMessage(
        `Processing ${logSizeMB.toFixed(1)} MB log file.\n` +
        `Estimated time: ${estimatedTimeMinutes} minutes.\n` +
        `The application will restart. Please wait...`
      );
      
      if (response.data.requiresRestart) {
        setTimeout(() => {
          setMessage('Processing... Check back in a few minutes and refresh the page.');
        }, 3000);
      }
    } catch (error) {
      setMessage('Error setting up log processing');
    }
    setLoading(false);
  };

  const formatBytes = (bytes) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  return (
    <div className="p-6">
      <h2 className="text-2xl font-semibold mb-6">System Management</h2>

      {message && (
        <div className={`mb-4 p-3 rounded-lg ${message.includes('Error') ? 'bg-red-500' : 'bg-green-500'} text-white`}>
          <pre className="whitespace-pre-wrap">{message}</pre>
        </div>
      )}

      <div className="grid gap-6">
        {/* Cache Information */}
        <div className="bg-white dark:bg-gray-800 rounded-lg p-6 shadow">
          <div className="flex items-center mb-4">
            <HardDrive className="w-5 h-5 mr-2" />
            <h3 className="text-lg font-semibold">Cache Storage</h3>
          </div>
          
          {cacheInfo && (
            <div className="space-y-3">
              <div className="flex justify-between">
                <span className="text-gray-600 dark:text-gray-400">Total Size:</span>
                <span className="font-mono">{formatBytes(cacheInfo.totalCacheSize)}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-gray-600 dark:text-gray-400">Used:</span>
                <span className="font-mono">{formatBytes(cacheInfo.usedCacheSize)}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-gray-600 dark:text-gray-400">Free:</span>
                <span className="font-mono">{formatBytes(cacheInfo.freeCacheSize)}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-gray-600 dark:text-gray-400">Files:</span>
                <span className="font-mono">{cacheInfo.totalFiles.toLocaleString()}</span>
              </div>
              
              {Object.keys(cacheInfo.serviceSizes).length > 0 && (
                <div className="mt-4 pt-4 border-t border-gray-200 dark:border-gray-700">
                  <h4 className="font-semibold mb-2">Service Breakdown:</h4>
                  {Object.entries(cacheInfo.serviceSizes).map(([service, size]) => (
                    <div key={service} className="flex justify-between items-center py-1">
                      <span className="text-gray-600 dark:text-gray-400 capitalize">{service}:</span>
                      <div className="flex items-center gap-2">
                        <span className="font-mono">{formatBytes(size)}</span>
                        <button
                          onClick={() => handleClearCache(service)}
                          disabled={loading}
                          className="p-1 text-red-500 hover:bg-red-100 dark:hover:bg-red-900 rounded"
                          title={`Clear ${service} cache`}
                        >
                          <Trash2 className="w-4 h-4" />
                        </button>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>

        {/* Action Buttons */}
        <div className="grid md:grid-cols-2 gap-4">
          <button
            onClick={() => handleClearCache()}
            disabled={loading}
            className="bg-yellow-500 hover:bg-yellow-600 text-white px-4 py-3 rounded-lg flex items-center justify-center gap-2 transition-colors"
          >
            <Trash2 className="w-5 h-5" />
            Clear All Cache
          </button>

          <button
            onClick={handleResetDatabase}
            disabled={loading}
            className="bg-red-500 hover:bg-red-600 text-white px-4 py-3 rounded-lg flex items-center justify-center gap-2 transition-colors"
          >
            <Database className="w-5 h-5" />
            Reset Database
          </button>

          <button
            onClick={handleResetLogs}
            disabled={loading}
            className="bg-blue-500 hover:bg-blue-600 text-white px-4 py-3 rounded-lg flex items-center justify-center gap-2 transition-colors"
          >
            <RotateCcw className="w-5 h-5" />
            Reset Log Position
          </button>

          <button
            onClick={handleProcessAllLogs}
            disabled={loading}
            className="bg-purple-500 hover:bg-purple-600 text-white px-4 py-3 rounded-lg flex items-center justify-center gap-2 transition-colors"
          >
            <FileText className="w-5 h-5" />
            Process Entire Log
          </button>
        </div>

        {/* Warning Messages */}
        <div className="bg-yellow-50 dark:bg-yellow-900/20 border border-yellow-200 dark:border-yellow-800 rounded-lg p-4">
          <div className="flex items-start">
            <AlertTriangle className="w-5 h-5 text-yellow-600 dark:text-yellow-500 mt-0.5 mr-3 flex-shrink-0" />
            <div className="text-sm text-yellow-800 dark:text-yellow-200">
              <p className="font-semibold mb-1">Important Notes:</p>
              <ul className="list-disc list-inside space-y-1">
                <li><strong>Reset Log Position:</strong> Starts fresh from current log position, only tracking new downloads</li>
                <li><strong>Process Entire Log:</strong> Imports ALL historical data (can take very long for large logs)</li>
                <li><strong>Clear Cache:</strong> Deletes actual cached game files (clients will need to re-download)</li>
                <li><strong>Reset Database:</strong> Clears all statistics and history (keeps cache files)</li>
              </ul>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

export default Management;