import React, { useState, useEffect, useRef } from 'react';
import { ToggleLeft, ToggleRight, Trash2, Database, RefreshCw, PlayCircle, AlertCircle, CheckCircle, Loader, StopCircle, Info, HardDrive, FileText, X, Eye } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import ApiService from '../../services/api.service';
import * as signalR from '@microsoft/signalr';

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
  
  // Cache clearing state
  const [cacheClearOperation, setCacheClearOperation] = useState(null);
  const [cacheClearProgress, setCacheClearProgress] = useState(null);
  const [showCacheClearModal, setShowCacheClearModal] = useState(false);
  
  // Use refs instead of state for interval management
  const statusPollingInterval = useRef(null);
  const processingErrorLogged = useRef(false);
  const longIntervalSet = useRef(false);
  const cacheClearPollingInterval = useRef(null);
  const signalRConnection = useRef(null);

  // Clean up polling on unmount
  useEffect(() => {
    return () => {
      if (statusPollingInterval.current) {
        clearInterval(statusPollingInterval.current);
      }
      if (cacheClearPollingInterval.current) {
        clearInterval(cacheClearPollingInterval.current);
      }
      if (signalRConnection.current) {
        signalRConnection.current.stop();
      }
    };
  }, []);

  // Load config and check processing status on mount
  useEffect(() => {
    loadConfig();
    checkProcessingStatus();
    setupSignalR();
    checkForActiveCacheOperations();
  }, []);

  const checkForActiveCacheOperations = async () => {
    try {
      const operations = await ApiService.getActiveCacheOperations();
      if (operations && operations.length > 0) {
        const activeOp = operations.find(op => 
          op.status === 'Running' || op.status === 'Preparing'
        );
        if (activeOp) {
          setCacheClearOperation(activeOp.operationId);
          setCacheClearProgress(activeOp);
          startCacheClearPolling(activeOp.operationId);
        }
      }
    } catch (err) {
      console.log('No active cache operations or unable to check');
    }
  };

  const setupSignalR = async () => {
    try {
      const apiUrl = import.meta.env.VITE_API_URL || `http://${window.location.hostname}:8080`;
      const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${apiUrl}/hubs/downloads`)
        .withAutomaticReconnect()
        .build();

      connection.on('CacheClearProgress', (progress) => {
        console.log('Cache clear progress:', progress);
        
        // Update progress state directly
        setCacheClearProgress(progress);
        
        // Check if operation is complete
        if (progress.status === 'Completed' || progress.status === 'Failed' || progress.status === 'Cancelled') {
          handleCacheClearComplete(progress);
        }
      });

      await connection.start();
      signalRConnection.current = connection;
      console.log('SignalR connected for cache clearing updates');
    } catch (err) {
      console.error('SignalR connection failed:', err);
      // Fall back to polling
    }
  };

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

  const handleClearAllCache = async () => {
    if (!confirm('This will instantly clear ALL cached game files. The old cache will be deleted in the background. Continue?')) {
      return;
    }
    
    setActionLoading(true);
    clearMessages();
    
    try {
      // Start the cache clearing operation
      const result = await ApiService.clearAllCache();
      
      if (result.operationId) {
        const opId = result.operationId;
        setCacheClearOperation(opId);
        setCacheClearProgress({
          operationId: opId,
          status: 'Preparing',
          statusMessage: 'Starting cache clear...',
          percentComplete: 0,
          bytesDeleted: 0,
          totalBytesToDelete: 0,
          directoriesProcessed: 0,
          totalDirectories: 4
        });
        setShowCacheClearModal(true);
        
        // Start polling immediately (SignalR might not be ready yet)
        startCacheClearPolling(opId);
      }
    } catch (err) {
      console.error('Start cache clear failed:', err);
      addError('Failed to start cache clearing: ' + err.message);
    } finally {
      setActionLoading(false);
    }
  };

  const startCacheClearPolling = (operationId) => {
    if (cacheClearPollingInterval.current) {
      clearInterval(cacheClearPollingInterval.current);
    }
    
    // Poll immediately, then every 1 second for instant operations
    const pollStatus = async () => {
      try {
        const status = await ApiService.getCacheClearStatus(operationId);
        console.log('Polled cache clear status:', status);
        setCacheClearProgress(status);
        
        // Check if operation is complete
        if (status.status === 'Completed' || status.status === 'Failed' || status.status === 'Cancelled') {
          handleCacheClearComplete(status);
          clearInterval(cacheClearPollingInterval.current);
          cacheClearPollingInterval.current = null;
        }
      } catch (err) {
        console.error('Error polling cache clear status:', err);
      }
    };
    
    // Poll immediately
    pollStatus();
    
    // Then poll every second
    cacheClearPollingInterval.current = setInterval(pollStatus, 1000);
  };

  const handleCacheClearComplete = (progress) => {
    // Stop polling
    if (cacheClearPollingInterval.current) {
      clearInterval(cacheClearPollingInterval.current);
      cacheClearPollingInterval.current = null;
    }
    
    // Show completion message
    if (progress.status === 'Completed') {
      const sizeCleared = formatBytes(progress.bytesDeleted || 0);
      setSuccessMessage(`Cache cleared successfully! ${sizeCleared} freed instantly. Old cache is being deleted in background.`);
      // Auto-close modal after 2 seconds for successful completion
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearOperation(null);
        setCacheClearProgress(null);
      }, 2000);
    } else if (progress.status === 'Failed') {
      addError(`Cache clearing failed: ${progress.error || 'Unknown error'}`);
      // Keep modal open for failed operations so user can see the error
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearOperation(null);
        setCacheClearProgress(null);
      }, 5000);
    } else if (progress.status === 'Cancelled') {
      setSuccessMessage('Cache clearing cancelled');
      // Close modal immediately for cancelled operations
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearOperation(null);
        setCacheClearProgress(null);
      }, 500);
    }
    
    // Refresh data
    fetchData();
  };

  const handleCancelCacheClear = async () => {
    if (!cacheClearOperation) return;
    
    try {
      // Immediately update UI to show cancelling
      setCacheClearProgress(prev => ({ 
        ...prev, 
        status: 'Cancelling',
        statusMessage: 'Cancelling operation...'
      }));
      
      // Send cancel request
      await ApiService.cancelCacheClear(cacheClearOperation);
      
      // Wait a moment for any final updates
      setTimeout(() => {
        // Force close the modal after cancellation
        setShowCacheClearModal(false);
        setCacheClearOperation(null);
        setCacheClearProgress(null);
        
        // Stop polling if active
        if (cacheClearPollingInterval.current) {
          clearInterval(cacheClearPollingInterval.current);
          cacheClearPollingInterval.current = null;
        }
        
        // Show success message
        setSuccessMessage('Cache clearing operation cancelled');
      }, 1500);
      
    } catch (err) {
      console.error('Failed to cancel cache clear:', err);
      // Even if cancel fails, close the modal
      setShowCacheClearModal(false);
      setCacheClearOperation(null);
      setCacheClearProgress(null);
      addError('Failed to cancel operation, but closed the dialog');
    }
  };

  const formatBytes = (bytes) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const handleAction = async (action, serviceName = null) => {
    if (mockMode && action !== 'mockMode') {
      addError('Actions are disabled in mock mode. Please disable mock mode first.');
      return;
    }

    if (action === 'clearAllCache') {
      handleClearAllCache();
      return;
    }

    setActionLoading(true);
    clearMessages();
    
    try {
      let result;
      switch(action) {
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

  // Add a useEffect to handle stuck modals (safeguard)
  useEffect(() => {
    if (showCacheClearModal && cacheClearProgress) {
      // If modal has been showing "Cancelling" for more than 5 seconds, force close
      if (cacheClearProgress.status === 'Cancelling') {
        const timeout = setTimeout(() => {
          console.log('Force closing stuck cache clear modal');
          setShowCacheClearModal(false);
          setCacheClearOperation(null);
          setCacheClearProgress(null);
          
          if (cacheClearPollingInterval.current) {
            clearInterval(cacheClearPollingInterval.current);
            cacheClearPollingInterval.current = null;
          }
        }, 5000);
        
        return () => clearTimeout(timeout);
      }
    }
  }, [showCacheClearModal, cacheClearProgress?.status]);

  // Check if cache clearing is running in background
  const isCacheClearingInBackground = cacheClearOperation && 
    !showCacheClearModal && 
    cacheClearProgress && 
    (cacheClearProgress.status === 'Running' || cacheClearProgress.status === 'Preparing');

  return (
    <div className="space-y-6">
      {/* Background Cache Clear Status Bar */}
      {isCacheClearingInBackground && (
        <div className="bg-blue-900 bg-opacity-30 rounded-lg p-4 border border-blue-700">
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-3 flex-1">
              <Loader className="w-5 h-5 text-blue-500 animate-spin" />
              <div className="flex-1">
                <p className="font-medium text-blue-400">
                  Cache clearing in progress...
                </p>
                <div className="flex items-center space-x-4 mt-1">
                  <span className="text-sm text-gray-300">
                    {(cacheClearProgress.percentComplete || 0).toFixed(0)}% complete
                  </span>
                  {cacheClearProgress.bytesDeleted > 0 && (
                    <span className="text-sm text-green-400">
                      {formatBytes(cacheClearProgress.bytesDeleted)} cleared
                    </span>
                  )}
                </div>
              </div>
            </div>
            <button
              onClick={() => setShowCacheClearModal(true)}
              className="flex items-center space-x-2 px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded text-white font-medium ml-4"
            >
              <Eye className="w-4 h-4" />
              <span>View Details</span>
            </button>
          </div>
        </div>
      )}

      {/* Cache Clear Modal */}
      {showCacheClearModal && cacheClearProgress && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="bg-gray-800 rounded-lg p-6 max-w-lg w-full mx-4 border border-gray-700">
            <h3 className="text-lg font-semibold text-white mb-4">Clearing Cache</h3>
            
            <div className="space-y-4">
              {/* Status Message */}
              <div className="flex items-center space-x-2">
                {cacheClearProgress.status === 'Completed' ? (
                  <CheckCircle className="w-5 h-5 text-green-500" />
                ) : cacheClearProgress.status === 'Failed' ? (
                  <AlertCircle className="w-5 h-5 text-red-500" />
                ) : cacheClearProgress.status === 'Cancelled' ? (
                  <X className="w-5 h-5 text-yellow-500" />
                ) : (
                  <Loader className="w-5 h-5 text-blue-500 animate-spin" />
                )}
                <div className="flex-1">
                  <span className="text-white">{cacheClearProgress.status}</span>
                  {cacheClearProgress.statusMessage && (
                    <p className="text-sm text-gray-400">{cacheClearProgress.statusMessage}</p>
                  )}
                </div>
              </div>
              
              {/* Progress Bar */}
              {(cacheClearProgress.status === 'Running' || cacheClearProgress.status === 'Preparing') && (
                <>
                  <div className="w-full bg-gray-700 rounded-full h-4 relative overflow-hidden">
                    <div 
                      className="bg-gradient-to-r from-blue-500 to-blue-600 h-4 rounded-full transition-all duration-500 relative"
                      style={{ width: `${Math.max(0, Math.min(100, cacheClearProgress.percentComplete || 0))}%` }}
                    >
                      {/* Animated stripes for active progress */}
                      <div className="absolute inset-0 bg-stripes animate-slide opacity-20"></div>
                    </div>
                    <div className="absolute inset-0 flex items-center justify-center">
                      <span className="text-xs text-white font-medium drop-shadow">
                        {(cacheClearProgress.percentComplete || 0).toFixed(0)}%
                      </span>
                    </div>
                  </div>
                  
                  {/* Bytes deleted progress */}
                  {cacheClearProgress.totalBytesToDelete > 0 && (
                    <div className="text-sm text-gray-400 text-center">
                      <span className="text-green-400 font-semibold">
                        {formatBytes(cacheClearProgress.bytesDeleted || 0)}
                      </span>
                      {' / '}
                      <span className="text-white">
                        {formatBytes(cacheClearProgress.totalBytesToDelete)}
                      </span>
                      {' cleared'}
                    </div>
                  )}
                </>
              )}
              
              {/* Stats Grid - Simplified for nuclear method */}
              {cacheClearProgress.status === 'Completed' && (
                <div className="grid grid-cols-2 gap-4 text-sm">
                  <div className="bg-gray-900 rounded p-3">
                    <div className="text-gray-400 text-xs uppercase mb-1">Space Freed</div>
                    <div className="text-green-400 font-semibold">
                      {formatBytes(cacheClearProgress.bytesDeleted || 0)}
                    </div>
                  </div>
                  
                  <div className="bg-gray-900 rounded p-3">
                    <div className="text-gray-400 text-xs uppercase mb-1">Time Taken</div>
                    <div className="text-white font-semibold">
                      {cacheClearProgress.endTime && cacheClearProgress.startTime
                        ? `${((new Date(cacheClearProgress.endTime) - new Date(cacheClearProgress.startTime)) / 1000).toFixed(1)}s`
                        : 'N/A'}
                    </div>
                  </div>
                </div>
              )}
              
              {/* Error message */}
              {cacheClearProgress.error && (
                <div className="p-3 bg-red-900 bg-opacity-30 rounded border border-red-700">
                  <p className="text-sm text-red-400">{cacheClearProgress.error}</p>
                </div>
              )}
              
              {/* Actions */}
              <div className="flex justify-end space-x-3 pt-4 border-t border-gray-700">
                {(cacheClearProgress.status === 'Running' || cacheClearProgress.status === 'Preparing') ? (
                  <>
                    <button
                      onClick={handleCancelCacheClear}
                      className="px-4 py-2 bg-red-600 hover:bg-red-700 rounded text-white flex items-center space-x-2"
                    >
                      <StopCircle className="w-4 h-4" />
                      <span>Cancel</span>
                    </button>
                    <button
                      onClick={() => setShowCacheClearModal(false)}
                      className="px-4 py-2 bg-gray-600 hover:bg-gray-700 rounded text-white"
                    >
                      Run in Background
                    </button>
                  </>
                ) : (
                  <button
                    onClick={() => {
                      setShowCacheClearModal(false);
                      setCacheClearOperation(null);
                      setCacheClearProgress(null);
                    }}
                    className="px-4 py-2 bg-gray-600 hover:bg-gray-700 rounded text-white"
                  >
                    Close
                  </button>
                )}
              </div>
            </div>
          </div>
        </div>
      )}

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
          disabled={actionLoading || isProcessingLogs || mockMode || showCacheClearModal || isCacheClearingInBackground}
          className="flex items-center justify-center space-x-2 px-4 py-3 w-full rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
          <span>{isCacheClearingInBackground ? 'Cache Clearing in Progress...' : 'Clear All Cached Files'}</span>
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