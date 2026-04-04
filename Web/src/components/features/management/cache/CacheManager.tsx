import React, { useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Server, AlertTriangle, FolderOpen, Clock, RefreshCw, HardDrive } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useConfig } from '@contexts/useConfig';
import { useCacheSize } from '@contexts/useCacheSize';
import { useNotifications } from '@contexts/notifications';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { Tooltip } from '@components/ui/Tooltip';
import { ReadOnlyBadge } from '@components/ui/ManagerCard';
import { AccordionSection } from '@components/ui/AccordionSection';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatBytes, formatCount } from '@utils/formatters';
import type { DatasourceInfo } from '../../../../types';

const formatScanTime = (timestamp: string): string => {
  try {
    const date = new Date(timestamp);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    if (diffHours < 24) return `${diffHours}h ago`;
    return `${diffDays}d ago`;
  } catch {
    return '';
  }
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
  const { config, refreshConfig } = useConfig();

  // Rsync availability check
  const [rsyncAvailable, setRsyncAvailable] = useState(false);
  useEffect(() => {
    ApiService.isRsyncAvailable()
      .then((data: { available: boolean }) => setRsyncAvailable(data.available))
      .catch((err: unknown) => console.error('Failed to check rsync availability:', err));
  }, []);

  // Directory permissions from shared hook (auto-refreshes via SignalR)
  const { cacheReadOnly } = useDirectoryPermissions();

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
  const [sectionExpanded, setSectionExpanded] = useState(() => {
    const saved = localStorage.getItem('management-disk-cache-expanded');
    return saved !== null ? saved === 'true' : false;
  });

  useEffect(() => {
    localStorage.setItem('management-disk-cache-expanded', String(sectionExpanded));
  }, [sectionExpanded]);
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
      await refreshConfig();
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

  // Header actions
  const headerActions = (
    <div className="flex items-center gap-2">
      <Tooltip content={t('management.cache.refreshCacheSize')} position="top">
        <Button
          onClick={() => fetchCacheSize(true)}
          disabled={cacheSizeLoading || isAnyRemovalRunning}
          variant="default"
          size="sm"
        >
          {cacheSizeLoading ? <LoadingSpinner inline size="sm" /> : t('common.refresh')}
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
        <AccordionSection
          title={t('management.cache.title')}
          icon={Server}
          iconColor="var(--theme-icon-green)"
          isExpanded={sectionExpanded}
          onToggle={() => setSectionExpanded((prev) => !prev)}
        >
          <div className="space-y-4">
            {/* Action toolbar */}
            <div className="flex flex-wrap items-center justify-end gap-2">{headerActions}</div>

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
                                : t('management.cache.clearDatasourceCache', {
                                    datasource: ds.name
                                  })
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
                <div className="space-y-3">
                  <div className="flex items-center justify-between">
                    <p className="text-themed-primary font-medium text-sm">
                      {t('management.cache.cacheSize')}
                    </p>
                    <Tooltip content={t('management.cache.refreshCacheSize')} position="top">
                      <Button
                        variant="subtle"
                        size="sm"
                        onClick={() => fetchCacheSize(true)}
                        disabled={cacheSizeLoading || isAnyRemovalRunning}
                      >
                        {cacheSizeLoading ? (
                          <LoadingSpinner inline size="sm" />
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
                      <LoadingSpinner inline size="xs" />
                      <span>{t('management.cache.calculatingSize')}</span>
                    </div>
                  ) : cacheSize ? (
                    <>
                      {/* Cache size stat block */}
                      <div className="rounded-lg border border-themed-secondary p-3 flex items-center gap-3">
                        <div className="w-9 h-9 rounded-lg flex items-center justify-center icon-bg-blue flex-shrink-0">
                          <HardDrive className="w-4 h-4 icon-blue" />
                        </div>
                        <div className="min-w-0">
                          <p className="text-2xl font-bold text-themed-primary truncate">
                            {formatBytes(cacheSize.totalBytes)}
                          </p>
                          <p className="text-xs text-themed-muted">
                            {t('management.cache.files')} · {formatCount(cacheSize.totalFiles)} ·{' '}
                            {t('management.cache.directories')} ·{' '}
                            {formatCount(cacheSize.hexDirectories)}
                          </p>
                        </div>
                      </div>
                      {/* Secondary info bar */}
                      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-themed-muted px-1 py-1">
                        {getEstimatedTime() && (
                          <div className="flex items-center gap-1.5">
                            <Clock className="w-3 h-3" />
                            <span>{t('management.cache.estDeletionTime')}</span>
                            <span className="font-medium text-themed-secondary">
                              {getEstimatedTime()}
                            </span>
                          </div>
                        )}
                        <div className="flex items-center gap-1.5">
                          <RefreshCw className="w-3 h-3" />
                          <span>
                            {cacheSize.isCached
                              ? t('management.cache.cachedScan', 'Cached scan')
                              : t('management.cache.freshScan', 'Fresh scan')}
                          </span>
                          <span className="font-medium text-themed-secondary">
                            {formatScanTime(cacheSize.timestamp)}
                          </span>
                        </div>
                      </div>
                    </>
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
                        title={
                          cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined
                        }
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
                        title={
                          cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined
                        }
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
          </div>
        </AccordionSection>
      </Card>

      <ConfirmationModal
        opened={showConfirmModal}
        onClose={() => setShowConfirmModal(false)}
        onConfirm={startCacheClear}
        title={
          clearingDatasource
            ? t('management.cache.confirmClear', { datasource: clearingDatasource })
            : t('management.cache.confirmClearAll')
        }
        confirmLabel={
          clearingDatasource
            ? t('management.cache.modal.deleteDatasourceCache', {
                datasource: clearingDatasource
              })
            : t('management.cache.modal.deleteAllCaches')
        }
        loading={actionLoading}
      >
        {clearingDatasource ? (
          <p className="text-themed-secondary">
            {t('management.cache.modal.deleteFromDatasource', {
              datasource: clearingDatasource,
              path: datasources.find((ds) => ds.name === clearingDatasource)?.cachePath || 'unknown'
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
      </ConfirmationModal>
    </>
  );
};

export default CacheManager;
