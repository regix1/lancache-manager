import React, { useState, useEffect } from 'react';
import { Server, Trash2, AlertTriangle, Loader2, Lock } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import type { Config } from '../../types';

interface CacheManagerProps {
  isAuthenticated: boolean;
  authMode?: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const CacheManager: React.FC<CacheManagerProps> = ({
  authMode = 'unauthenticated',
  mockMode,
  onError,
  onSuccess
}) => {
  const signalR = useSignalR();
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
  const [deleteMode, setDeleteMode] = useState<'preserve' | 'full' | 'rsync'>('preserve');
  const [deleteModeLoading, setDeleteModeLoading] = useState(false);
  const [cpuCount, setCpuCount] = useState(16); // Default max, will be updated
  const [rsyncAvailable, setRsyncAvailable] = useState(false);
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [isCacheClearing, setIsCacheClearing] = useState(false);

  // Report cache clearing status to parent

  useEffect(() => {
    loadConfig();
    loadThreadCount();
    loadDeleteMode();
    loadCpuCount();
    loadRsyncAvailability();
    loadDirectoryPermissions();

    // Poll CPU count every 30 seconds to detect VM/container changes
    const cpuPollInterval = setInterval(() => {
      loadCpuCount();
    }, 30000);

    return () => {
      clearInterval(cpuPollInterval);
    };
  }, []);

  // Listen for cache clear completion (via SignalR for UI state only)
  useEffect(() => {
    if (mockMode) return;

    const handleCacheClearComplete = () => {
      setIsCacheClearing(false);
    };

    signalR.on('CacheClearComplete', handleCacheClearComplete);

    return () => {
      signalR.off('CacheClearComplete', handleCacheClearComplete);
    };
  }, [mockMode, signalR]);

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

  const loadRsyncAvailability = async () => {
    try {
      const data = await ApiService.isRsyncAvailable();
      setRsyncAvailable(data.available);
    } catch (err) {
      console.error('Failed to check rsync availability:', err);
      setRsyncAvailable(false);
    }
  };

  const loadDirectoryPermissions = async () => {
    try {
      setCheckingPermissions(true);
      const data = await ApiService.getDirectoryPermissions();
      setCacheReadOnly(data.cache.readOnly);
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      setCacheReadOnly(false); // Assume writable on error
    } finally {
      setCheckingPermissions(false);
    }
  };

  const handleThreadCountChange = async (newThreadCount: number) => {
    if (newThreadCount < 1 || newThreadCount > cpuCount) return;

    setThreadCountLoading(true);
    try {
      await ApiService.setCacheThreadCount(newThreadCount);
      setThreadCount(newThreadCount);
      onSuccess?.(
        `Cache clearing will now use ${newThreadCount} thread${newThreadCount > 1 ? 's' : ''}`
      );
    } catch (err: any) {
      console.error('Failed to update thread count:', err);
      onError?.(err?.message || 'Failed to update thread count');
    } finally {
      setThreadCountLoading(false);
    }
  };

  const handleDeleteModeChange = async (newMode: 'preserve' | 'full' | 'rsync') => {
    setDeleteModeLoading(true);
    try {
      await ApiService.setCacheDeleteMode(newMode);
      setDeleteMode(newMode);
      const modeDesc =
        newMode === 'rsync' ? 'Rsync' : newMode === 'full' ? 'Fast Mode' : 'Safe Mode';
      onSuccess?.(`Delete mode set to: ${modeDesc}`);
    } catch (err: any) {
      console.error('Failed to update delete mode:', err);
      onError?.(err?.message || 'Failed to update delete mode');
    } finally {
      setDeleteModeLoading(false);
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

    // Set clearing state BEFORE API call to avoid race condition
    // SignalR completion event may arrive before API returns
    setIsCacheClearing(true);

    try {
      await ApiService.clearAllCache();
      // NotificationsContext handles success/error messages via SignalR
    } catch (err: any) {
      onError?.('Failed to start cache clearing: ' + (err?.message || 'Unknown error'));
      setIsCacheClearing(false);
    } finally {
      setActionLoading(false);
    }
  };


  return (
    <>
      <Card>
        {cacheReadOnly ? (
          <div className="flex items-center gap-2">
            <Server className="w-5 h-5 icon-green flex-shrink-0" />
            <h3 className="text-lg font-semibold text-themed-primary">Disk Cache Management</h3>
            <span
              className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
              style={{
                backgroundColor: 'var(--theme-warning-bg)',
                color: 'var(--theme-warning)',
                borderColor: 'var(--theme-warning)'
              }}
            >
              <Lock className="w-3 h-3" />
              Read-only
            </span>
          </div>
        ) : (
          <>
            <div className="flex items-center gap-2 mb-6">
              <Server className="w-5 h-5 icon-green flex-shrink-0" />
              <h3 className="text-lg font-semibold text-themed-primary">Disk Cache Management</h3>
            </div>

            {/* Main Cache Path and Clear Button */}
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
              <div className="flex-1">
                {isLoadingConfig ? (
                  <div className="flex items-center gap-2">
                    <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
                    <p className="text-sm text-themed-secondary">Loading cache configuration...</p>
                  </div>
                ) : (
                  <>
                    <p className="text-themed-secondary">
                      Manage cached game files in{' '}
                      <code className="bg-themed-tertiary px-2 py-1 rounded text-xs">
                        {config.cachePath}
                      </code>
                    </p>
                    <p className="text-xs text-themed-muted mt-1 flex items-center gap-1.5">
                      <AlertTriangle className="w-3.5 h-3.5 text-themed-accent flex-shrink-0" />
                      <span>This deletes ALL cached game files from disk</span>
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
                  isCacheClearing ||
                  authMode !== 'authenticated' ||
                  cacheReadOnly ||
                  checkingPermissions
                }
                loading={actionLoading || checkingPermissions}
                className="w-full sm:w-48"
                title={cacheReadOnly ? 'Cache directory is mounted read-only' : undefined}
              >
                {isCacheClearing ? 'Clearing...' : 'Clear Cache'}
              </Button>
            </div>

            {/* Configuration Options - Unified Grid Layout */}
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4 p-4 rounded-lg bg-themed-tertiary/30">
              {/* Delete Mode Configuration */}
              <div className="space-y-3">
                <div>
                  <p className="text-themed-primary font-medium text-sm mb-1">Deletion Method</p>
                  <p className="text-xs text-themed-muted">
                    {deleteMode === 'rsync'
                      ? 'Rsync with empty directory (network storage)'
                      : deleteMode === 'full'
                        ? 'Fast Mode directory removal (faster)'
                        : 'Individual file deletion (slower, keeps structure)'}
                  </p>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <Button
                    size="sm"
                    variant={deleteMode === 'preserve' ? 'filled' : 'default'}
                    color={deleteMode === 'preserve' ? 'blue' : undefined}
                    onClick={() => handleDeleteModeChange('preserve')}
                    disabled={
                      deleteModeLoading ||
                      mockMode ||
                      isCacheClearing ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                    title={cacheReadOnly ? 'Cache directory is read-only' : undefined}
                  >
                    Safe Mode
                  </Button>
                  <Button
                    size="sm"
                    variant={deleteMode === 'full' ? 'filled' : 'default'}
                    color={deleteMode === 'full' ? 'green' : undefined}
                    onClick={() => handleDeleteModeChange('full')}
                    disabled={
                      deleteModeLoading ||
                      mockMode ||
                      isCacheClearing ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                    title={cacheReadOnly ? 'Cache directory is read-only' : undefined}
                  >
                    Fast Mode
                  </Button>
                  {rsyncAvailable && (
                    <Button
                      size="sm"
                      variant={deleteMode === 'rsync' ? 'filled' : 'default'}
                      color={deleteMode === 'rsync' ? 'purple' : undefined}
                      onClick={() => handleDeleteModeChange('rsync')}
                      disabled={
                        deleteModeLoading ||
                        mockMode ||
                        isCacheClearing ||
                        authMode !== 'authenticated' ||
                        cacheReadOnly
                      }
                      title={cacheReadOnly ? 'Cache directory is read-only' : undefined}
                    >
                      Rsync
                    </Button>
                  )}
                </div>
              </div>

              {/* Thread Count Configuration */}
              <div className="space-y-3">
                <div>
                  <p className="text-themed-primary font-medium text-sm mb-1">Clearing Threads</p>
                  <p className="text-xs text-themed-muted">
                    Higher = faster (max: {cpuCount} CPU{cpuCount > 1 ? 's' : ''})
                  </p>
                </div>
                <div className="flex items-center gap-3">
                  <Button
                    size="sm"
                    variant="default"
                    onClick={() => handleThreadCountChange(threadCount - 1)}
                    disabled={
                      threadCount <= 1 ||
                      threadCountLoading ||
                      mockMode ||
                      isCacheClearing ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                    title={cacheReadOnly ? 'Cache directory is read-only' : undefined}
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
                    disabled={
                      threadCount >= cpuCount ||
                      threadCountLoading ||
                      mockMode ||
                      isCacheClearing ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                    title={cacheReadOnly ? 'Cache directory is read-only' : undefined}
                  >
                    +
                  </Button>
                </div>
              </div>
            </div>
          </>
        )}
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
            <code className="bg-themed-tertiary px-1 py-0.5 rounded">{config.cachePath}</code>.
            Games will need to redownload content after clearing.
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
    </>
  );
};

export default CacheManager;
