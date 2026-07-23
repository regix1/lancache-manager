import React, { useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Server, AlertTriangle, FolderOpen, RefreshCw, Trash2 } from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useConfig } from '@contexts/useConfig';
import { useCacheSize } from '@contexts/useCacheSize';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { useNotifications } from '@contexts/notifications';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import CardDirectoryNotice from '@components/features/management/CardDirectoryNotice';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import { isCardDiskActionBlocked, resolveCardNotice } from '@utils/cardDirectoryNotice';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuItem, ActionMenuDangerItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { formatBytes, formatCount } from '@utils/formatters';
import { getErrorMessage } from '@utils/error';
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
  const { cacheReadOnly, logsReadOnly, cacheExist, logsExist, checkingPermissions } =
    useDirectoryPermissionsContext();
  const signalR = useSignalR();
  const { config, updateConfig } = useConfig();

  // Rsync availability check
  const [rsyncAvailable, setRsyncAvailable] = useState(false);
  useEffect(() => {
    ApiService.isRsyncAvailable()
      .then((data: { available: boolean }) => setRsyncAvailable(data.available))
      .catch((err: unknown) => console.error('Failed to check rsync availability:', err));
  }, []);

  // Cache size from global context (persists across navigation)
  const {
    cacheSize,
    isLoading: cacheSizeLoading,
    hasFetched: hasFetchedCacheSize,
    error: cacheSizeError,
    fetchCacheSize,
    clearCacheSize
  } = useCacheSize();
  const { refreshStats } = useStats();
  const { addNotification, isAnyRemovalRunning } = useNotifications();

  // Derive cache clearing state from notifications (standardized pattern)
  const isCacheClearing = useOperationBusy({ types: ['cache_clearing'] });
  // Own-card gate: any running OR queued cache clear (all or per-datasource)
  // disables every Clear button in this card; other cards' removals still enqueue.
  const isCacheClearActive = useOperationBusy({
    types: ['cache_clearing'],
    status: ['running', 'waiting']
  });

  // Wait-queue model: a running cache file scan no longer disables the Clear buttons
  // (clicking enqueues; the purple waiting card is the feedback). This flag now only
  // (a) gates the Refresh button - refresh triggers the SAME scan, a meaningless
  // re-click - and (b) drives the "will start after..." tooltip on the Clear buttons.
  const isCacheSizeScanRunning = useOperationBusy({
    types: ['cache_size_scan'],
    status: ['running', 'waiting']
  });

  const [showConfirmModal, setShowConfirmModal] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [deleteMode, setDeleteMode] = useState<'preserve' | 'full' | 'rsync'>(
    config.cacheDeleteMode as 'preserve' | 'full' | 'rsync'
  );
  const [deleteModeLoading, setDeleteModeLoading] = useState(false);
  const [clearingDatasource, setClearingDatasource] = useState<string | null>(null); // null = all, string = specific
  // Own-run gate: a running clear-all disables the Clear All button (a re-click is a
  // no-op); a per-datasource clear leaves it clickable so the click can enqueue.
  const isClearAllRunning = isCacheClearing && !clearingDatasource;
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [sectionExpanded, setSectionExpanded] = useState(() => {
    const saved = localStorage.getItem('management-disk-cache-expanded');
    return saved !== null ? saved === 'true' : false;
  });
  useAccordionGroupItem('storage-cache', sectionExpanded, () =>
    setSectionExpanded((prev) => !prev)
  );

  useEffect(() => {
    localStorage.setItem('management-disk-cache-expanded', String(sectionExpanded));
  }, [sectionExpanded]);
  const cacheOperationInProgressRef = useRef(false);
  const deleteModeChangeInProgressRef = useRef(false);

  const handleRefreshCacheSize = useCallback(async () => {
    await fetchCacheSize(true);
    await refreshStats(true);
  }, [fetchCacheSize, refreshStats]);

  // Read the persisted cache-size result once. A valid empty response marks the context as
  // fetched so the empty state cannot turn into a request loop; only Refresh starts a scan.
  useEffect(() => {
    if (
      !mockMode &&
      !cacheReadOnly &&
      !hasFetchedCacheSize &&
      !cacheSizeLoading &&
      !cacheSizeError
    ) {
      fetchCacheSize();
    }
  }, [
    mockMode,
    cacheReadOnly,
    hasFetchedCacheSize,
    cacheSizeLoading,
    cacheSizeError,
    fetchCacheSize
  ]);

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
      const response = await ApiService.setCacheDeleteMode(newMode);
      const confirmedMode = response.deleteMode as 'preserve' | 'full' | 'rsync';
      setDeleteMode(confirmedMode);
      updateConfig({ cacheDeleteMode: confirmedMode });
      const modeDesc =
        newMode === 'rsync'
          ? t('management.cache.deleteModes.rsync')
          : newMode === 'full'
            ? t('management.cache.deleteModes.removeAll')
            : t('management.cache.deleteModes.preserve');
      onSuccess?.(t('management.cache.deleteModeSet', { mode: modeDesc }));
    } catch (err: unknown) {
      console.error('Failed to update delete mode:', err);
      onError?.(getErrorMessage(err) || t('management.cache.errors.updateDeleteMode'));
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
      const result = clearingDatasource
        ? await ApiService.clearDatasourceCache(clearingDatasource)
        : await ApiService.clearAllCache();
      // Wait-queue model: queued/deduplicated responses must not seed a running card -
      // the OperationWaiting event (or the already-visible card) owns the UI.
      if (result.operationId && !result.queued && !result.alreadyRunning) {
        addNotification(
          buildSeededRunningNotification(
            'cache_clearing',
            result.operationId,
            t('signalr.cacheClear.starting')
          )
        );
      }
    } catch (err: unknown) {
      onError?.(
        t('management.cache.errors.startCacheClearing', {
          error: getErrorMessage(err) || t('common.unknownError')
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
            cachePath: config.cachePath,
            logsPath: config.logsPath,
            cacheWritable: config.cacheWritable,
            logsWritable: config.logsWritable,
            enabled: true,
            nginxReopenAvailable: false
          }
        ];
  const directoryNoticeConditions = {
    cacheWrite: true,
    cacheRead: false,
    logsWrite: false,
    nginx: false
  };
  const directoryNoticeLiveState = {
    cacheReadOnly,
    logsReadOnly,
    cacheExist,
    logsExist,
    checkingPermissions,
    nginxReopenGate: { available: true, messageKey: null }
  };
  const directoryNotice = resolveCardNotice(directoryNoticeConditions, directoryNoticeLiveState);
  const diskActionBlocked = isCardDiskActionBlocked(
    directoryNoticeConditions,
    directoryNoticeLiveState
  );
  const hasMultipleDatasources = datasources.length > 1;

  // Header actions
  const headerActions = (
    <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
      <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
        {(close) => (
          <>
            <ActionMenuItem
              icon={<RefreshCw className="w-3.5 h-3.5" />}
              disabled={cacheSizeLoading || isCacheSizeScanRunning}
              onClick={() => {
                handleRefreshCacheSize();
                close();
              }}
            >
              {t('common.refresh')}
            </ActionMenuItem>
            {hasMultipleDatasources && (
              <>
                <ActionMenuDivider />
                <ActionMenuDangerItem
                  icon={<Trash2 className="w-3.5 h-3.5" />}
                  disabled={
                    actionLoading ||
                    isCacheClearActive ||
                    mockMode ||
                    authMode !== 'authenticated' ||
                    diskActionBlocked ||
                    isClearAllRunning ||
                    checkingPermissions
                  }
                  onClick={() => {
                    handleClearCache(null);
                    close();
                  }}
                >
                  {isClearAllRunning ? t('common.clearing') : t('common.clearAll')}
                </ActionMenuDangerItem>
              </>
            )}
          </>
        )}
      </SectionActionsMenu>
    </div>
  );

  return (
    <>
      <AccordionSection
        title={t('management.cache.title')}
        description={t('management.cache.summary')}
        icon={Server}
        iconColor="var(--theme-icon-green)"
        isExpanded={sectionExpanded}
        onToggle={() => setSectionExpanded((prev) => !prev)}
        badge={headerActions}
      >
        <div className="space-y-3">
          <CardDirectoryNotice notice={directoryNotice} />

          <>
            {/* Cache Size Info */}
            <div className="space-y-3">
              <p className="mgmt-subhead caps-label">{t('management.cache.cacheSize')}</p>

              {cacheSizeError ? (
                <Alert color="red">
                  <p className="font-medium">
                    {t('management.cache.cacheSizeError', 'Cache Size Error')}
                  </p>
                  <p className="text-sm mt-1">{cacheSizeError}</p>
                </Alert>
              ) : cacheSizeLoading && !cacheSize ? (
                <div className="flex items-center gap-2 text-xs text-themed-muted">
                  <LoadingSpinner inline size="xs" />
                  <span>{t('management.cache.calculatingSize')}</span>
                </div>
              ) : cacheSize ? (
                <div className="mgmt-stat-grid">
                  <div className="mgmt-stat">
                    <p className="mgmt-stat__label caps-label caps-label--sm">
                      {t('management.cache.cacheSize')}
                    </p>
                    <p className="mgmt-stat__value">{formatBytes(cacheSize.totalBytes)}</p>
                  </div>
                  <div className="mgmt-stat">
                    <p className="mgmt-stat__label caps-label caps-label--sm">
                      {t('management.cache.files')}
                    </p>
                    <p className="mgmt-stat__value">{formatCount(cacheSize.totalFiles)}</p>
                  </div>
                  <div className="mgmt-stat">
                    <p className="mgmt-stat__label caps-label caps-label--sm">
                      {t('management.cache.directories')}
                    </p>
                    <p className="mgmt-stat__value">{formatCount(cacheSize.hexDirectories)}</p>
                  </div>
                  {getEstimatedTime() && (
                    <div className="mgmt-stat">
                      <p className="mgmt-stat__label caps-label caps-label--sm">
                        {t('management.cache.estDeletionTime')}
                      </p>
                      <p className="mgmt-stat__value">{getEstimatedTime()}</p>
                    </div>
                  )}
                  <div className="mgmt-stat">
                    <p className="mgmt-stat__label caps-label caps-label--sm">
                      {cacheSize.isCached
                        ? t('management.cache.cachedScan', 'Cached scan')
                        : t('management.cache.freshScan', 'Fresh scan')}
                    </p>
                    <p className="mgmt-stat__value">{formatScanTime(cacheSize.timestamp)}</p>
                  </div>
                </div>
              ) : (
                <p className="py-6 text-sm text-themed-muted text-center">
                  {t('management.cache.clickRefreshToCalculate')}
                </p>
              )}
            </div>

            {/* Configuration Options */}
            <div className="mgmt-panel">
              {/* Delete Mode Configuration */}
              <div>
                <p className="mgmt-subhead caps-label">{t('management.cache.deletionMethod')}</p>
                <p className="text-xs text-themed-muted mt-1">
                  {deleteMode === 'rsync'
                    ? t('management.cache.deletionMethods.rsyncDesc')
                    : deleteMode === 'full'
                      ? t('management.cache.deletionMethods.fullDesc')
                      : t('management.cache.deletionMethods.preserveDesc')}
                </p>
              </div>
              <div className="mgmt-segment-row">
                <Tooltip
                  content={cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined}
                  position="top"
                  className="flex-1 basis-0"
                >
                  <Button
                    size="sm"
                    className="w-full"
                    variant={deleteMode === 'preserve' ? 'filled' : 'default'}
                    color={deleteMode === 'preserve' ? 'blue' : undefined}
                    onClick={() => handleDeleteModeChange('preserve')}
                    awaitPermissions
                    loading={deleteModeLoading}
                    disabled={
                      mockMode ||
                      isAnyRemovalRunning ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                  >
                    {t('management.cache.deleteModes.preserve')}
                  </Button>
                </Tooltip>
                <Tooltip
                  content={cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined}
                  position="top"
                  className="flex-1 basis-0"
                >
                  <Button
                    size="sm"
                    className="w-full"
                    variant={deleteMode === 'full' ? 'filled' : 'default'}
                    color={deleteMode === 'full' ? 'green' : undefined}
                    onClick={() => handleDeleteModeChange('full')}
                    awaitPermissions
                    loading={deleteModeLoading}
                    disabled={
                      mockMode ||
                      isAnyRemovalRunning ||
                      authMode !== 'authenticated' ||
                      cacheReadOnly
                    }
                  >
                    {t('management.cache.deleteModes.removeAll')}
                  </Button>
                </Tooltip>
                {rsyncAvailable && (
                  <Tooltip
                    content={
                      cacheReadOnly ? t('management.cache.alerts.readOnly.title') : undefined
                    }
                    position="top"
                    className="flex-1 basis-0"
                  >
                    <Button
                      size="sm"
                      className="w-full"
                      variant={deleteMode === 'rsync' ? 'filled' : 'default'}
                      color={deleteMode === 'rsync' ? 'purple' : undefined}
                      onClick={() => handleDeleteModeChange('rsync')}
                      awaitPermissions
                      loading={deleteModeLoading}
                      disabled={
                        mockMode ||
                        isAnyRemovalRunning ||
                        authMode !== 'authenticated' ||
                        cacheReadOnly
                      }
                    >
                      {t('management.cache.deleteModes.rsync')}
                    </Button>
                  </Tooltip>
                )}
              </div>
            </div>

            {/* Datasource list */}
            <div className="space-y-3">
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
                  <div className="pt-3 flex justify-end">
                    <Tooltip
                      content={
                        !ds.cacheWritable
                          ? t('management.cache.alerts.readOnly.title')
                          : !isCacheClearActive && (isAnyRemovalRunning || isCacheSizeScanRunning)
                            ? t('common.notifications.willQueueBehindCurrent')
                            : t('management.cache.clearDatasourceCache', {
                                datasource: ds.name
                              })
                      }
                      position="top"
                    >
                      <Button
                        variant="filled"
                        color="red"
                        size="sm"
                        onClick={(e) => {
                          e.stopPropagation();
                          handleClearCache(ds.name);
                        }}
                        awaitPermissions
                        loading={isCacheClearing && clearingDatasource === ds.name}
                        disabled={
                          actionLoading ||
                          isCacheClearActive ||
                          mockMode ||
                          authMode !== 'authenticated' ||
                          diskActionBlocked ||
                          !ds.cacheWritable
                        }
                      >
                        {isCacheClearing && clearingDatasource === ds.name
                          ? t('common.clearing')
                          : t('management.cache.clearCache')}
                      </Button>
                    </Tooltip>
                  </div>
                </DatasourceListItem>
              ))}

              <div className="border-t border-themed-secondary pt-3 mt-3">
                <p className="text-xs text-themed-muted flex items-center gap-1.5">
                  <AlertTriangle className="w-3.5 h-3.5 text-themed-accent flex-shrink-0" />
                  <span>{t('management.cache.clearingCacheDeletes')}</span>
                </p>
              </div>
            </div>
          </>
        </div>
      </AccordionSection>

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
            <div className="space-y-1.5 p-3 rounded-lg bg-[var(--theme-bg-tertiary-muted)]">
              {datasources.map((ds) => (
                <div key={ds.name} className="flex items-center gap-2 text-xs">
                  <FolderOpen className="w-3.5 h-3.5 text-themed-muted flex-shrink-0" />
                  <span className="font-medium text-themed-primary">{ds.name}:</span>
                  <Tooltip content={ds.cachePath} position="top" className="flex min-w-0">
                    <code className="text-themed-secondary truncate">{ds.cachePath}</code>
                  </Tooltip>
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
