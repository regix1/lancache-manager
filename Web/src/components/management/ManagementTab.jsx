import React, { useState, useEffect, useRef, useCallback } from 'react';
import { ToggleLeft, ToggleRight, Trash2, Database, RefreshCw, PlayCircle, AlertCircle, CheckCircle, Loader, StopCircle, HardDrive, FileText, X, Eye } from 'lucide-react';
import { useData } from '../../contexts/DataContext';
import ApiService from '../../services/api.service';
import operationStateService from '../../services/operationState.service';
import { useBackendOperation } from '../../hooks/useBackendOperation';
import * as signalR from '@microsoft/signalr';

// Helper function to format bytes
const formatBytes = (bytes) => {
  if (!bytes || bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

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
  
  // State management
  const [actionLoading, setActionLoading] = useState(false);
  const [alerts, setAlerts] = useState({ errors: [], success: null });
  const [serviceCounts, setServiceCounts] = useState({});
  const [config, setConfig] = useState({
    cachePath: '/mnt/cache/cache',
    logPath: '/logs/access.log',
    services: []
  });
  const [hasMigrated, setHasMigrated] = useState(false);
  
  // Cache clearing state
  const [cacheClearProgress, setCacheClearProgress] = useState(null);
  const [showCacheClearModal, setShowCacheClearModal] = useState(false);
  
  // Backend operations using new hook
  const cacheOp = useBackendOperation('activeCacheClearOperation', 'cacheClearing', 30);
  const logProcessingOp = useBackendOperation('activeLogProcessing', 'logProcessing', 120);
  const serviceRemovalOp = useBackendOperation('activeServiceRemoval', 'serviceRemoval', 30);
  
  // Refs for intervals and connections
  const intervals = useRef({});
  const signalRConnection = useRef(null);

  // Alert management helpers
  const addError = useCallback((message) => {
    setAlerts(prev => ({
      ...prev,
      errors: [...prev.errors, { id: Date.now(), message }]
    }));
  }, []);

  const setSuccess = useCallback((message) => {
    setAlerts(prev => ({ ...prev, success: message }));
    setTimeout(() => setAlerts(prev => ({ ...prev, success: null })), 10000);
  }, []);

  const clearAlerts = useCallback(() => {
    setAlerts({ errors: [], success: null });
  }, []);

  // Cleanup function for intervals
  const clearInterval = useCallback((name) => {
    if (intervals.current[name]) {
      window.clearInterval(intervals.current[name]);
      intervals.current[name] = null;
    }
  }, []);

  const setInterval = useCallback((name, callback, delay) => {
    clearInterval(name);
    intervals.current[name] = window.setInterval(callback, delay);
  }, [clearInterval]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      Object.keys(intervals.current).forEach(clearInterval);
      if (signalRConnection.current) {
        signalRConnection.current.stop();
      }
    };
  }, [clearInterval]);

  // Initialize on mount with migration
  useEffect(() => {
    const initialize = async () => {
      // One-time migration from localStorage
      if (!hasMigrated) {
        const migrated = await operationStateService.migrateFromLocalStorage();
        if (migrated > 0) {
          setSuccess(`Migrated ${migrated} operations from local storage to server`);
        }
        setHasMigrated(true);
      }
      
      await loadConfig();
      await restoreOperations();
      setupSignalR();
    };
    
    initialize();
  }, []);

  const loadConfig = async () => {
    try {
      const [configData, counts] = await Promise.all([
        ApiService.getConfig(),
        ApiService.getServiceLogCounts()
      ]);
      setConfig(configData);
      setServiceCounts(counts);
    } catch (err) {
      console.error('Failed to load config:', err);
      setConfig({
        cachePath: '/mnt/cache/cache',
        logPath: '/logs/access.log',
        services: ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']
      });
    }
  };

  const restoreOperations = async () => {
    // Restore log processing
    const logOp = await logProcessingOp.load();
    if (logOp?.data) {
      const status = await ApiService.getProcessingStatus().catch(() => null);
      if (status?.isProcessing) {
        setIsProcessingLogs(true);
        setProcessingStatus({
          message: `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`,
          detailMessage: status.processingRate ? `Speed: ${status.processingRate.toFixed(1)} MB/s` : '',
          progress: status.percentComplete || 0,
          estimatedTime: status.estimatedTime,
          status: status.status
        });
        startProcessingPolling();
      } else {
        await logProcessingOp.clear();
      }
    }

    // Restore cache clearing
    const cacheClear = await cacheOp.load();
    if (cacheClear?.data?.operationId) {
      try {
        const status = await ApiService.getCacheClearStatus(cacheClear.data.operationId);
        if (status && ['Running', 'Preparing'].includes(status.status)) {
          setCacheClearProgress(status);
          startCacheClearPolling(cacheClear.data.operationId);
        } else {
          await cacheOp.clear();
        }
      } catch (err) {
        await cacheOp.clear();
      }
    }

    // Restore service removal
    const serviceOp = await serviceRemovalOp.load();
    if (serviceOp?.data?.service) {
      setSuccess(`Removing ${serviceOp.data.service} entries from logs (operation resumed)...`);
      setTimeout(async () => {
        await serviceRemovalOp.clear();
        loadConfig();
        fetchData();
      }, 10000);
    }
  };

  const setupSignalR = async () => {
    try {
      const apiUrl = import.meta.env.VITE_API_URL || `http://${window.location.hostname}:8080`;
      const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${apiUrl}/hubs/downloads`)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

      connection.on('CacheClearProgress', async (progress) => {
        setCacheClearProgress(progress);
        if (progress.operationId) {
          await cacheOp.update({ lastProgress: progress.percentComplete || 0 });
        }
        if (['Completed', 'Failed', 'Cancelled'].includes(progress.status)) {
          handleCacheClearComplete(progress);
        }
      });

      await connection.start();
      signalRConnection.current = connection;
      console.log('SignalR connected');
    } catch (err) {
      console.error('SignalR connection failed, falling back to polling:', err);
    }
  };

  // Polling functions
  const startProcessingPolling = useCallback(() => {
    const checkStatus = async () => {
      try {
        const status = await ApiService.getProcessingStatus();
        if (status?.isProcessing) {
          setIsProcessingLogs(true);
          setProcessingStatus({
            message: `Processing: ${status.mbProcessed?.toFixed(1) || 0} MB of ${status.mbTotal?.toFixed(1) || 0} MB`,
            detailMessage: status.processingRate ? `Speed: ${status.processingRate.toFixed(1)} MB/s` : '',
            progress: status.percentComplete || 0,
            estimatedTime: status.estimatedTime,
            status: status.status
          });
          await logProcessingOp.update({ 
            lastProgress: status.percentComplete || 0,
            mbProcessed: status.mbProcessed,
            mbTotal: status.mbTotal
          });
        } else {
          setIsProcessingLogs(false);
          await logProcessingOp.clear();
          clearInterval('processing');
          
          if (status?.percentComplete >= 100) {
            setProcessingStatus({
              message: 'Processing complete!',
              detailMessage: `Processed ${status.mbTotal?.toFixed(1) || 0} MB`,
              progress: 100,
              status: 'complete'
            });
            setTimeout(() => setProcessingStatus(null), 5000);
            fetchData();
          }
        }
      } catch (err) {
        console.error('Error checking processing status:', err);
      }
    };
    
    checkStatus();
    setInterval('processing', checkStatus, 3000);
  }, [setIsProcessingLogs, setProcessingStatus, logProcessingOp, clearInterval, setInterval, fetchData]);

  const startCacheClearPolling = useCallback((operationId) => {
    const pollStatus = async () => {
      try {
        const status = await ApiService.getCacheClearStatus(operationId);
        setCacheClearProgress(status);
        
        if (['Running', 'Preparing'].includes(status.status)) {
          await cacheOp.update({ lastProgress: status.percentComplete || 0 });
        } else {
          handleCacheClearComplete(status);
          clearInterval('cacheClearing');
        }
      } catch (err) {
        console.error('Error polling cache clear status:', err);
      }
    };
    
    pollStatus();
    setInterval('cacheClearing', pollStatus, 1000);
  }, [cacheOp, clearInterval, setInterval]);

  const handleCacheClearComplete = useCallback(async (progress) => {
    clearInterval('cacheClearing');
    await cacheOp.clear();
    
    if (progress.status === 'Completed') {
      setSuccess(`Cache cleared successfully! ${formatBytes(progress.bytesDeleted || 0)} freed.`);
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearProgress(null);
      }, 2000);
    } else if (progress.status === 'Failed') {
      addError(`Cache clearing failed: ${progress.error || 'Unknown error'}`);
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearProgress(null);
      }, 5000);
    } else if (progress.status === 'Cancelled') {
      setSuccess('Cache clearing cancelled');
      setShowCacheClearModal(false);
      setCacheClearProgress(null);
    }
    
    fetchData();
  }, [clearInterval, cacheOp, setSuccess, addError, fetchData]);

  // Action handlers
  const handleClearAllCache = async () => {
    if (!confirm('This will clear ALL cached game files. Continue?')) return;
    
    setActionLoading(true);
    clearAlerts();
    
    try {
      const result = await ApiService.clearAllCache();
      if (result.operationId) {
        await cacheOp.save({ operationId: result.operationId });
        setCacheClearProgress({
          operationId: result.operationId,
          status: 'Preparing',
          statusMessage: 'Starting cache clear...',
          percentComplete: 0,
          bytesDeleted: 0,
          totalBytesToDelete: 0
        });
        setShowCacheClearModal(true);
        startCacheClearPolling(result.operationId);
      }
    } catch (err) {
      addError('Failed to start cache clearing: ' + err.message);
    } finally {
      setActionLoading(false);
    }
  };

  const handleCancelCacheClear = async () => {
    if (!cacheOp.operation?.data?.operationId) return;
    
    try {
      setCacheClearProgress(prev => ({ 
        ...prev, 
        status: 'Cancelling',
        statusMessage: 'Cancelling operation...'
      }));
      
      await ApiService.cancelCacheClear(cacheOp.operation.data.operationId);
      await cacheOp.clear();
      
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearProgress(null);
        clearInterval('cacheClearing');
        setSuccess('Cache clearing operation cancelled');
      }, 1500);
    } catch (err) {
      console.error('Failed to cancel cache clear:', err);
      setShowCacheClearModal(false);
      setCacheClearProgress(null);
      await cacheOp.clear();
    }
  };

  const handleCancelProcessing = async () => {
    if (!confirm('Cancel log processing?')) return;
    
    setActionLoading(true);
    try {
      await ApiService.cancelProcessing();
      setIsProcessingLogs(false);
      await logProcessingOp.clear();
      clearInterval('processing');
      setSuccess('Processing cancelled');
      setTimeout(() => {
        setProcessingStatus(null);
        fetchData();
      }, 5000);
    } catch (err) {
      addError('Failed to cancel processing');
    } finally {
      setActionLoading(false);
    }
  };

  const handleAction = async (action, serviceName = null) => {
    if (mockMode && action !== 'mockMode') {
      addError('Actions disabled in mock mode');
      return;
    }

    setActionLoading(true);
    clearAlerts();
    
    try {
      let result;
      switch(action) {
        case 'resetDatabase':
          if (!confirm('Delete all download history?')) {
            setActionLoading(false);
            return;
          }
          result = await ApiService.resetDatabase();
          break;
          
        case 'resetLogs':
          result = await ApiService.resetLogPosition();
          break;
          
        case 'processAllLogs':
          if (!confirm('Process entire log file?')) {
            setActionLoading(false);
            return;
          }
          
          await logProcessingOp.save({ type: 'processAll' });
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
            setTimeout(() => startProcessingPolling(), 5000);
          } else {
            await logProcessingOp.clear();
          }
          break;
          
        case 'removeServiceLogs':
          if (!confirm(`Remove all ${serviceName} entries?`)) {
            setActionLoading(false);
            return;
          }
          
          await serviceRemovalOp.save({ service: serviceName });
          result = await ApiService.removeServiceFromLogs(serviceName);
          await serviceRemovalOp.clear();
          await loadConfig();
          break;
          
        default:
          throw new Error('Unknown action');
      }
      
      if (result) {
        setSuccess(result.message || 'Action completed successfully');
      }
      
      if (action !== 'processAllLogs') {
        setTimeout(fetchData, 2000);
      }
    } catch (err) {
      console.error(`Action ${action} failed:`, err);
      
      // Clear operations on error
      if (action === 'processAllLogs') await logProcessingOp.clear();
      if (action === 'removeServiceLogs') await serviceRemovalOp.clear();
      
      const errorMessage = err.message?.includes('read-only') 
        ? 'Logs directory is read-only. Remove :ro from docker-compose volume mount.'
        : err.message || 'Action failed';
      
      addError(errorMessage);
    } finally {
      setActionLoading(false);
    }
  };

  // UI state helpers
  const isCacheClearingInBackground = cacheOp.operation?.data && 
    !showCacheClearModal && 
    cacheClearProgress && 
    ['Running', 'Preparing'].includes(cacheClearProgress.status);

  const activeServiceRemoval = serviceRemovalOp.operation?.data?.service;

  // Render status bar component
  const StatusBar = ({ color, icon: Icon, title, subtitle, progress, onViewDetails }) => (
    <div className={`bg-${color}-900 bg-opacity-30 rounded-lg p-4 border border-${color}-700`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center space-x-3 flex-1">
          <Icon className={`w-5 h-5 text-${color}-500 animate-spin`} />
          <div className="flex-1">
            <p className={`font-medium text-${color}-400`}>{title}</p>
            {subtitle && <p className="text-sm text-gray-300 mt-1">{subtitle}</p>}
            {progress !== undefined && (
              <div className="flex items-center space-x-4 mt-1">
                <span className="text-sm text-gray-300">{progress}% complete</span>
              </div>
            )}
          </div>
        </div>
        {onViewDetails && (
          <button
            onClick={onViewDetails}
            className="flex items-center space-x-2 px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded text-white font-medium ml-4"
          >
            <Eye className="w-4 h-4" />
            <span>View Details</span>
          </button>
        )}
      </div>
    </div>
  );

  return (
    <div className="space-y-6">
      {/* Status Bars */}
      {isCacheClearingInBackground && (
        <StatusBar
          color="blue"
          icon={Loader}
          title="Cache clearing in progress..."
          subtitle={cacheClearProgress.bytesDeleted > 0 ? `${formatBytes(cacheClearProgress.bytesDeleted)} cleared` : null}
          progress={cacheClearProgress.percentComplete?.toFixed(0)}
          onViewDetails={() => setShowCacheClearModal(true)}
        />
      )}

      {activeServiceRemoval && (
        <StatusBar
          color="orange"
          icon={Loader}
          title={`Removing ${activeServiceRemoval} entries from logs...`}
          subtitle="This may take several minutes for large log files"
        />
      )}

      {/* Processing Status */}
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
                {processingStatus.progress > 0 && processingStatus.status !== 'complete' && (
                  <div className="mt-2">
                    <div className="w-full bg-gray-700 rounded-full h-2">
                      <div 
                        className="bg-yellow-500 h-2 rounded-full transition-all duration-500"
                        style={{ width: `${Math.min(processingStatus.progress, 100)}%` }}
                      />
                    </div>
                    <p className="text-xs text-gray-400 mt-1">
                      {processingStatus.progress.toFixed(1)}% complete
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
                <StopCircle className="w-4 h-4" />
                <span>Cancel</span>
              </button>
            )}
          </div>
        </div>
      )}

      {/* Alerts */}
      {alerts.errors.map(error => (
        <div key={error.id} className="bg-red-900 bg-opacity-30 rounded-lg p-4 border border-red-700">
          <div className="flex items-start justify-between">
            <div className="flex items-start space-x-2 flex-1">
              <AlertCircle className="w-5 h-5 text-red-400 mt-0.5 flex-shrink-0" />
              <span className="text-red-400">{error.message}</span>
            </div>
            <button
              onClick={() => setAlerts(prev => ({
                ...prev,
                errors: prev.errors.filter(e => e.id !== error.id)
              }))}
              className="ml-4 text-red-400 hover:text-red-300"
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>
      ))}

      {alerts.success && (
        <div className="bg-green-900 bg-opacity-30 rounded-lg p-4 border border-green-700">
          <div className="flex items-center space-x-2">
            <CheckCircle className="w-5 h-5 text-green-400" />
            <span className="text-green-400">{alerts.success}</span>
          </div>
        </div>
      )}

      {/* Cache Clear Modal */}
      {showCacheClearModal && cacheClearProgress && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="bg-gray-800 rounded-lg p-6 max-w-lg w-full mx-4 border border-gray-700">
            <h3 className="text-lg font-semibold text-white mb-4">Clearing Cache</h3>
            
            <div className="space-y-4">
              <div className="flex items-center space-x-2">
                {cacheClearProgress.status === 'Completed' ? (
                  <CheckCircle className="w-5 h-5 text-green-500" />
                ) : cacheClearProgress.status === 'Failed' ? (
                  <AlertCircle className="w-5 h-5 text-red-500" />
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
              
              {['Running', 'Preparing'].includes(cacheClearProgress.status) && (
                <>
                  <div className="w-full bg-gray-700 rounded-full h-4 relative overflow-hidden">
                    <div 
                      className="bg-gradient-to-r from-blue-500 to-blue-600 h-4 rounded-full transition-all duration-500"
                      style={{ width: `${Math.min(100, cacheClearProgress.percentComplete || 0)}%` }}
                    />
                    <div className="absolute inset-0 flex items-center justify-center">
                      <span className="text-xs text-white font-medium">
                        {(cacheClearProgress.percentComplete || 0).toFixed(0)}%
                      </span>
                    </div>
                  </div>
                  
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
              
              {cacheClearProgress.error && (
                <div className="p-3 bg-red-900 bg-opacity-30 rounded border border-red-700">
                  <p className="text-sm text-red-400">{cacheClearProgress.error}</p>
                </div>
              )}
              
              <div className="flex justify-end space-x-3 pt-4 border-t border-gray-700">
                {['Running', 'Preparing'].includes(cacheClearProgress.status) ? (
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

      {/* Management Sections */}
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
            disabled={isProcessingLogs || cacheOp.loading || logProcessingOp.loading}
            className="flex items-center space-x-2 px-4 py-2 rounded-lg bg-blue-600 hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {mockMode ? <ToggleRight className="w-5 h-5" /> : <ToggleLeft className="w-5 h-5" />}
            <span>{mockMode ? 'Enabled' : 'Disabled'}</span>
          </button>
        </div>
        {mockMode && (
          <div className="mt-4 p-3 bg-blue-900 bg-opacity-30 rounded-lg border border-blue-700">
            <div className="flex items-center space-x-2 text-blue-400">
              <AlertCircle className="w-4 h-4" />
              <span className="text-sm">Mock mode active - API actions disabled</span>
            </div>
          </div>
        )}
      </div>

      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <HardDrive className="w-5 h-5 text-blue-400" />
          <h3 className="text-lg font-semibold text-white">Disk Cache Management</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Manage cached game files in <code className="bg-gray-700 px-2 py-1 rounded">{config.cachePath}</code>
        </p>
        <button
          onClick={handleClearAllCache}
          disabled={actionLoading || isProcessingLogs || mockMode || isCacheClearingInBackground || cacheOp.loading}
          className="flex items-center justify-center space-x-2 px-4 py-3 w-full rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 transition-colors"
        >
          {actionLoading || cacheOp.loading ? <Loader className="w-4 h-4 animate-spin" /> : <Trash2 className="w-4 h-4" />}
          <span>{isCacheClearingInBackground ? 'Cache Clearing in Progress...' : 'Clear All Cached Files'}</span>
        </button>
        <p className="text-xs text-gray-500 mt-2">
          ⚠️ This deletes ALL cached game files from disk
        </p>
      </div>

      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <Database className="w-5 h-5 text-purple-400" />
          <h3 className="text-lg font-semibold text-white">Database Management</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Manage download history and statistics
        </p>
        <button
          onClick={() => handleAction('resetDatabase')}
          disabled={actionLoading || isProcessingLogs || mockMode}
          className="flex items-center justify-center space-x-2 px-4 py-3 w-full rounded-lg bg-red-600 hover:bg-red-700 disabled:opacity-50 transition-colors"
        >
          {actionLoading ? <Loader className="w-4 h-4 animate-spin" /> : <Database className="w-4 h-4" />}
          <span>Reset Database</span>
        </button>
        <p className="text-xs text-gray-500 mt-2">
          Clears all download history (does not affect cached files)
        </p>
      </div>

      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <FileText className="w-5 h-5 text-green-400" />
          <h3 className="text-lg font-semibold text-white">Log Processing</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Control how access.log is processed for statistics
        </p>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <button
            onClick={() => handleAction('resetLogs')}
            disabled={actionLoading || isProcessingLogs || mockMode || logProcessingOp.loading}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-yellow-600 hover:bg-yellow-700 disabled:opacity-50 transition-colors"
          >
            <RefreshCw className="w-4 h-4" />
            <span>Reset Log Position</span>
          </button>
          <button
            onClick={() => handleAction('processAllLogs')}
            disabled={actionLoading || isProcessingLogs || mockMode || logProcessingOp.loading}
            className="flex items-center justify-center space-x-2 px-4 py-3 rounded-lg bg-green-600 hover:bg-green-700 disabled:opacity-50 transition-colors"
          >
            {logProcessingOp.loading ? <Loader className="w-4 h-4 animate-spin" /> : <PlayCircle className="w-4 h-4" />}
            <span>Process All Logs</span>
          </button>
        </div>
        <div className="mt-4 p-3 bg-gray-700 rounded-lg">
          <p className="text-xs text-gray-400">
            <strong>Reset:</strong> Start from current end of log<br/>
            <strong>Process All:</strong> Import entire log history
          </p>
        </div>
      </div>

      <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
        <div className="flex items-center space-x-2 mb-4">
          <FileText className="w-5 h-5 text-orange-400" />
          <h3 className="text-lg font-semibold text-white">Log File Management</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Remove service entries from <code className="bg-gray-700 px-2 py-1 rounded">{config.logPath}</code>
        </p>
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
          {(config.services.length > 0 ? config.services : ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot']).map(service => {
            const isRemoving = activeServiceRemoval === service;
            return (
              <button
                key={service}
                onClick={() => handleAction('removeServiceLogs', service)}
                disabled={actionLoading || isProcessingLogs || mockMode || activeServiceRemoval || serviceRemovalOp.loading}
                className={`px-4 py-3 rounded-lg transition-colors flex flex-col items-center ${
                  isRemoving 
                    ? 'bg-orange-700 cursor-not-allowed opacity-75' 
                    : 'bg-gray-700 hover:bg-gray-600 disabled:opacity-50'
                }`}
              >
                {isRemoving || serviceRemovalOp.loading ? (
                  <>
                    <Loader className="w-4 h-4 animate-spin mb-1" />
                    <span className="capitalize font-medium">Removing...</span>
                  </>
                ) : (
                  <>
                    <span className="capitalize font-medium">Clear {service}</span>
                    {serviceCounts[service] !== undefined && (
                      <span className="text-xs text-gray-400 mt-1">
                        ({serviceCounts[service].toLocaleString()} entries)
                      </span>
                    )}
                  </>
                )}
              </button>
            );
          })}
        </div>
        <div className="mt-4 p-3 bg-yellow-900 bg-opacity-30 rounded-lg border border-yellow-700">
          <p className="text-xs text-yellow-400">
            <strong>Warning:</strong> Requires write permissions to logs directory
          </p>
        </div>
      </div>

      {/* Backend Operation Status (for debugging) */}
      {(cacheOp.error || logProcessingOp.error || serviceRemovalOp.error) && (
        <div className="bg-orange-900 bg-opacity-30 rounded-lg p-4 border border-orange-700">
          <p className="text-sm text-orange-400">
            Backend storage error: {cacheOp.error || logProcessingOp.error || serviceRemovalOp.error}
          </p>
        </div>
      )}
    </div>
  );
};

export default ManagementTab;