import React, { useState, useEffect } from 'react';
import { HardDrive, Trash2, Loader, CheckCircle, AlertCircle, StopCircle, Eye } from 'lucide-react';
import ApiService from '../../services/api.service';
import { useBackendOperation } from '../../hooks/useBackendOperation';
import { formatBytes } from '../../utils/formatters';
import { Alert } from '../ui/Alert';
import { Button } from '../ui/Button';
import { Card } from '../ui/Card';
import { Modal } from '../ui/Modal';
import type { CacheClearStatus, Config } from '../../types';

interface CacheManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const CacheManager: React.FC<CacheManagerProps> = ({
  isAuthenticated,
  mockMode,
  onError,
  onSuccess
}) => {
  const [cacheClearProgress, setCacheClearProgress] = useState<CacheClearStatus | null>(null);
  const [showCacheClearModal, setShowCacheClearModal] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [config, setConfig] = useState<Config>({ cachePath: '/cache', logPath: '/logs/access.log', services: [] });
  
  const cacheOp = useBackendOperation('activeCacheClearOperation', 'cacheClearing', 30);
  
  useEffect(() => {
    loadConfig();
    restoreCacheOperation();
  }, []);

  const loadConfig = async () => {
    try {
      const configData = await ApiService.getConfig();
      setConfig(configData);
    } catch (err) {
      console.error('Failed to load config:', err);
    }
  };

  const restoreCacheOperation = async () => {
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
  };

  const startCacheClearPolling = (operationId: string) => {
    const pollInterval = setInterval(async () => {
      try {
        const status = await ApiService.getCacheClearStatus(operationId);
        setCacheClearProgress(status);
        
        if (['Running', 'Preparing'].includes(status.status)) {
          await cacheOp.update({ lastProgress: status.percentComplete || status.progress || 0 });
        } else {
          handleCacheClearComplete(status);
          clearInterval(pollInterval);
        }
      } catch (err) {
        console.error('Error polling cache clear status:', err);
        clearInterval(pollInterval);
      }
    }, 1000);
  };

  const handleCacheClearComplete = async (progress: CacheClearStatus) => {
    await cacheOp.clear();
    
    if (progress.status === 'Completed') {
      onSuccess?.(`Cache cleared successfully! ${formatBytes(progress.bytesDeleted || 0)} freed.`);
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearProgress(null);
      }, 2000);
    } else if (progress.status === 'Failed') {
      onError?.(`Cache clearing failed: ${progress.error || progress.message || 'Unknown error'}`);
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearProgress(null);
      }, 5000);
    } else if (progress.status === 'Cancelled') {
      onSuccess?.('Cache clearing cancelled');
      setShowCacheClearModal(false);
      setCacheClearProgress(null);
    }
  };

  const handleClearAllCache = async () => {
    if (!isAuthenticated) {
      onError?.('Authentication required');
      return;
    }
    
    if (!window.confirm('This will clear ALL cached game files. Continue?')) return;
    
    setActionLoading(true);
    
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
    } catch (err: any) {
      onError?.('Failed to start cache clearing: ' + err.message);
    } finally {
      setActionLoading(false);
    }
  };

  const handleCancelCacheClear = async () => {
    const operation = cacheOp.operation as any;
    if (!operation?.data?.operationId) return;
    
    try {
      setCacheClearProgress(prev => prev ? { 
        ...prev, 
        status: 'Cancelling',
        statusMessage: 'Cancelling operation...'
      } : null);
      
      await ApiService.cancelCacheClear(operation.data.operationId);
      await cacheOp.clear();
      
      setTimeout(() => {
        setShowCacheClearModal(false);
        setCacheClearProgress(null);
        onSuccess?.('Cache clearing operation cancelled');
      }, 1500);
    } catch (err) {
      console.error('Failed to cancel cache clear:', err);
      setShowCacheClearModal(false);
      setCacheClearProgress(null);
      await cacheOp.clear();
    }
  };

  const isCacheClearingInBackground = (cacheOp.operation as any)?.data && 
    !showCacheClearModal && 
    cacheClearProgress && 
    ['Running', 'Preparing'].includes(cacheClearProgress.status);

  const progressPercent = cacheClearProgress?.percentComplete || cacheClearProgress?.progress || 0;

  return (
    <>
      {isCacheClearingInBackground && (
        <Alert color="blue" icon={<Loader className="w-5 h-5 animate-spin" />}>
          <div className="flex items-center justify-between">
            <div className="flex-1">
              <p className="font-medium">Cache clearing in progress...</p>
              {cacheClearProgress.bytesDeleted && cacheClearProgress.bytesDeleted > 0 && (
                <p className="text-sm mt-1 opacity-75">
                  {formatBytes(cacheClearProgress.bytesDeleted)} cleared
                </p>
              )}
              <p className="text-sm mt-1 opacity-75">
                {progressPercent.toFixed(0)}% complete
              </p>
            </div>
            <Button
              variant="filled"
              color="blue"
              size="sm"
              leftSection={<Eye className="w-4 h-4" />}
              onClick={() => setShowCacheClearModal(true)}
            >
              View Details
            </Button>
          </div>
        </Alert>
      )}

      <Card>
        <div className="flex items-center space-x-2 mb-4">
          <HardDrive className="w-5 h-5 text-blue-400" />
          <h3 className="text-lg font-semibold text-white">Disk Cache Management</h3>
        </div>
        <p className="text-gray-400 text-sm mb-4">
          Manage cached game files in <code className="bg-gray-700 px-2 py-1 rounded">{config.cachePath}</code>
        </p>
        <Button
          fullWidth
          variant="filled"
          color="red"
          leftSection={<Trash2 className="w-4 h-4" />}
          onClick={handleClearAllCache}
          disabled={actionLoading || mockMode || isCacheClearingInBackground || cacheOp.loading || !isAuthenticated}
          loading={actionLoading || cacheOp.loading}
        >
          {isCacheClearingInBackground ? 'Cache Clearing in Progress...' : 'Clear All Cached Files'}
        </Button>
        <p className="text-xs text-gray-500 mt-2">
          ⚠️ This deletes ALL cached game files from disk
        </p>
      </Card>

      <Modal
        opened={showCacheClearModal && cacheClearProgress !== null}
        onClose={() => {
          if (!['Running', 'Preparing'].includes(cacheClearProgress?.status || '')) {
            setShowCacheClearModal(false);
            setCacheClearProgress(null);
          }
        }}
        title="Clearing Cache"
        size="lg"
      >
        <div className="space-y-4">
          <div className="flex items-center space-x-2">
            {cacheClearProgress?.status === 'Completed' ? (
              <CheckCircle className="w-5 h-5 text-green-500" />
            ) : cacheClearProgress?.status === 'Failed' ? (
              <AlertCircle className="w-5 h-5 text-red-500" />
            ) : (
              <Loader className="w-5 h-5 text-blue-500 animate-spin" />
            )}
            <div className="flex-1">
              <span className="text-white font-medium">{cacheClearProgress?.status}</span>
              {(cacheClearProgress?.statusMessage || cacheClearProgress?.message) && (
                <p className="text-sm text-gray-400">{cacheClearProgress?.statusMessage || cacheClearProgress?.message}</p>
              )}
            </div>
          </div>
          
          {cacheClearProgress && ['Running', 'Preparing'].includes(cacheClearProgress.status) && (
            <>
              <div className="w-full bg-gray-700 rounded-full h-4 relative overflow-hidden">
                <div 
                  className="bg-gradient-to-r from-blue-500 to-blue-600 h-4 rounded-full transition-all duration-500"
                  style={{ width: `${Math.min(100, progressPercent)}%` }}
                />
                <div className="absolute inset-0 flex items-center justify-center">
                  <span className="text-xs text-white font-medium">
                    {progressPercent.toFixed(0)}%
                  </span>
                </div>
              </div>
              
              {cacheClearProgress.bytesDeleted !== undefined && cacheClearProgress.totalBytesToDelete && cacheClearProgress.totalBytesToDelete > 0 && (
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
          
          {cacheClearProgress?.error && (
            <Alert color="red">
              {cacheClearProgress.error}
            </Alert>
          )}
          
          <div className="flex justify-end space-x-3 pt-4 border-t border-gray-700">
            {cacheClearProgress && ['Running', 'Preparing'].includes(cacheClearProgress.status) ? (
              <>
                <Button
                  variant="filled"
                  color="red"
                  leftSection={<StopCircle className="w-4 h-4" />}
                  onClick={handleCancelCacheClear}
                >
                  Cancel
                </Button>
                <Button
                  variant="default"
                  onClick={() => setShowCacheClearModal(false)}
                >
                  Run in Background
                </Button>
              </>
            ) : (
              <Button
                variant="default"
                onClick={() => {
                  setShowCacheClearModal(false);
                  setCacheClearProgress(null);
                }}
              >
                Close
              </Button>
            )}
          </div>
        </div>
      </Modal>

      {cacheOp.error && (
        <Alert color="orange">
          Backend storage error: {cacheOp.error}
        </Alert>
      )}
    </>
  );
};

export default CacheManager;