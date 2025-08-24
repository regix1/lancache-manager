import React, { useState, useEffect, useRef } from 'react';
import { ToggleLeft, ToggleRight, Trash2, Database, RefreshCw, PlayCircle, AlertCircle, CheckCircle, Loader, StopCircle, Info, HardDrive, FileText, X } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import ApiService from '../../services/api.service';

const ManagementTab = () => {
  const { 
    mockMode, 
    setMockMode, 
    fetchData, 
    isProcessingLogs,
    setIsProcessingLogs, 
    processingStatus,
    setProcessingStatus,
    connectionStatus 
  } = useData();
  
  const [actionLoading, setActionLoading] = useState(false);
  const [persistentErrors, setPersistentErrors] = useState([]);
  const [successMessage, setSuccessMessage] = useState(null);
  const [serviceCounts, setServiceCounts] = useState({});
  const [config, setConfig] = useState({
    cachePath: '/mnt/cache/cache',
    logPath: '/logs/access.log',
    services: []
  });
  
  // Use refs instead of state for interval management
  const statusPollingInterval = useRef(null);
  const processingErrorLogged = useRef(false);
  const longIntervalSet = useRef(false);

  // Clean up polling on unmount
  useEffect(() => {
    return () => {
      if (statusPollingInterval.current) {
        clearInterval(statusPollingInterval.current);
      }
    };
  }, []);

  // Load config and check processing status on mount
  useEffect(() => {
    loadConfig();
    checkProcessingStatus();
  }, []);

  const loadConfig = async () => {
    try {
      const configData = await ApiService.getConfig();
      setConfig(configData);
      
      // Load service counts for the discovered services
      const counts = await ApiService.getServiceLogCounts();
      setServiceCounts(counts);
    } catch (err) {
      console.error('Failed to load config:', err);
      // Use defaults if loading fails
      setConfig({
        cachePath: '/mnt/cache/cache',
        logPath: '/logs/access.log',
        services: ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']
      });
    }
  };

  const checkProcessingStatus = async () => {
    try {
      const status = await ApiService.getProcessingStatus();
      
      if (status && status.isProcessing) {
        setIsProcessingLogs(true);
        
        let message = 'Processing logs...';
        let detailMessage = '';
        
        if (status.status === 'processing') {
          message = `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`;
          if (status.processingRate && status.processingRate > 0) {
            detailMessage = `Speed: ${status.processingRate.toFixed(1)} MB/s`;
          }
        }
        
        setProcessingStatus({
          message,
          detailMessage,
          progress: status.percentComplete || 0,
          estimatedTime: status.estimatedTime,
          status: status.status
        });
        
        // Continue polling if processing
        if (!statusPollingInterval.current) {
          statusPollingInterval.current = setInterval(checkProcessingStatus, 3000);
        }
        
        processingErrorLogged.current = false;
        longIntervalSet.current = false;
        
      } else {
        // Not processing
        setIsProcessingLogs(false);
        
        // Check if we just completed
        if (status && status.percentComplete >= 100) {
          setProcessingStatus({
            message: 'Processing complete!',
            detailMessage: `Processed ${status.mbTotal?.toFixed(1) || 0} MB`,
            progress: 100,
            status: 'complete'
          });
          
          setTimeout(() => {
            setProcessingStatus(null);
          }, 5000);
          
          fetchData();
        } else {
          setProcessingStatus(null);
        }
        
        // Stop polling
        if (statusPollingInterval.current) {
          clearInterval(statusPollingInterval.current);
          statusPollingInterval.current = null;
        }
      }
    } catch (err) {
      if (!processingErrorLogged.current) {
        console.error('Error checking processing status:', err);
        processingErrorLogged.current = true;
      }
    }
  };

  const handleCancelProcessing = async () => {
    if (!confirm('Are you sure you want to cancel processing?')) {
      return;
    }
    
    setActionLoading(true);
    clearMessages();
    
    try {
      const result = await ApiService.cancelProcessing();
      
      setIsProcessingLogs(false);
      setProcessingStatus({
        message: 'Cancelling processing...',
        detailMessage: 'Stopping log processing',
        status: 'cancelling'
      });
      
      if (statusPollingInterval.current) {
        clearInterval(statusPollingInterval.current);
        statusPollingInterval.current = null;
      }
      
      setSuccessMessage(result.message || 'Processing cancelled');
      
      setTimeout(() => {
        setProcessingStatus(null);
        fetchData();
      }, 5000);
    } catch (err) {
      console.error('Cancel processing failed:', err);
      addError('Failed to cancel processing');
    } finally {
      setActionLoading(false);
    }
  };

  const handleAction = async (action, serviceName = null) => {
    if (mockMode && action !== 'mockMode') {
      addError('Actions are disabled in mock mode. Please disable mock mode first.');
      return;
    }

    setActionLoading(true);
    clearMessages();
    
    try {
      let result;
      switch(action) {
        case 'clearAllCache':
          if (!confirm('This will delete ALL cached game files (may be hundreds of GB). Are you sure?')) {
            setActionLoading(false);
            return;
          }
          result = await ApiService.clearAllCache();
          break;
          
        case 'resetDatabase':
          if (!confirm('This will delete all download history and statistics. Are you sure?')) {
            setActionLoading(false);
            return;
          }
          result = await ApiService.resetDatabase();
          break;
          
        case 'resetLogs':
          result = await ApiService.resetLogPosition();
          break;
          
        case 'processAllLogs':
          if (!confirm(`This will process the entire log file. Continue?`)) {
            setActionLoading(false);
            return;
          }
          
          result = await ApiService.processAllLogs();
          
          if (result) {
            setIsProcessingLogs(true);
            setProcessingStatus({
              message: 'Preparing to process logs...',
              detailMessage: `${result.logSizeMB?.toFixed(1) || 0} MB to process`,
              progress: 0,
              estimatedTime: `Estimated: ${result.estimatedTimeMinutes} minutes`,
              status: 'starting'
            });
            
            processingErrorLogged.current = false;
            longIntervalSet.current = false;
            
            if (statusPollingInterval.current) {
              clearInterval(statusPollingInterval.current);
            }
            
            setTimeout(() => {
              checkProcessingStatus();
              statusPollingInterval.current = setInterval(checkProcessingStatus, 3000);
            }, 5000);
          }
          break;
          
        case 'removeServiceLogs':
          if (!confirm(`This will permanently remove all ${serviceName} entries from the log file. A backup will be created. Continue?`)) {
            setActionLoading(false);
            return;
          }
          result = await ApiService.removeServiceFromLogs(serviceName);
          await loadConfig(); // Reload counts after removal
          break;
          
        default:
          throw new Error('Unknown action');
      }
      
      if (result) {
        setSuccessMessage(result.message || `Action completed successfully`);
      }
      
      // Refresh data after action (except for processAllLogs)
      if (action !== 'processAllLogs') {
        setTimeout(fetchData, 2000);
      }
    } catch (err) {
      console.error(`Action ${action} failed:`, err);
      
      // Parse error message
      let errorMessage = 'Action failed';
      if (err.message) {
        if (err.message.includes('read-only') || err.message.includes('Read-only')) {
          errorMessage = `Cannot modify log file: The logs directory is mounted as read-only. To modify logs, update your docker-compose.yml to mount logs with write permissions (remove ':ro' from the volume mount).`;
        } else if (err.message.includes('not found')) {
          errorMessage = err.message;
        } else if (err.name === 'AbortError') {
          errorMessage = 'Request timeout - operation may still be running';
        } else if (err.message.includes('Failed to fetch')) {
          errorMessage = 'Cannot connect to API server';
        } else {
          errorMessage = err.message;
        }
      }
      
      addError(errorMessage);
    } finally {
      setActionLoading(false);
    }
  };

  const addError = (message) => {
    setPersistentErrors(prev => [...prev, { id: Date.now(), message }]);
  };

  const clearMessages = () => {
    setPersistentErrors([]);
    setSuccessMessage(null);
  };

  const removeError = (id) => {
    setPersistentErrors(prev => prev.filter(err => err.id !== id));
  };

  // Clear success message after 10 seconds
  useEffect(() => {
    if (successMessage) {
      const timer = setTimeout(() => setSuccessMessage(null), 10000);
      return () => clearTimeout(timer);
    }
  }, [successMessage]);

  return (
    <div className="space-y-6">
      {/* Persistent Error Messages */}
      {persistentErrors.length > 0 && (
        <div className="space-y-2">
          {persistentErrors.map(error => (
            <div key={error.id} className="bg-red-900 bg-opacity-30 rounded-lg p-4 border border-red-700">
              <div className="flex items-start justify-between">
                <div className="flex items-start space-x-2 flex-1">
                  <AlertCircle className="w-5 h-5 text-red-400 mt-0.5 flex-shrink-0" />
                  <span className="text-red-400">{error.message}</span>
                </div>
                <button
                  onClick={() => removeError(error.id)}
                  className="ml-4 text-red-400 hover:text-red-300"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Success Message */}
      {successMessage && (
        <div className="bg-green-900 bg-opacity-30 rounded-lg p-4 border border-green-700">
          <div className="flex items-center space-x-2">
            <CheckCircle className="w-5 h-5 text-green-400" />
            <span className="text-green-400">{successMessage}</span>
          </div>
        </div>
      )}

      {/* Processing Status Banner */}
      {isProcessingLogs && processingStatus && (
        <div className={`rounded-lg p-4 border ${
          processingStatus.status === 'complete' 
            ? 'bg-green-900 bg-opacity-30 border-green-700'
            : 'bg-yellow-900 bg-opacity-30 border-yellow-700'
        }`}>
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-3 flex-1">
              {processingStatus.status === 'complete' ? (
                <CheckCircle className="w-5 h-5 text-green-500" />
              ) : (
                <Loader className="w-5 h-5 text-yellow-500 animate-spin" />
              )}
              <div className="flex-1">
                <p className={`font-medium ${
                  processingStatus.status === 'complete' ? 'text-green-400' : 'text-yellow-400'
                }`}>
                  {processingStatus.message}
                </p>
                {processingStatus.detailMessage && (
                  <p className="text-sm text-gray-300 mt-1">{processingStatus.detailMessage}</p>
                )}
                {processingStatus.progress !== undefined && processingStatus.progress > 0 && processingStatus.status === 'processing' && (
                  <div className="mt-2">
                    <div className="w-full bg-gray-700 rounded-full h-2">
                      <div 
                        className="bg-yellow-500 h-2 rounded-full transition-all duration-500"
                        style={{ width: `${Math.min(processingStatus.progress || 0, 100)}%` }}
                      />
                    </div>
                    <p className="text-xs text-gray-400 mt-1">
                      {(processingStatus.progress || 0).toFixed(1)}% complete
                      {processingStatus.estimatedTime && ` • ${processingStatus.estimatedTime} remaining`}
                    </p>
                  </div>
                )}
              </div>
            </div>
            {processingStatus.status !== 'complete' && (
              <button
                onClick={handleCancelProcessing}
                disabled={actionLoading}
                className="flex items-center space-x-2 px-4 py-2 bg-red-600 hover:bg-red-700 rounded text-white font-medium disabled:opacity-50 ml-4"
              >
                {actionLoading ? (
                  <Loader className="w-4 h-4 animate-spin" />
                ) : (
                  <StopCircle className="w-4 h-4" />
                )}
                <span>Cancel</span>
              </button>
            )}
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
            disabled={isProcessingLogs}
            className="flex items-center space-x-2 px-4 py-2 rounded-lg bg-blue-600 hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
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

      {/* Disk Cache Management */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <HardDrive className="w-5 h-5 text-blue-400" />
          <h3 className="text-lg font-semibold text-white">Disk Cache Management</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Manage cached game files stored on disk in <code className="bg-gray-700 px-2 py-1 rounded">{config.cachePath}</code>
        </p>
        <button
          onClick={() => handleAction('clearAllCache')}
          disabled={actionLoading || isProcessingLogs || mockMode}
          className="flex items-center justify-center space-x-2 px-4 py-3 w-full rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
          <span>Clear All Cached Files</span>
        </button>
        <p className="text-xs text-gray-500 mt-2">
          ⚠️ This deletes ALL cached game files from disk to free up space
        </p>
      </div>

      {/* Database Management */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <Database className="w-5 h-5 text-purple-400" />
          <h3 className="text-lg font-semibold text-white">Database Management</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Manage download history and statistics stored in the database
        </p>
        <button
          onClick={() => handleAction('resetDatabase')}
          disabled={actionLoading || isProcessingLogs || mockMode}
          className="flex items-center justify-center space-x-2 px-4 py-3 w-full rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <Database className="w-4 h-4" />}
          <span>Reset Database</span>
        </button>
        <p className="text-xs text-gray-500 mt-2">
          Clears all download history and statistics (does not affect cached files)
        </p>
      </div>

      {/* Log Processing */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <FileText className="w-5 h-5 text-green-400" />
          <h3 className="text-lg font-semibold text-white">Log Processing</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Control how the access.log file is processed for statistics
        </p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <button
            onClick={() => handleAction('resetLogs')}
            disabled={actionLoading || isProcessingLogs || mockMode}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-yellow-600 hover:bg-yellow-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <RefreshCw className="w-4 h-4" />}
            <span>Reset Log Position</span>
          </button>
          <button
            onClick={() => handleAction('processAllLogs')}
            disabled={actionLoading || isProcessingLogs || mockMode}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-green-600 hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <PlayCircle className="w-4 h-4" />}
            <span>Process All Logs</span>
          </button>
        </div>
        <div className="mt-4 p-3 bg-gray-700 rounded-lg">
          <p className="text-xs text-gray-400">
            <strong>Reset:</strong> Start monitoring from current end of log file<br/>
            <strong>Process All:</strong> Import entire log history into database
          </p>
        </div>
      </div>

      {/* Log File Management */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <FileText className="w-5 h-5 text-orange-400" />
          <h3 className="text-lg font-semibold text-white">Log File Management</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Remove specific service entries from <code className="bg-gray-700 px-2 py-1 rounded">{config.logPath}</code>
        </p>
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
          {/* Show discovered services or defaults */}
          {(config.services.length > 0 ? config.services : ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']).map(service => {
            const count = serviceCounts[service];
            return (
              <button
                key={service}
                onClick={() => handleAction('removeServiceLogs', service)}
                disabled={actionLoading || isProcessingLogs || mockMode}
                className="px-4 py-3 rounded-lg bg-gray-700 hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex flex-col items-center"
              >
                <span className="capitalize font-medium">Clear {service}</span>
                <span className="text-xs text-gray-400 mt-1">
                  {count !== undefined ? `(${count.toLocaleString()} entries)` : ''}
                </span>
              </button>
            );
          })}
        </div>
        <div className="mt-4 p-3 bg-yellow-900 bg-opacity-30 rounded-lg border border-yellow-700">
          <p className="text-xs text-yellow-400">
            <strong>Warning:</strong> Requires write permissions to logs directory. Check container logs if you get permission errors.
          </p>
        </div>
      </div>
    </div>
  );
};

export default ManagementTab;