import React, { useState, useEffect, useRef, useCallback } from 'react';
import { HardDrive, Trash2, AlertTriangle, Loader } from 'lucide-react';
import ApiService from '@services/api.service';
import { AuthMode } from '@services/auth.service';
import { useBackendOperation } from '@hooks/useBackendOperation';
import { formatBytes } from '@utils/formatters';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import type { CacheClearStatus, Config } from '../../types';

interface CacheManagerProps {
  isAuthenticated: boolean;
  authMode?: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onBackgroundOperation?: (operation: any) => void;
}

const CacheManager: React.FC<CacheManagerProps> = ({
  authMode = 'unauthenticated',
  mockMode,
  onError,
  onSuccess,
  onBackgroundOperation
}) => {
  const [cacheClearProgress, setCacheClearProgress] = useState<CacheClearStatus | null>(null);
  const [showConfirmModal, setShowConfirmModal] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [isLoadingConfig, setIsLoadingConfig] = useState(true);
  const [config, setConfig] = useState<Config>({
    cachePath: 'Loading...',
    logPath: 'Loading...',
    services: [],
    timezone: 'UTC'
  });
  const [threadCount, setThreadCount] = useState(4);
  const [threadCountLoading, setThreadCountLoading] = useState(false);
  const [deleteMode, setDeleteMode] = useState<'preserve' | 'full'>('preserve');
  const [deleteModeLoading, setDeleteModeLoading] = useState(false);
  const [cpuCount, setCpuCount] = useState(16); // Default max, will be updated

  const cacheOp = useBackendOperation('activeCacheClearOperation', 'cacheClearing', 30);

  // Use ref to store the callback to prevent infinite loop
  const onBackgroundOperationRef = useRef(onBackgroundOperation);
  useEffect(() => {
    onBackgroundOperationRef.current = onBackgroundOperation;
  });

  // Report cache clearing status to parent

  useEffect(() => {
    loadConfig();
    loadThreadCount();
    loadDeleteMode();
    loadCpuCount();
    restoreCacheOperation();

    // Poll CPU count every 30 seconds to detect VM/container changes
    const cpuPollInterval = setInterval(() => {
      loadCpuCount();
    }, 30000);

    return () => {
      clearInterval(cpuPollInterval);
    };
  }, []);

  const loadConfig = async () => {
    try {
      setIsLoadingConfig(true);
      const configData = await ApiService.getConfig();
      setConfig(configData);
    } catch (err) {
      console.error('Failed to load config:', err);
    } finally {
      setIsLoadingConfig(false);
    }
  };

  const loadThreadCount = async () => {
    try {
      const data = await ApiService.getCacheThreadCount();
      setThreadCount(data.threadCount);
    } catch (err) {
      console.error('Failed to load thread count:', err);
    }
  };

  const loadDeleteMode = async () => {
    try {
      const data = await ApiService.getCacheDeleteMode();
      setDeleteMode(data.deleteMode as 'preserve' | 'full');
    } catch (err) {
      console.error('Failed to load delete mode:', err);
    }
  };

  const loadCpuCount = async () => {
    try {
      const data = await ApiService.getSystemCpuCount();
      setCpuCount(data.cpuCount);
    } catch (err) {
      console.error('Failed to load CPU count:', err);
    }
  };

  const handleThreadCountChange = async (newThreadCount: number) => {
    if (newThreadCount < 1 || newThreadCount > cpuCount) return;

    setThreadCountLoading(true);
    try {
      await ApiService.setCacheThreadCount(newThreadCount);
      setThreadCount(newThreadCount);
      onSuccess?.(`Cache clearing will now use ${newThreadCount} thread${newThreadCount > 1 ? 's' : ''}`);
    } catch (err: any) {
      console.error('Failed to update thread count:', err);
      onError?.(err?.message || 'Failed to update thread count');
    } finally {
      setThreadCountLoading(false);
    }
  };

  const handleDeleteModeChange = async (newMode: 'preserve' | 'full') => {
    setDeleteModeLoading(true);
    try {
      await ApiService.setCacheDeleteMode(newMode);
      setDeleteMode(newMode);
      const modeDesc = newMode === 'full' ? 'Full deletion (faster)' : 'Preserve structure';
      onSuccess?.(`Delete mode set to: ${modeDesc}`);
    } catch (err: any) {
      console.error('Failed to update delete mode:', err);
      onError?.(err?.message || 'Failed to update delete mode');
    } finally {
      setDeleteModeLoading(false);
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
        setCacheClearProgress(null);
      }, 2000);
    } else if (progress.status === 'Failed') {
      onError?.(`Cache clearing failed: ${progress.error || progress.message || 'Unknown error'}`);
      setTimeout(() => {
        setCacheClearProgress(null);
      }, 5000);
    } else if (progress.status === 'Cancelled') {
      onSuccess?.('Cache clearing cancelled');
      setCacheClearProgress(null);
    } else {
      setCacheClearProgress(null);
    }
  };

  const handleClearAllCache = () => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setShowConfirmModal(true);
  };

  const startCacheClear = async () => {
    setActionLoading(true);
    setShowConfirmModal(false);

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
        startCacheClearPolling(result.operationId);
      }
    } catch (err: any) {
      onError?.('Failed to start cache clearing: ' + (err?.message || 'Unknown error'));
    } finally {
      setActionLoading(false);
    }
  };

  const handleCancelCacheClear = useCallback(async () => {
    const operation = cacheOp.operation as any;
    if (!operation?.data?.operationId) return;

    try {
      setCacheClearProgress((prev) =>
        prev
          ? {
              ...prev,
              status: 'Cancelling',
              statusMessage: 'Cancelling operation...'
            }
          : null
      );

      await ApiService.cancelCacheClear(operation.data.operationId);
      await cacheOp.clear();

      setTimeout(() => {
        setCacheClearProgress(null);
        onSuccess?.('Cache clearing operation cancelled');
      }, 1500);
    } catch (err) {
      console.error('Failed to cancel cache clear:', err);
      setCacheClearProgress(null);
      await cacheOp.clear();
    }
  }, [cacheOp, onSuccess]);

  useEffect(() => {
    const isActive =
      (cacheOp.operation as any)?.data &&
      cacheClearProgress &&
      ['Running', 'Preparing', 'Cancelling'].includes(cacheClearProgress.status);

    if (isActive && onBackgroundOperationRef.current) {
      onBackgroundOperationRef.current({
        bytesDeleted: cacheClearProgress.bytesDeleted || 0,
        filesDeleted: cacheClearProgress.filesDeleted || 0,
        progress: cacheClearProgress.percentComplete || cacheClearProgress.progress || 0,
        cancel: handleCancelCacheClear
      });
    } else if (onBackgroundOperationRef.current) {
      onBackgroundOperationRef.current(null);
    }
  }, [cacheOp.operation, cacheClearProgress]);

  const isCacheClearingActive =
    (cacheOp.operation as any)?.data &&
    cacheClearProgress &&
    ['Running', 'Preparing', 'Cancelling'].includes(cacheClearProgress.status);

  return (
    <>
      <Card>
        <div className="flex items-center space-x-2 mb-4">
          <HardDrive className="w-5 h-5 text-themed-primary" />
          <h3 className="text-lg font-semibold text-themed-primary">Disk Cache Management</h3>
        </div>
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div className="flex-1">
            {isLoadingConfig ? (
              <div className="flex items-center gap-2">
                <Loader className="w-4 h-4 animate-spin text-themed-accent" />
                <p className="text-sm text-themed-secondary">Loading cache configuration...</p>
              </div>
            ) : (
              <>
                <p className="text-themed-secondary">
                  Manage cached game files in{' '}
                  <code className="bg-themed-tertiary px-2 py-1 rounded">{config.cachePath}</code>
                </p>
                <p className="text-xs text-themed-muted mt-1">
                  ⚠️ This deletes ALL cached game files from disk
                </p>
              </>
            )}
          </div>
          <Button
            variant="filled"
            color="red"
            leftSection={<Trash2 className="w-4 h-4" />}
            onClick={handleClearAllCache}
            disabled={
              actionLoading ||
              mockMode ||
              isCacheClearingActive ||
              cacheOp.loading ||
              authMode !== 'authenticated'
            }
            loading={actionLoading || cacheOp.loading}
            className="w-full sm:w-48"
          >
            {isCacheClearingActive ? 'Clearing...' : 'Clear Cache'}
          </Button>
        </div>

        {/* Delete Mode Configuration */}
        <div className="mt-4 pt-4 border-t border-themed-tertiary">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div className="flex-1">
              <p className="text-themed-secondary font-medium">Deletion Mode</p>
              <p className="text-xs text-themed-muted mt-1">
                {deleteMode === 'full'
                  ? 'Bulk delete (faster, no file count)'
                  : 'Delete files individually (slower, shows file count)'}
              </p>
            </div>
            <div className="flex items-center gap-2">
              <Button
                size="sm"
                variant={deleteMode === 'preserve' ? 'filled' : 'default'}
                color={deleteMode === 'preserve' ? 'blue' : undefined}
                onClick={() => handleDeleteModeChange('preserve')}
                disabled={deleteModeLoading || mockMode || authMode !== 'authenticated'}
              >
                Preserve
              </Button>
              <Button
                size="sm"
                variant={deleteMode === 'full' ? 'filled' : 'default'}
                color={deleteMode === 'full' ? 'green' : undefined}
                onClick={() => handleDeleteModeChange('full')}
                disabled={deleteModeLoading || mockMode || authMode !== 'authenticated'}
              >
                Full (Faster)
              </Button>
            </div>
          </div>
        </div>

        {/* Thread Count Configuration */}
        <div className="mt-4 pt-4 border-t border-themed-tertiary">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div className="flex-1">
              <p className="text-themed-secondary font-medium">Cache Clearing Threads</p>
              <p className="text-xs text-themed-muted mt-1">
                Higher values = faster (max: {cpuCount} CPU{cpuCount > 1 ? 's' : ''})
              </p>
            </div>
            <div className="flex items-center gap-3">
              <Button
                size="sm"
                variant="default"
                onClick={() => handleThreadCountChange(threadCount - 1)}
                disabled={threadCount <= 1 || threadCountLoading || mockMode || authMode !== 'authenticated'}
              >
                -
              </Button>
              <div className="min-w-[60px] text-center">
                <span className="text-lg font-semibold text-themed-primary">{threadCount}</span>
                <span className="text-xs text-themed-muted">/{cpuCount}</span>
              </div>
              <Button
                size="sm"
                variant="default"
                onClick={() => handleThreadCountChange(threadCount + 1)}
                disabled={threadCount >= cpuCount || threadCountLoading || mockMode || authMode !== 'authenticated'}
              >
                +
              </Button>
            </div>
          </div>
        </div>
      </Card>

      <Modal
        opened={showConfirmModal}
        onClose={() => {
          if (!actionLoading) {
            setShowConfirmModal(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Confirm Cache Clear</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            This will permanently delete <strong>all cached game files</strong> from{' '}
            <code className="bg-themed-tertiary px-1 py-0.5 rounded">{config.cachePath}</code>. Games will need to redownload content after clearing.
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">Important:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>Stop all active downloads before proceeding</li>
                <li>Download history and settings will be preserved</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowConfirmModal(false)}
              disabled={actionLoading}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={startCacheClear}
              loading={actionLoading}
            >
              Delete Cached Files
            </Button>
          </div>
        </div>
      </Modal>

      {cacheOp.error && <Alert color="orange">Backend storage error: {cacheOp.error}</Alert>}
    </>
  );
};

export default CacheManager;
