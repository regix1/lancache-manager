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
    services: []
  });

  const cacheOp = useBackendOperation('activeCacheClearOperation', 'cacheClearing', 30);

  // Use ref to store the callback to prevent infinite loop
  const onBackgroundOperationRef = useRef(onBackgroundOperation);
  useEffect(() => {
    onBackgroundOperationRef.current = onBackgroundOperation;
  });

  // Report cache clearing status to parent

  useEffect(() => {
    loadConfig();
    restoreCacheOperation();
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
