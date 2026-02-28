import React, { use, useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Server, AlertTriangle, FolderOpen, Clock, Loader2, RefreshCw } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext';
import { useCacheSize } from '@contexts/CacheSizeContext';
import { useNotifications } from '@contexts/notifications';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { Tooltip } from '@components/ui/Tooltip';
import { ManagerCardHeader, ReadOnlyBadge } from '@components/ui/ManagerCard';
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

// Cache promises to avoid refetching on every render
let configPromise: Promise<Config> | null = null;
let rsyncPromise: Promise<boolean> | null = null;

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

interface CacheManagerProps {
  isAdmin: boolean;
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
  const { t } = useTranslation();
  const signalR = useSignalR();

  // Use the 'use' hook to load data
  const config = use(getCacheConfigPromise());
  const rsyncAvailable = use(getRsyncPromise());

  // Directory permissions from shared hook (auto-refreshes via SignalR)
  const { cacheReadOnly, checkingPermissions: checkingCachePermissions } =
    useDirectoryPermissions();

  // Cache size from global context (persists across navigation)
  const {
    cacheSize,
    isLoading: cacheSizeLoading,
    error: cacheSizeError,
    fetchCacheSize,
    clearCacheSize
  } = useCacheSize();
  const { notifications, addNotification, isAnyRemovalRunning } = useNotifications();

  // Derive cache clearing state from notifications (standardized pattern)
  const activeCacheClearNotification = notifications.find(
    (n) => n.type === 'cache_clearing' && n.status === 'running'
  );
  const isCacheClearing = !!activeCacheClearNotification;

  const [showConfirmModal, setShowConfirmModal] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [deleteMode, setDeleteMode] = useState<'preserve' | 'full' | 'rsync'>(
    config.cacheDeleteMode as 'preserve' | 'full' | 'rsync'
  );
  const [deleteModeLoading, setDeleteModeLoading] = useState(false);
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

  // Show notification when cache size fetch fails
  useEffect(() => {
    if (cacheSizeError) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.cache.cacheSizeError', 'Cache Size Error'),
        detailMessage: cacheSizeError,
        details: { notificationType: 'error' }
      });
    }
  }, [cacheSizeError, addNotification, t]);

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
    setExpandedDatasources((prev) => {
      const next = new Set(prev);
      if (next.has(name)) {
        next.delete(name);
      } else {
        next.add(name);
      }
      return next;
    });
  };

  // Listen for cache clear completion to refresh cache size
  // Note: NotificationsContext handles the operation state via cache_clearing notifications
  useEffect(() => {
    if (mockMode) return;

    const handleCacheClearingComplete = () => {
      // Clear the cached size so it refetches with new values
      clearCacheSize();
    };

    signalR.on('CacheClearingComplete', handleCacheClearingComplete);

    return () => {
      signalR.off('CacheClearingComplete', handleCacheClearingComplete);
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
        newMode === 'rsync'
          ? t('management.cache.deleteModes.rsync')
          : newMode === 'full'
            ? t('management.cache.deleteModes.removeAll')
            : t('management.cache.deleteModes.preserve');
      onSuccess?.(t('management.cache.deleteModeSet', { mode: modeDesc }));
    } catch (err: unknown) {
      console.error('Failed to update delete mode:', err);
      onError?.(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.cache.errors.updateDeleteMode')
      );
    } finally {
      setDeleteModeLoading(false);
      deleteModeChangeInProgressRef.current = false;
    }
  };

  const handleClearCache = (datasourceName: string | null = null) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
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

    // Note: NotificationsContext automatically handles cache clearing state via SignalR events
    // (CacheClearingProgress and CacheClearingComplete). No need to manually manage isCacheClearing.

    try {
      if (clearingDatasource) {
        await ApiService.clearDatasourceCache(clearingDatasource);
      } else {
        await ApiService.clearAllCache();
      }
      // NotificationsContext handles success/error messages via SignalR
    } catch (err: unknown) {
      onError?.(
        t('management.cache.errors.startCacheClearing', {
          error: (err instanceof Error ? err.message : String(err)) || t('common.unknownError')
        })
      );
      // Note: On error, NotificationsContext will handle the notification dismissal
    } finally {
      setActionLoading(false);
      cacheOperationInProgressRef.current = false;
    }
  };

  // Get datasources - use dataSources array if available, otherwise create single entry from legacy config
  const datasources: DatasourceInfo[] =
    config.dataSources && config.dataSources.length > 0
      ? config.dataSources
      : [
          {
            name: 'default',
            cachePath: config.cachePath || '/cache',
            logsPath: config.logsPath || '/logs',
            cacheWritable: config.cacheWritable ?? false,
            logsWritable: config.logsWritable ?? false,
            enabled: true
          }
        ];
  const hasMultipleDatasources = datasources.length > 1;

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.cache.help.title')} variant="subtle">
        <div className="divide-y divide-[var(--theme-text-muted)]">
          <div className="py-1.5 first:pt-0 last:pb-0">
            <div className="font-medium text-themed-primary">
              {t('management.cache.help.preserve.term')}
            </div>
            <div className="mt-0.5">{t('management.cache.help.preserve.description')}</div>
          </div>
          <div className="py-1.5 first:pt-0 last:pb-0">
            <div className="font-medium text-themed-primary">
              {t('management.cache.help.removeAll.term')}
            </div>
            <div className="mt-0.5">{t('management.cache.help.removeAll.description')}</div>
          </div>
          <div className="py-1.5 first:pt-0 last:pb-0">
            <div className="font-medium text-themed-primary">
              {t('management.cache.help.rsync.term')}
            </div>
            <div className="mt-0.5">{t('management.cache.help.rsync.description')}</div>
          </div>
        </div>
      </HelpSection>

      <HelpNote type="warning">{t('management.cache.help.warning')}</HelpNote>
    </HelpPopover>
  );

  // Header actions
  const headerActions = (
    <div className="flex items-center gap-2">
      <Tooltip content={t('management.cache.refreshCacheSize')} position="top">
        <Button
          onClick={fetchCacheSize}
          disabled={cacheSizeLoading || isAnyRemovalRunning}
          variant="subtle"
          size="sm"
        >
          {cacheSizeLoading ? <Loader2 className="w-4 h-4 animate-spin" /> : t('common.refresh')}
        </Button>
      </Tooltip>
      {hasMultipleDatasources && !cacheReadOnly && (
        <Button
          variant="filled"
          color="red"
          size="sm"
          onClick={() => handleClearCache(null)}
          disabled={
            actionLoading ||
            mockMode ||
            isAnyRemovalRunning ||
            authMode !== 'authenticated' ||
            cacheReadOnly
          }
          loading={actionLoading && !clearingDatasource}
          title={cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined}
        >
          {isCacheClearing && !clearingDatasource ? t('common.clearing') : t('common.clearAll')}
        </Button>
      )}
    </div>
  );

  return (
    <>
      <Card>
        <ManagerCardHeader
          icon={Server}
          iconColor="green"
          title={t('management.cache.title')}
          subtitle={t('management.cache.subtitle')}
          helpContent={helpContent}
          permissions={{
            cacheReadOnly,
            checkingPermissions: checkingCachePermissions
          }}
          actions={headerActions}
        />

        {/* Read-Only Warning */}
        {cacheReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">{t('management.cache.alerts.readOnly.title')}</p>
              <p className="text-sm mt-1">
                {t('management.cache.alerts.readOnly.descriptionPrefix')}{' '}
                <code className="bg-themed-tertiary px-1 rounded">:ro</code>{' '}
                {t('management.cache.alerts.readOnly.descriptionSuffix')}
              </p>
            </div>
          </Alert>
        )}

        {cacheReadOnly ? (
          <ReadOnlyBadge />
        ) : (
          <>
            {/* Datasource list */}
            <div className="mb-6">
              <div className="space-y-3 mb-4">
                {datasources.map((ds) => (
                  <DatasourceListItem
                    key={ds.name}
                    name={ds.name}
                    path={ds.cachePath}
                    isExpanded={expandedDatasources.has(ds.name)}
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
                          isAnyRemovalRunning ||
                          authMode !== 'authenticated' ||
                          cacheReadOnly ||
                          !ds.cacheWritable
                        }
                        loading={isCacheClearing && clearingDatasource === ds.name}
                        fullWidth
                        title={
                          !ds.cacheWritable
                            ? t('management.cache.alerts.readOnly.title')
                            : t('management.cache.clearDatasourceCache', { datasource: ds.name })
                        }
                      >
                        {isCacheClearing && clearingDatasource === ds.name
                          ? t('common.clearing')
                          : t('management.cache.clearCache')}
                      </Button>
                    </div>
                  </DatasourceListItem>
                ))}
              </div>

              {/* Warning */}
              <p className="text-xs text-themed-muted flex items-center gap-1.5">
                <AlertTriangle className="w-3.5 h-3.5 text-themed-accent flex-shrink-0" />
                <span>{t('management.cache.clearingCacheDeletes')}</span>
              </p>
            </div>

            {/* Cache Size Info */}
            <div className="p-4 rounded-lg bg-themed-tertiary/30 mb-4">
              <div className="flex items-center justify-between mb-2">
                <p className="text-themed-primary font-medium text-sm">
                  {t('management.cache.cacheSize')}
                </p>
                <Tooltip content={t('management.cache.refreshCacheSize')} position="top">
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={fetchCacheSize}
                    disabled={cacheSizeLoading || isAnyRemovalRunning}
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
                  <span>{t('management.cache.calculatingSize')}</span>
                </div>
              ) : cacheSize ? (
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-themed-muted">
                      {t('management.cache.totalSize')}
                    </span>
                    <span className="text-sm font-semibold text-themed-primary">
                      {cacheSize.formattedSize}
                    </span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-themed-muted">{t('management.cache.files')}</span>
                    <span className="text-sm text-themed-secondary">
                      {cacheSize.totalFiles.toLocaleString()}
                    </span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-themed-muted">
                      {t('management.cache.directories')}
                    </span>
                    <span className="text-sm text-themed-secondary">
                      {cacheSize.hexDirectories.toLocaleString()}
                    </span>
                  </div>
                  {getEstimatedTime() && (
                    <div className="flex items-center justify-between pt-2 border-t border-themed-secondary">
                      <span className="text-xs text-themed-muted flex items-center gap-1">
                        <Clock className="w-3 h-3" />
                        {t('management.cache.estDeletionTime')}
                      </span>
                      <span className="text-sm text-themed-secondary">{getEstimatedTime()}</span>
                    </div>
                  )}
                </div>
              ) : (
                <p className="text-xs text-themed-muted">
                  {t('management.cache.clickRefreshToCalculate')}
                </p>
              )}
            </div>

            {/* Configuration Options */}
            <div className="p-4 rounded-lg bg-themed-tertiary/30">
              {/* Delete Mode Configuration */}
              <div className="space-y-3">
                <div>
                  <p className="text-themed-primary font-medium text-sm mb-1">
                    {t('management.cache.deletionMethod')}
                  </p>
                  <p className="text-xs text-themed-muted">
                    {deleteMode === 'rsync'
                      ? t('management.cache.deletionMethods.rsyncDesc')
                      : deleteMode === 'full'
                        ? t('management.cache.deletionMethods.fullDesc')
                        : t('management.cache.deletionMethods.preserveDesc')}
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
                      isAnyRemovalRunning ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                    title={cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined}
                  >
                    {t('management.cache.deleteModes.preserve')}
                  </Button>
                  <Button
                    size="sm"
                    variant={deleteMode === 'full' ? 'filled' : 'default'}
                    color={deleteMode === 'full' ? 'green' : undefined}
                    onClick={() => handleDeleteModeChange('full')}
                    disabled={
                      deleteModeLoading ||
                      mockMode ||
                      isAnyRemovalRunning ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                    title={cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined}
                  >
                    {t('management.cache.deleteModes.removeAll')}
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
                        isAnyRemovalRunning ||
                        authMode !== 'authenticated' ||
                        cacheReadOnly
                      }
                      title={
                        cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined
                      }
                    >
                      {t('management.cache.deleteModes.rsync')}
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
            <span>
              {clearingDatasource
                ? t('management.cache.confirmClear', { datasource: clearingDatasource })
                : t('management.cache.confirmClearAll')}
            </span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          {clearingDatasource ? (
            <p className="text-themed-secondary">
              {t('management.cache.modal.deleteFromDatasource', {
                datasource: clearingDatasource,
                path:
                  datasources.find((ds) => ds.name === clearingDatasource)?.cachePath || 'unknown'
              })}
            </p>
          ) : (
            <>
              <p className="text-themed-secondary">
                {t('management.cache.modal.deleteFromAll', { count: datasources.length })}
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
          )}

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('management.cache.alerts.important')}</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.cache.modal.cannotBeUndone')}</li>
                <li>{t('management.cache.modal.stopActiveDownloads')}</li>
                <li>{t('management.cache.modal.historyPreserved')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowConfirmModal(false)}
              disabled={actionLoading}
            >
              {t('common.cancel')}
            </Button>
            <Button variant="filled" color="red" onClick={startCacheClear} loading={actionLoading}>
              {clearingDatasource
                ? t('management.cache.modal.deleteDatasourceCache', {
                    datasource: clearingDatasource
                  })
                : t('management.cache.modal.deleteAllCaches')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CacheManager;
