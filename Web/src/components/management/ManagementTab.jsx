import React, { useState, useEffect } from 'react';
import { ToggleLeft, ToggleRight, Trash2, Database, RefreshCw, PlayCircle, AlertCircle, CheckCircle, Loader } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import ApiService from '../../services/api.service';
import { SERVICES } from '../../utils/constants';

const ManagementTab = () => {
  const { 
    mockMode, 
    setMockMode, 
    fetchData, 
    setIsProcessingLogs, 
    setProcessingStatus,
    connectionStatus 
  } = useData();
  const [actionLoading, setActionLoading] = useState(false);
  const [actionMessage, setActionMessage] = useState(null);
  const [pollingInterval, setPollingInterval] = useState(null);

  // Clean up polling on unmount
  useEffect(() => {
    return () => {
      if (pollingInterval) {
        clearInterval(pollingInterval);
      }
    };
  }, [pollingInterval]);

  const pollForData = async () => {
    try {
      // Try to fetch latest downloads to see if processing is producing data
      const response = await fetch(`${import.meta.env.VITE_API_URL || `http://${window.location.hostname}:8080`}/api/downloads/latest`, {
        signal: AbortSignal.timeout(5000)
      });
      
      if (response.ok) {
        const data = await response.json();
        if (data && data.length > 0) {
          // We're getting data, processing might be complete or ongoing
          setProcessingStatus(prev => ({
            ...prev,
            message: `Processing logs... Found ${data.length} downloads so far`,
            progress: Math.min((prev?.progress || 0) + 5, 95)
          }));
          
          // Refresh the main data
          fetchData();
        }
      }
    } catch (err) {
      // Ignore errors during polling
      console.log('Polling error (expected during processing):', err.message);
    }
  };

  const handleAction = async (action, serviceName = null) => {
    if (mockMode) {
      setActionMessage({ 
        type: 'warning', 
        text: 'Actions are disabled in mock mode. Please disable mock mode first.' 
      });
      return;
    }

    setActionLoading(true);
    setActionMessage(null);
    
    // Special handling for process all logs
    if (action === 'processAllLogs') {
      setIsProcessingLogs(true);
      setProcessingStatus({
        message: 'Starting log processing...',
        progress: 0
      });
    }
    
    try {
      let result;
      switch(action) {
        case 'clearCache':
          result = await ApiService.clearCache(serviceName);
          break;
        case 'resetDatabase':
          result = await ApiService.resetDatabase();
          break;
        case 'resetLogs':
          result = await ApiService.resetLogPosition();
          break;
        case 'processAllLogs':
          result = await ApiService.processAllLogs();
          
          // Update status with result
          if (result.logSizeMB) {
            setProcessingStatus({
              message: `Processing ${result.logSizeMB.toFixed(1)} MB of logs...`,
              estimatedTime: `${result.estimatedTimeMinutes} minutes`,
              progress: 10
            });
            
            // Start polling for actual data
            const interval = setInterval(() => {
              pollForData();
            }, 10000); // Poll every 10 seconds
            
            setPollingInterval(interval);
            
            // Set a maximum timeout based on estimate
            const maxTimeout = Math.max(result.estimatedTimeMinutes * 60000, 300000); // At least 5 minutes
            
            setTimeout(() => {
              // Stop polling and mark as complete
              if (interval) {
                clearInterval(interval);
              }
              
              setIsProcessingLogs(false);
              setProcessingStatus({
                type: 'success',
                message: 'Log processing should be complete. Refreshing data...',
                progress: 100
              });
              
              // Final data refresh
              fetchData();
              
              // Clear success message after 5 seconds
              setTimeout(() => {
                setProcessingStatus(null);
              }, 5000);
            }, maxTimeout);
          }
          break;
        default:
          throw new Error('Unknown action');
      }
      
      setActionMessage({ 
        type: 'success', 
        text: result.message || `Action '${action}' completed successfully` 
      });
      
      // Refresh data after action (except for processAllLogs which handles its own)
      if (action !== 'processAllLogs') {
        setTimeout(fetchData, 1000);
      }
    } catch (err) {
      console.error(`Action ${action} failed:`, err);
      
      if (action === 'processAllLogs') {
        setIsProcessingLogs(false);
        setProcessingStatus(null);
        if (pollingInterval) {
          clearInterval(pollingInterval);
        }
      }
      
      // Provide more detailed error messages
      let errorMessage = 'Action failed: ';
      if (err.name === 'AbortError') {
        errorMessage = 'Request timeout - the operation is taking longer than expected';
      } else if (err.message.includes('Failed to fetch')) {
        errorMessage = 'Cannot connect to API. Please ensure the backend is running';
      } else if (err.message.includes('HTTP 404')) {
        errorMessage = 'API endpoint not found. Please check if the backend API is up to date.';
      } else if (err.message.includes('HTTP 500')) {
        errorMessage = 'Server error occurred. Check the backend logs for details.';
      } else {
        errorMessage += err.message;
      }
      
      setActionMessage({ 
        type: 'error', 
        text: errorMessage 
      });
    } finally {
      setActionLoading(false);
    }
  };

  // Manual refresh button for when processing
  const manualRefresh = async () => {
    setActionMessage({ type: 'info', text: 'Refreshing data...' });
    await fetchData();
    setActionMessage({ type: 'success', text: 'Data refreshed!' });
    setTimeout(() => setActionMessage(null), 3000);
  };

  return (
    <div className="space-y-6">
      {/* Connection Status Banner */}
      {connectionStatus !== 'connected' && (
        <div className="bg-yellow-900 bg-opacity-30 rounded-lg p-4 border border-yellow-700">
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-2 text-yellow-400">
              <AlertCircle className="w-5 h-5" />
              <span>API connection issues detected. Some features may not work.</span>
            </div>
            <button
              onClick={manualRefresh}
              className="px-3 py-1 bg-yellow-600 hover:bg-yellow-700 rounded text-sm text-white"
            >
              Retry
            </button>
          </div>
        </div>
      )}

      {/* Processing Notice */}
      {setIsProcessingLogs && (
        <div className="bg-blue-900 bg-opacity-30 rounded-lg p-4 border border-blue-700">
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-2 text-blue-400">
              <Loader className="w-5 h-5 animate-spin" />
              <span>Log processing in progress. Data will appear as logs are processed.</span>
            </div>
            <button
              onClick={manualRefresh}
              className="px-3 py-1 bg-blue-600 hover:bg-blue-700 rounded text-sm text-white"
            >
              Refresh Data
            </button>
          </div>
        </div>
      )}

      {/* Mock Mode Toggle */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <h3 className="text-lg font-semibold text-white mb-4">Mock Mode</h3>
        <div className="flex items-center justify-between">
          <div>
            <p className="text-gray-300">Enable mock data for demonstration</p>
            <p className="text-sm text-gray-500 mt-1">
              Simulates realistic cache data and download activity
            </p>
          </div>
          <button
            onClick={() => setMockMode(!mockMode)}
            className="flex items-center space-x-2 px-4 py-2 rounded-lg bg-blue-600 hover:bg-blue-700 transition-colors"
          >
            {mockMode ? <ToggleRight className="w-5 h-5" /> : <ToggleLeft className="w-5 h-5" />}
            <span>{mockMode ? 'Enabled' : 'Disabled'}</span>
          </button>
        </div>
        {mockMode && (
          <div className="mt-4 p-3 bg-blue-900 bg-opacity-30 rounded-lg border border-blue-700">
            <div className="flex items-center space-x-2 text-blue-400">
              <AlertCircle className="w-4 h-4" />
              <span className="text-sm">Mock mode is active - API actions are disabled</span>
            </div>
          </div>
        )}
      </div>

      {/* Cache Management */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <h3 className="text-lg font-semibold text-white mb-4">Cache Management</h3>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <button
            onClick={() => handleAction('clearCache')}
            disabled={actionLoading}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
            <span>Clear All Cache</span>
          </button>
          <button
            onClick={() => handleAction('resetDatabase')}
            disabled={actionLoading}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <Database className="w-4 h-4" />}
            <span>Reset Database</span>
          </button>
        </div>
      </div>

      {/* Log Processing */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <h3 className="text-lg font-semibold text-white mb-4">Log Processing</h3>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <button
            onClick={() => handleAction('resetLogs')}
            disabled={actionLoading}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-yellow-600 hover:bg-yellow-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <RefreshCw className="w-4 h-4" />}
            <span>Reset Log Position</span>
          </button>
          <button
            onClick={() => handleAction('processAllLogs')}
            disabled={actionLoading}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-green-600 hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <PlayCircle className="w-4 h-4" />}
            <span>Process All Logs</span>
          </button>
        </div>
        <div className="mt-4 p-3 bg-gray-700 rounded-lg">
          <p className="text-xs text-gray-400">
            <strong>Note:</strong> Processing large logs can take significant time. The dashboard will update as data becomes available. You can manually refresh or wait for automatic updates.
          </p>
        </div>
      </div>

      {/* Service-specific cache clear */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <h3 className="text-lg font-semibold text-white mb-4">Clear Service Cache</h3>
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
          {SERVICES.map(service => (
            <button
              key={service}
              onClick={() => handleAction('clearCache', service)}
              disabled={actionLoading}
              className="px-4 py-2 rounded-lg bg-gray-700 hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors capitalize"
            >
              Clear {service}
            </button>
          ))}
        </div>
      </div>

      {/* Action Messages */}
      {actionMessage && (
        <div className={`p-4 rounded-lg border ${
          actionMessage.type === 'success' 
            ? 'bg-green-900 bg-opacity-30 border-green-700 text-green-400' 
            : actionMessage.type === 'warning'
            ? 'bg-yellow-900 bg-opacity-30 border-yellow-700 text-yellow-400'
            : actionMessage.type === 'info'
            ? 'bg-blue-900 bg-opacity-30 border-blue-700 text-blue-400'
            : 'bg-red-900 bg-opacity-30 border-red-700 text-red-400'
        }`}>
          <div className="flex items-center space-x-2">
            {actionMessage.type === 'success' ? (
              <CheckCircle className="w-4 h-4" />
            ) : (
              <AlertCircle className="w-4 h-4" />
            )}
            <span>{actionMessage.text}</span>
          </div>
        </div>
      )}
    </div>
  );
};

export default ManagementTab;