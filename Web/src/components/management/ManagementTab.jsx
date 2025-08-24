import React, { useState, useEffect, useRef } from 'react';
import { ToggleLeft, ToggleRight, Trash2, Database, RefreshCw, PlayCircle, AlertCircle, CheckCircle, Loader, StopCircle, Info } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import ApiService from '../../services/api.service';
import { SERVICES } from '../../utils/constants';

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
  const [actionMessage, setActionMessage] = useState(null);
  
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

  // Check processing status on mount
  useEffect(() => {
    checkProcessingStatus();
  }, []);

  const checkProcessingStatus = async () => {
    try {
      const status = await ApiService.getProcessingStatus();
      
      if (status && status.isProcessing) {
        setIsProcessingLogs(true);
        
        let message = 'Processing logs...';
        let detailMessage = '';
        
        if (status.status === 'restarting') {
          message = 'Service is restarting to begin processing...';
          detailMessage = 'Please wait while the service restarts. Processing will begin shortly.';
        } else if (status.status === 'processing') {
          message = `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`;
          if (status.processingRate && status.processingRate > 0) {
            detailMessage = `Speed: ${status.processingRate.toFixed(1)} MB/s`;
          }
          if (status.downloadCount && status.downloadCount > 0) {
            detailMessage += ` • Found ${status.downloadCount} downloads`;
          }
        }
        
        setProcessingStatus({
          message,
          detailMessage,
          progress: status.percentComplete || 0,
          estimatedTime: status.estimatedTime,
          status: status.status
        });
        
        // Continue polling if processing - use ref to manage interval
        if (!statusPollingInterval.current) {
          statusPollingInterval.current = setInterval(checkProcessingStatus, 3000); // Check every 3 seconds
        }
        
        // Reset error logging flag on successful check
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
          
          // Clear the complete message after 5 seconds
          setTimeout(() => {
            setProcessingStatus(null);
          }, 5000);
          
          // Refresh data to show results
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
      // Only log error once, not repeatedly
      if (!processingErrorLogged.current) {
        console.error('Error checking processing status:', err);
        processingErrorLogged.current = true;
      }
      
      // If we get connection errors during processing, assume the service is restarting
      if (isProcessingLogs && (err.message?.includes('Failed to fetch') || err.name === 'AbortError')) {
        setProcessingStatus(prev => ({
          ...prev,
          message: 'Service is restarting...',
          detailMessage: 'Connection temporarily lost. This is normal during restart.',
          status: 'restarting'
        }));
        
        // Use a longer interval to reduce spam when having connection issues
        if (statusPollingInterval.current && !longIntervalSet.current) {
          clearInterval(statusPollingInterval.current);
          statusPollingInterval.current = setInterval(checkProcessingStatus, 5000); // Check every 5 seconds instead
          longIntervalSet.current = true;
        }
      }
      // Don't clear processing status on error - might just be a network issue
    }
  };

  const handleCancelProcessing = async () => {
    if (!confirm('Are you sure you want to cancel processing? The service will restart and return to normal monitoring.')) {
      return;
    }
    
    setActionLoading(true);
    setActionMessage(null);
    
    try {
      const result = await ApiService.cancelProcessing();
      
      setIsProcessingLogs(false);
      setProcessingStatus({
        message: 'Cancelling processing...',
        detailMessage: 'Service is restarting to stop processing',
        status: 'cancelling'
      });
      
      if (statusPollingInterval.current) {
        clearInterval(statusPollingInterval.current);
        statusPollingInterval.current = null;
      }
      
      setActionMessage({ 
        type: 'success', 
        text: result.message || 'Processing cancelled. Service is restarting...' 
      });
      
      // Clear status after a delay
      setTimeout(() => {
        setProcessingStatus(null);
        fetchData();
      }, 5000);
    } catch (err) {
      console.error('Cancel processing failed:', err);
      setActionMessage({ 
        type: 'error', 
        text: 'Failed to cancel processing' 
      });
    } finally {
      setActionLoading(false);
    }
  };

  const handleAction = async (action, serviceName = null) => {
    if (mockMode && action !== 'mockMode') {
      setActionMessage({ 
        type: 'warning', 
        text: 'Actions are disabled in mock mode. Please disable mock mode first.' 
      });
      return;
    }

    setActionLoading(true);
    setActionMessage(null);
    
    try {
      let result;
      switch(action) {
        case 'clearCache':
          result = await ApiService.clearCache(serviceName);
          break;
        case 'resetDatabase':
          if (!confirm('This will delete all data. Are you sure?')) {
            setActionLoading(false);
            return;
          }
          result = await ApiService.resetDatabase();
          break;
        case 'resetLogs':
          result = await ApiService.resetLogPosition();
          break;
        case 'processAllLogs':
          if (!confirm(`This will process the entire log file which may take a long time. The service will restart to begin processing. Continue?`)) {
            setActionLoading(false);
            return;
          }
          
          result = await ApiService.processAllLogs();
          
          if (result) {
            setIsProcessingLogs(true);
            setProcessingStatus({
              message: 'Preparing to process logs...',
              detailMessage: `${result.logSizeMB?.toFixed(1) || 0} MB to process. Service is restarting...`,
              progress: 0,
              estimatedTime: `Estimated: ${result.estimatedTimeMinutes} minutes`,
              status: 'restarting'
            });
            
            // Reset error flags
            processingErrorLogged.current = false;
            longIntervalSet.current = false;
            
            // Clear any existing interval
            if (statusPollingInterval.current) {
              clearInterval(statusPollingInterval.current);
            }
            
            // Start checking status after a delay (to allow restart)
            setTimeout(() => {
              checkProcessingStatus();
              statusPollingInterval.current = setInterval(checkProcessingStatus, 3000);
            }, 5000); // Wait 5 seconds before starting to poll
          }
          break;
        default:
          throw new Error('Unknown action');
      }
      
      if (result) {
        setActionMessage({ 
          type: 'success', 
          text: result.message || `Action completed successfully` 
        });
      }
      
      // Refresh data after action (except for processAllLogs)
      if (action !== 'processAllLogs') {
        setTimeout(fetchData, 2000);
      }
    } catch (err) {
      console.error(`Action ${action} failed:`, err);
      
      let errorMessage = 'Action failed: ';
      if (err.name === 'AbortError') {
        errorMessage = 'Request timeout';
      } else if (err.message.includes('Failed to fetch')) {
        errorMessage = 'Cannot connect to API';
      } else {
        errorMessage += err.message || 'Unknown error';
      }
      
      setActionMessage({ 
        type: 'error', 
        text: errorMessage 
      });
    } finally {
      setActionLoading(false);
    }
  };

  const manualRefresh = async () => {
    await fetchData();
    await checkProcessingStatus();
  };

  // Clear action messages after 5 seconds
  useEffect(() => {
    if (actionMessage) {
      const timer = setTimeout(() => setActionMessage(null), 5000);
      return () => clearTimeout(timer);
    }
  }, [actionMessage]);

  return (
    <div className="space-y-6">
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
              <div className="flex space-x-2 ml-4">
                <button
                  onClick={manualRefresh}
                  disabled={actionLoading}
                  className="px-3 py-2 bg-blue-600 hover:bg-blue-700 rounded text-sm text-white disabled:opacity-50"
                >
                  Refresh
                </button>
                <button
                  onClick={handleCancelProcessing}
                  disabled={actionLoading || processingStatus.status === 'cancelling'}
                  className="flex items-center space-x-2 px-4 py-2 bg-red-600 hover:bg-red-700 rounded text-white font-medium disabled:opacity-50"
                >
                  {actionLoading ? (
                    <Loader className="w-4 h-4 animate-spin" />
                  ) : (
                    <StopCircle className="w-4 h-4" />
                  )}
                  <span>Force Cancel</span>
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Connection Status Banner */}
      {connectionStatus !== 'connected' && !isProcessingLogs && (
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

      {/* Cache Management */}
      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <h3 className="text-lg font-semibold text-white mb-4">Cache Management</h3>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <button
            onClick={() => handleAction('clearCache')}
            disabled={actionLoading || isProcessingLogs || mockMode}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
            <span>Clear All Cache</span>
          </button>
          <button
            onClick={() => handleAction('resetDatabase')}
            disabled={actionLoading || isProcessingLogs || mockMode}
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
            <strong>Note:</strong> Processing large logs can take significant time. Use the Force Cancel button above to stop processing and save progress.
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
              disabled={actionLoading || isProcessingLogs || mockMode}
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