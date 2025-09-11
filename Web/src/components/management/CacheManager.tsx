import React, { useState, useEffect } from 'react';
import { HardDrive, Trash2, Loader, CheckCircle, AlertCircle, StopCircle } from 'lucide-react';
import ApiService from '@services/api.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import { formatBytes } from '@utils/formatters';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import type { CacheClearStatus, Config } from '../../types';

interface CacheManagerProps {
  isAuthenticated: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onBackgroundOperation?: (operation: any) => void;
}

const CacheManager: React.FC<CacheManagerProps> = ({
  isAuthenticated,
  mockMode,
  onError,
  onSuccess,
  onBackgroundOperation
}) => {
  const [cacheClearProgress, setCacheClearProgress] = useState<CacheClearStatus | null>(null);
  const [showCacheClearModal, setShowCacheClearModal] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [config, setConfig] = useState<Config>({ cachePath: '/cache', logPath: '/logs/access.log', services: [] });
  
  const cacheOp = useBackendOperation('activeCacheClearOperation', 'cacheClearing', 30);
  
  // Report cache clearing status to parent
  useEffect(() => {
    const isCacheClearingInBackground = (cacheOp.operation as any)?.data && 
      !showCacheClearModal && 
      cacheClearProgress && 
      ['Running', 'Preparing'].includes(cacheClearProgress.status);
      
    if (isCacheClearingInBackground && onBackgroundOperation) {
      onBackgroundOperation({
        bytesDeleted: cacheClearProgress.bytesDeleted,
        progress: cacheClearProgress.percentComplete || cacheClearProgress.progress || 0,
        showModal: () => setShowCacheClearModal(true)
      });
    } else if (onBackgroundOperation) {
      onBackgroundOperation(null);
    }
  }, [cacheOp.operation, showCacheClearModal, cacheClearProgress, onBackgroundOperation]);
  
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
      <Card>
        <div className="flex items-center space-x-2 mb-4">
          <HardDrive className="w-5 h-5 text-themed-primary" />
          <h3 className="text-lg font-semibold text-themed-primary">Disk Cache Management</h3>
        </div>
        <p className="text-themed-muted text-sm mb-4">
          Manage cached game files in <code className="bg-themed-tertiary px-2 py-1 rounded">{config.cachePath}</code>
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
        <p className="text-xs text-themed-muted mt-2">
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
              <CheckCircle className="w-5 h-5 cache-hit" />
            ) : cacheClearProgress?.status === 'Failed' ? (
              <AlertCircle className="w-5 h-5 text-themed-error" />
            ) : (
              <Loader className="w-5 h-5 text-themed-primary animate-spin" />
            )}
            <div className="flex-1">
              <span className="text-themed-primary font-medium">{cacheClearProgress?.status}</span>
              {(cacheClearProgress?.statusMessage || cacheClearProgress?.message) && (
                <p className="text-sm text-themed-muted">{cacheClearProgress?.statusMessage || cacheClearProgress?.message}</p>
              )}
            </div>
          </div>
          
          {cacheClearProgress && ['Running', 'Preparing'].includes(cacheClearProgress.status) && (
            <>
              <div className="w-full progress-track rounded-full h-4 relative overflow-hidden">
                <div 
                  className="progress-bar-medium h-4 rounded-full smooth-transition"
                  style={{ width: `${Math.min(100, progressPercent)}%` }}
                />
                <div className="absolute inset-0 flex items-center justify-center">
                  <span className="text-xs text-themed-primary font-medium">
                    {progressPercent.toFixed(0)}%
                  </span>
                </div>
              </div>
              
              {cacheClearProgress.bytesDeleted !== undefined && cacheClearProgress.totalBytesToDelete && cacheClearProgress.totalBytesToDelete > 0 && (
                <div className="text-sm text-themed-muted text-center">
                  <span className="cache-hit font-semibold">
                    {formatBytes(cacheClearProgress.bytesDeleted || 0)}
                  </span>
                  {' / '}
                  <span className="text-themed-primary">
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
          
          <div className="flex justify-end space-x-3 pt-4 border-t border-themed-secondary">
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