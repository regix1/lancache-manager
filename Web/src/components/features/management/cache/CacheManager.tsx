import React, { useState, useEffect, use, useRef, useCallback } from 'react';
import { Server, AlertTriangle, FolderOpen, CheckCircle, XCircle, HardDrive, Lock, RefreshCw, Clock, Loader2 } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import { useCacheSize } from '@contexts/CacheSizeContext';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { Tooltip } from '@components/ui/Tooltip';
import type { Config, DatasourceInfo } from '../../../../types';

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

  // Cache size from global context (persists across navigation)
  const { cacheSize, isLoading: cacheSizeLoading, error: cacheSizeError, fetchCacheSize, clearCacheSize } = useCacheSize();

  const [showConfirmModal, setShowConfirmModal] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [deleteMode, setDeleteMode] = useState<'preserve' | 'full' | 'rsync'>(config.cacheDeleteMode as 'preserve' | 'full' | 'rsync');
  const [deleteModeLoading, setDeleteModeLoading] = useState(false);
  const [isCacheClearing, setIsCacheClearing] = useState(false);
  const [clearingDatasource, setClearingDatasource] = useState<string | null>(null); // null = all, string = specific
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const cacheOperationInProgressRef = useRef(false);
  const deleteModeChangeInProgressRef = useRef(false);

  // Fetch cache size on mount if not already loaded
  useEffect(() => {
    if (!mockMode && !cacheReadOnly && !cacheSize && !cacheSizeLoading) {
      fetchCacheSize();
    }
  }, [mockMode, cacheReadOnly, cacheSize, cacheSizeLoading, fetchCacheSize]);

  // Get estimated time based on current delete mode
  const getEstimatedTime = useCallback(() => {
    if (!cacheSize) return null;
    const times = cacheSize.estimatedDeletionTimes;
    switch (deleteMode) {
      case 'preserve':
        return times.preserveFormatted;
      case 'full':
        return times.fullFormatted;
      case 'rsync':
        return times.rsyncFormatted;
      default:
        return times.preserveFormatted;
    }
  }, [cacheSize, deleteMode]);

  const toggleExpanded = (name: string) => {
    setExpandedDatasources(prev => {
      const next = new Set(prev);
      if (next.has(name)) {
        next.delete(name);
      } else {
        next.add(name);
      }
      return next;
    });
  };

  // Listen for cache clear completion (via SignalR for UI state only)
  useEffect(() => {
    if (mockMode) return;

    const handleCacheClearComplete = () => {
      setIsCacheClearing(false);
      // Clear the cached size so it refetches with new values
      clearCacheSize();
    };

    signalR.on('CacheClearComplete', handleCacheClearComplete);

    return () => {
      signalR.off('CacheClearComplete', handleCacheClearComplete);
    };
  }, [mockMode, signalR, clearCacheSize]);

  const handleDeleteModeChange = async (newMode: 'preserve' | 'full' | 'rsync') => {
    // Skip if already selected
    if (newMode === deleteMode) {
      return;
    }

    // Prevent double-clicks
    if (deleteModeChangeInProgressRef.current) {
      return;
    }
    deleteModeChangeInProgressRef.current = true;

    setDeleteModeLoading(true);
    try {
      await ApiService.setCacheDeleteMode(newMode);
      setDeleteMode(newMode);
      const modeDesc =
        newMode === 'rsync' ? 'Rsync' : newMode === 'full' ? 'Remove All' : 'Preserve';
      onSuccess?.(`Delete mode set to: ${modeDesc}`);
    } catch (err: unknown) {
      console.error('Failed to update delete mode:', err);
      onError?.((err instanceof Error ? err.message : String(err)) || 'Failed to update delete mode');
    } finally {
      setDeleteModeLoading(false);
      deleteModeChangeInProgressRef.current = false;
    }
  };


  const handleClearCache = (datasourceName: string | null = null) => {
    if (authMode !== 'authenticated') {
      onError?.('Full authentication required for management operations');
      return;
    }

    setClearingDatasource(datasourceName);
    setShowConfirmModal(true);
  };

  const startCacheClear = async () => {
    // Prevent double-clicks
    if (cacheOperationInProgressRef.current) {
      return;
    }
    cacheOperationInProgressRef.current = true;

    setActionLoading(true);
    setShowConfirmModal(false);

    // Set clearing state BEFORE API call to avoid race condition
    // SignalR completion event may arrive before API returns
    setIsCacheClearing(true);

    try {
      if (clearingDatasource) {
        await ApiService.clearDatasourceCache(clearingDatasource);
      } else {
        await ApiService.clearAllCache();
      }
      // NotificationsContext handles success/error messages via SignalR
    } catch (err: unknown) {
      onError?.('Failed to start cache clearing: ' + ((err instanceof Error ? err.message : String(err)) || 'Unknown error'));
      setIsCacheClearing(false);
    } finally {
      setActionLoading(false);
      cacheOperationInProgressRef.current = false;
    }
  };


  return (
    <>
      <Card>
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-green flex-shrink-0">
              <Server className="w-5 h-5 icon-green" />
            </div>
            <div>
              <h3 className="text-lg font-semibold text-themed-primary">Disk Cache Management</h3>
              <p className="text-xs text-themed-muted">Clear cached game files from disk</p>
            </div>
            <HelpPopover position="left" width={300}>
              <HelpSection title="Deletion Methods">
                <div className="space-y-1.5">
                  <HelpDefinition term="Preserve" termColor="blue">
                    Deletes files individually, keeps directory structure intact
                  </HelpDefinition>
                  <HelpDefinition term="Remove All" termColor="green">
                    Removes entire directories at once — fastest for local storage
                  </HelpDefinition>
                  <HelpDefinition term="Rsync" termColor="purple">
                    Uses rsync --delete — reliable for network storage (NFS/SMB)
                  </HelpDefinition>
                </div>
              </HelpSection>

              <HelpNote type="warning">
                Clearing cache deletes all cached game files.
                Games will need to redownload content.
              </HelpNote>
            </HelpPopover>
          </div>
          <div className="flex items-center gap-2">
            <Tooltip content={cacheReadOnly ? 'Cache is read-only' : 'Cache is writable'} position="top">
              <span className="flex items-center gap-0.5">
                <HardDrive className="w-3.5 h-3.5 text-themed-muted" />
                {cacheReadOnly ? (
                  <XCircle className="w-4 h-4" style={{ color: 'var(--theme-warning)' }} />
                ) : (
                  <CheckCircle className="w-4 h-4" style={{ color: 'var(--theme-success-text)' }} />
                )}
              </span>
            </Tooltip>
          </div>
        </div>

        {/* Read-Only Warning */}
        {cacheReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">Cache directory is read-only</p>
              <p className="text-sm mt-1">
                Remove <code className="bg-themed-tertiary px-1 rounded">:ro</code> from your
                docker-compose volume mounts to enable cache clearing.
              </p>
            </div>
          </Alert>
        )}

        {cacheReadOnly ? (
          <div className="flex items-center justify-center py-4">
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
            {/* Main Cache Path and Clear Buttons */}
            {(() => {
              // Get datasources - use dataSources array if available, otherwise create single entry from legacy config
              const datasources: DatasourceInfo[] = config.dataSources && config.dataSources.length > 0
                ? config.dataSources
                : [{
                    name: 'default',
                    cachePath: config.cachePath || '/cache',
                    logsPath: config.logsPath || '/logs',
                    cacheWritable: config.cacheWritable ?? false,
                    logsWritable: config.logsWritable ?? false,
                    enabled: true
                  }];
              const hasMultiple = datasources.length > 1;
              const isExpanded = (name: string) => expandedDatasources.has(name);

              return (
                <div className="mb-6">
                  {/* Datasource list */}
                  <div className="space-y-3 mb-4">
                    {datasources.map((ds) => (
                      <DatasourceListItem
                        key={ds.name}
                        name={ds.name}
                        path={ds.cachePath}
                        isExpanded={isExpanded(ds.name)}
                        onToggle={() => toggleExpanded(ds.name)}
                        enabled={ds.enabled && ds.cacheWritable}
                      >
                        {/* Expanded content */}
                        <div className="pt-3">
                          <Button
                            variant="filled"
                            color="red"
                            size="sm"
                            onClick={(e) => {
                              e.stopPropagation();
                              handleClearCache(ds.name);
                            }}
                            disabled={
                              actionLoading ||
                              mockMode ||
                              isCacheClearing ||
                              authMode !== 'authenticated' ||
                              cacheReadOnly ||
                              !ds.cacheWritable
                            }
                            loading={isCacheClearing && clearingDatasource === ds.name}
                            fullWidth
                            title={!ds.cacheWritable ? 'Cache directory is read-only' : `Clear ${ds.name} cache`}
                          >
                            {isCacheClearing && clearingDatasource === ds.name ? 'Clearing...' : 'Clear Cache'}
                          </Button>
                        </div>
                      </DatasourceListItem>
                    ))}
                  </div>

                  {/* Warning and Clear All button */}
                  <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
                    <p className="text-xs text-themed-muted flex items-center gap-1.5">
                      <AlertTriangle className="w-3.5 h-3.5 text-themed-accent flex-shrink-0" />
                      <span>Clearing cache deletes ALL cached game files from disk</span>
                    </p>
                    {hasMultiple && (
                      <Button
                        variant="filled"
                        color="red"
                        size="sm"
                        onClick={() => handleClearCache(null)}
                        disabled={
                          actionLoading ||
                          mockMode ||
                          isCacheClearing ||
                          authMode !== 'authenticated' ||
                          cacheReadOnly
                        }
                        loading={actionLoading && !clearingDatasource}
                        title={cacheReadOnly ? 'Cache directory is mounted read-only' : undefined}
                      >
                        {isCacheClearing && !clearingDatasource ? 'Clearing All...' : 'Clear All Caches'}
                      </Button>
                    )}
                  </div>
                </div>
              );
            })()}

            {/* Cache Size Info */}
            <div className="p-4 rounded-lg bg-themed-tertiary/30 mb-4">
              <div className="flex items-center justify-between mb-2">
                <p className="text-themed-primary font-medium text-sm">Cache Size</p>
                <Tooltip content="Refresh cache size" position="top">
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={fetchCacheSize}
                    disabled={cacheSizeLoading || isCacheClearing}
                    className="!p-1"
                  >
                    {cacheSizeLoading ? (
                      <Loader2 className="w-3.5 h-3.5 animate-spin text-themed-muted" />
                    ) : (
                      <RefreshCw className="w-3.5 h-3.5 text-themed-muted" />
                    )}
                  </Button>
                </Tooltip>
              </div>
              
              {cacheSizeError ? (
                <p className="text-xs text-themed-error">{cacheSizeError}</p>
              ) : cacheSizeLoading && !cacheSize ? (
                <div className="flex items-center gap-2 text-xs text-themed-muted">
                  <Loader2 className="w-3 h-3 animate-spin" />
                  <span>Calculating cache size...</span>
                </div>
              ) : cacheSize ? (
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-themed-muted">Total Size</span>
                    <span className="text-sm font-semibold text-themed-primary">{cacheSize.formattedSize}</span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-themed-muted">Files</span>
                    <span className="text-sm text-themed-secondary">{cacheSize.totalFiles.toLocaleString()}</span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-themed-muted">Directories</span>
                    <span className="text-sm text-themed-secondary">{cacheSize.hexDirectories.toLocaleString()}</span>
                  </div>
                  {getEstimatedTime() && (
                    <div className="flex items-center justify-between pt-2 border-t border-themed-secondary">
                      <span className="text-xs text-themed-muted flex items-center gap-1">
                        <Clock className="w-3 h-3" />
                        Est. deletion time
                      </span>
                      <span className="text-sm text-themed-secondary">{getEstimatedTime()}</span>
                    </div>
                  )}
                </div>
              ) : (
                <p className="text-xs text-themed-muted">Click refresh to calculate cache size</p>
              )}
            </div>

            {/* Configuration Options */}
            <div className="p-4 rounded-lg bg-themed-tertiary/30">
              {/* Delete Mode Configuration */}
              <div className="space-y-3">
                <div>
                  <p className="text-themed-primary font-medium text-sm mb-1">Deletion Method</p>
                  <p className="text-xs text-themed-muted">
                    {deleteMode === 'rsync'
                      ? 'Rsync with empty directory (best for NFS/SMB)'
                      : deleteMode === 'full'
                        ? 'Remove entire directories at once'
                        : 'Delete files individually (keeps structure)'}
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
                    Preserve
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
                    Remove All
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
            <span>{clearingDatasource ? `Clear ${clearingDatasource} Cache` : 'Clear All Caches'}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          {(() => {
            const datasources: DatasourceInfo[] = config.dataSources && config.dataSources.length > 0
              ? config.dataSources
              : [{
                  name: 'default',
                  cachePath: config.cachePath || '/cache',
                  logsPath: config.logsPath || '/logs',
                  cacheWritable: config.cacheWritable ?? false,
                  logsWritable: config.logsWritable ?? false,
                  enabled: true
                }];

            // Show specific datasource or all
            if (clearingDatasource) {
              const targetDs = datasources.find(ds => ds.name === clearingDatasource);
              return (
                <p className="text-themed-secondary">
                  This will permanently delete <strong>all cached game files</strong> from the <strong>{clearingDatasource}</strong> cache at{' '}
                  <code className="bg-themed-tertiary px-1 py-0.5 rounded">{targetDs?.cachePath || 'unknown'}</code>.
                  Games will need to redownload content after clearing.
                </p>
              );
            }

            // Show all datasources
            return (
              <>
                <p className="text-themed-secondary">
                  This will permanently delete <strong>all cached game files</strong> from {datasources.length > 1 ? 'all datasources' : 'the cache'}.
                  Games will need to redownload content after clearing.
                </p>
                <div className="space-y-1.5 p-3 rounded-lg bg-themed-tertiary/50">
                  {datasources.map((ds) => (
                    <div key={ds.name} className="flex items-center gap-2 text-xs">
                      <FolderOpen className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                      <span className="font-medium text-themed-primary">{ds.name}:</span>
                      <code className="text-themed-secondary truncate">{ds.cachePath}</code>
                    </div>
                  ))}
                </div>
              </>
            );
          })()}

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
              onClick={startCacheClear}
              loading={actionLoading}
            >
              {clearingDatasource ? `Delete ${clearingDatasource} Cache` : 'Delete All Caches'}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CacheManager;
