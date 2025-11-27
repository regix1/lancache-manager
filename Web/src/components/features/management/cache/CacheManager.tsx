import React, { useState, useEffect, use } from 'react';
import { Server, Trash2, AlertTriangle, Lock } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import type { Config } from '../../../../types';

// Fetch initial cache configuration data
const fetchCacheConfig = async (): Promise<Config> => {
  return await ApiService.getConfig();
};


const fetchRsyncAvailability = async (): Promise<boolean> => {
  try {
    const data = await ApiService.isRsyncAvailable();
    return data.available;
  } catch (err) {
    console.error('Failed to check rsync availability:', err);
    return false;
  }
};

const fetchDirectoryPermissions = async (): Promise<boolean> => {
  try {
    const data = await ApiService.getDirectoryPermissions();
    return data.cache.readOnly;
  } catch (err) {
    console.error('Failed to check directory permissions:', err);
    return false; // Assume writable on error
  }
};

// Cache promises to avoid refetching on every render
let configPromise: Promise<Config> | null = null;
let rsyncPromise: Promise<boolean> | null = null;
let permissionsPromise: Promise<boolean> | null = null;

const getCacheConfigPromise = () => {
  if (!configPromise) {
    configPromise = fetchCacheConfig();
  }
  return configPromise;
};

const getRsyncPromise = () => {
  if (!rsyncPromise) {
    rsyncPromise = fetchRsyncAvailability();
  }
  return rsyncPromise;
};

const getPermissionsPromise = () => {
  if (!permissionsPromise) {
    permissionsPromise = fetchDirectoryPermissions();
  }
  return permissionsPromise;
};

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

  // Use the 'use' hook to load data
  const config = use(getCacheConfigPromise());
  const rsyncAvailable = use(getRsyncPromise());
  const cacheReadOnly = use(getPermissionsPromise());

  const [showConfirmModal, setShowConfirmModal] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [deleteMode, setDeleteMode] = useState<'preserve' | 'full' | 'rsync'>(config.cacheDeleteMode as 'preserve' | 'full' | 'rsync');
  const [deleteModeLoading, setDeleteModeLoading] = useState(false);
  const [isCacheClearing, setIsCacheClearing] = useState(false);

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
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-green">
              <Server className="w-5 h-5 icon-green" />
            </div>
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
            <div className="flex items-center gap-3 mb-6">
              <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-green">
                <Server className="w-5 h-5 icon-green" />
              </div>
              <h3 className="text-lg font-semibold text-themed-primary">Disk Cache Management</h3>
              <HelpPopover position="left" width={300}>
                <HelpSection title="Deletion Methods">
                  <div className="space-y-1.5">
                    <HelpDefinition term="Safe Mode" termColor="blue">
                      Individual file deletion — slower but keeps directory structure
                    </HelpDefinition>
                    <HelpDefinition term="Fast Mode" termColor="green">
                      Full directory removal — faster for local storage
                    </HelpDefinition>
                    <HelpDefinition term="Rsync" termColor="purple">
                      Sync with empty directory — best for network storage
                    </HelpDefinition>
                  </div>
                </HelpSection>

                <HelpNote type="warning">
                  Clearing cache deletes all cached game files.
                  Games will need to redownload content.
                </HelpNote>
              </HelpPopover>
            </div>

            {/* Main Cache Path and Clear Button */}
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-6">
              <div className="flex-1">
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
                  cacheReadOnly
                }
                loading={actionLoading}
                className="w-full sm:w-48"
                title={cacheReadOnly ? 'Cache directory is mounted read-only' : undefined}
              >
                {isCacheClearing ? 'Clearing...' : 'Clear Cache'}
              </Button>
            </div>

            {/* Configuration Options */}
            <div className="p-4 rounded-lg bg-themed-tertiary/30">
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
