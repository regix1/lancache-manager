import React, { useState, useEffect, useCallback } from 'react';
import { useOptimisticPending } from '@/hooks/useOptimisticPending';
import { useSelectionSet } from '@/hooks/useSelectionSet';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, RefreshCw, Search, Trash2 } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useDockerSocket } from '@contexts/useDockerSocket';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useNotifications } from '@contexts/notifications';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type {
  CorruptionRemovalCompleteEvent,
  CorruptionDetectionCompleteEvent,
  CorruptionDetailsProgressEvent
} from '@contexts/SignalRContext/types';
import { Card } from '@components/ui/Card';
import { AccordionSection } from '@components/ui/AccordionSection';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { showPermissionBlock } from '@utils/permissionUi';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { getErrorMessage } from '@utils/error';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuItem, ActionMenuDangerItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import { formatCount } from '@utils/formatters';
import { LoadingState, ReadOnlyBadge } from '@components/ui/ManagerCard';
import Badge from '@components/ui/Badge';
import { useFormattedDateTime } from '@/hooks/useFormattedDateTime';
import { useManagerLoading } from '@/hooks/useManagerLoading';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import type { CorruptedChunkDetail } from '@/types';

interface CorruptionManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
}

const CorruptionManager: React.FC<CorruptionManagerProps> = ({ authMode, mockMode, onError }) => {
  const { t } = useTranslation();
  const { notifications, addNotification, isAnyRemovalRunning } = useNotifications();
  const { notifyError } = useErrorHandler();
  const { on, off } = useSignalR();
  const { isDockerAvailable } = useDockerSocket();
  const { logsReadOnly, cacheReadOnly, logsExist, cacheExist, checkingPermissions } =
    useDirectoryPermissionsContext();

  // Derive corruption detection scan state from notifications (standardized pattern like GameCacheDetector)
  const isScanningFromNotification = useOperationBusy({ types: ['corruption_detection'] });

  // Track local starting state for immediate UI feedback before SignalR events arrive
  const [isStartingScan, setIsStartingScan] = useState(false);

  // Combined scanning state: either notification says running OR we're in starting phase
  const isScanning = isScanningFromNotification || isStartingScan;

  // State
  const [corruptionSummary, setCorruptionSummary] = useState<Record<string, number>>({});
  const [pendingCorruptionRemoval, setPendingCorruptionRemoval] = useState<string | null>(null);
  const [expandedCorruptionService, setExpandedCorruptionService] = useState<string | null>(null);
  const [corruptionDetails, setCorruptionDetails] = useState<
    Record<string, CorruptedChunkDetail[]>
  >({});
  const [loadingDetailsServices, setLoadingDetailsServices] = useState<Set<string>>(new Set());
  const [detailsProgress, setDetailsProgress] = useState<Record<string, number>>({});
  const { isLoading, isRefreshing, hasInitiallyLoaded, beginLoad, markLoaded, markFailed } =
    useManagerLoading();
  const {
    isPending: isCorruptionRemovalPending,
    anyPending: anyCorruptionRemovalPending,
    markStarting: markCorruptionRemovalStarting,
    clearPending: clearCorruptionRemovalPending,
    clearOnNotification: clearCorruptionRemovalOnNotification
  } = useOptimisticPending<string>();
  const [pendingRemoveAll, setPendingRemoveAll] = useState(false);
  const {
    anyPending: startingRemoveAll,
    markStarting: markRemoveAllStarting,
    clearPending: clearRemoveAllPending,
    clearOnNotification: clearRemoveAllOnNotification
  } = useOptimisticPending<'removeAll'>();

  // Batch multi-select removal: client-only selection over service tags, plus a confirm
  // modal and an optimistic "starting" flag mirroring the Remove All flow (both hit the
  // same endpoint and share the single corruption_removal notification lifecycle).
  const selection = useSelectionSet<string>();
  const [pendingRemoveSelected, setPendingRemoveSelected] = useState(false);
  const {
    anyPending: startingRemoveSelected,
    markStarting: markRemoveSelectedStarting,
    clearPending: clearRemoveSelectedPending,
    clearOnNotification: clearRemoveSelectedOnNotification
  } = useOptimisticPending<'removeSelected'>();

  // Directory permissions are provided by StorageSection to avoid duplicate API calls.
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [hasCachedResults, setHasCachedResults] = useState(false);
  const [missThreshold, setMissThreshold] = useState(3);
  const [detectionMode, setDetectionMode] = useState('cache_and_logs');
  const [sectionExpanded, setSectionExpanded] = useState(() => {
    const saved = localStorage.getItem('management-corruption-expanded');
    return saved !== null ? saved === 'true' : false;
  });

  // Derive legacy boolean from detection mode for backward-compatible API calls
  const compareToCacheLogs = detectionMode === 'cache_and_logs';
  const isRedownloadMode = detectionMode === 'redownload';

  useEffect(() => {
    localStorage.setItem('management-corruption-expanded', String(sectionExpanded));
  }, [sectionExpanded]);

  const detectionModeOptions = [
    {
      value: 'logs_only',
      label: t('management.corruption.detectionModeLogsOnly'),
      shortLabel: t('management.corruption.detectionModeLogsOnlyShort'),
      description: t('management.corruption.detectionModeLogsOnlyDesc', {
        threshold: missThreshold
      })
    },
    {
      value: 'cache_and_logs',
      label: t('management.corruption.detectionModeCacheLogs'),
      shortLabel: t('management.corruption.detectionModeCacheLogsShort'),
      description: t('management.corruption.detectionModeCacheLogsDesc', {
        threshold: missThreshold
      })
    },
    {
      value: 'redownload',
      label: t('management.corruption.detectionModeRedownload'),
      shortLabel: t('management.corruption.detectionModeRedownloadShort'),
      description: t('management.corruption.detectionModeRedownloadDesc', {
        threshold: missThreshold
      })
    }
  ];

  const thresholdOptions = [
    {
      value: '3',
      label: t('management.corruption.sensitivityHigh'),
      shortLabel: t('management.corruption.sensitivityHighShort'),
      description: t('management.corruption.sensitivityHighDesc')
    },
    {
      value: '5',
      label: t('management.corruption.sensitivityMedium'),
      shortLabel: t('management.corruption.sensitivityMediumShort'),
      description: t('management.corruption.sensitivityMediumDesc')
    },
    {
      value: '10',
      label: t('management.corruption.sensitivityLow'),
      shortLabel: t('management.corruption.sensitivityLowShort'),
      description: t('management.corruption.sensitivityLowDesc')
    }
  ];

  const formattedLastDetection = useFormattedDateTime(lastDetectionTime);

  // Derive active corruption removal from notifications
  const activeCorruptionRemovalNotification = notifications.find(
    (n) => n.type === 'corruption_removal' && n.status === 'running'
  );
  const removingCorruption =
    (activeCorruptionRemovalNotification?.details?.service as string | null) ?? null;
  // Own-card gate: any running OR queued corruption removal disables every
  // corruption remove button (per-service rows and Remove All gate together).
  // Other cards' removals must NOT disable them - clicking enqueues.
  const isCorruptionRemovalActive = useOperationBusy({
    types: ['corruption_removal'],
    status: ['running', 'waiting']
  });

  // Load cached data from database
  const loadCachedData = useCallback(
    async (showNotification = false) => {
      beginLoad(showNotification);
      try {
        const cached = await ApiService.getCachedCorruptionDetection();
        if (cached.hasCachedResults && cached.corruptionCounts) {
          setCorruptionSummary(cached.corruptionCounts);
          setLastDetectionTime(cached.lastDetectionTime || null);
          setHasCachedResults(true);

          // Show notification only when explicitly requested or once per session
          const sessionKey = 'corruptionManager_loadedNotificationShown';
          const alreadyShownThisSession = sessionStorage.getItem(sessionKey) === 'true';
          const totalCorrupted = Object.values(cached.corruptionCounts).reduce((a, b) => a + b, 0);
          const serviceCount = Object.keys(cached.corruptionCounts).filter(
            (k) => cached.corruptionCounts![k] > 0
          ).length;

          if (showNotification || !alreadyShownThisSession) {
            if (totalCorrupted > 0) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.corruption.notifications.loadedResults', {
                  chunks: formatCount(totalCorrupted),
                  services: serviceCount
                }),
                details: { notificationType: 'info' }
              });
            } else if (showNotification) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.corruption.notifications.noCorruptedInPrevious'),
                details: { notificationType: 'success' }
              });
            }
            sessionStorage.setItem(sessionKey, 'true');
          }
        } else {
          setCorruptionSummary({});
          setLastDetectionTime(null);
          setHasCachedResults(false);

          if (showNotification) {
            addNotification({
              type: 'generic',
              status: 'completed',
              message: t('management.corruption.notifications.noPreviousResults'),
              details: { notificationType: 'info' }
            });
          }
        }
        markLoaded();
      } catch (err: unknown) {
        notifyError(
          t('management.corruption.errors.loadCachedData', 'Failed to load corruption data'),
          err,
          {
            logLabel: 'Failed to load cached corruption data'
          }
        );
        markFailed();
      }
    },
    [addNotification, t, beginLoad, markLoaded, markFailed, notifyError]
  );

  // Start a background scan
  const startScan = useCallback(async () => {
    if (isScanning || mockMode) return;

    // Note: NotificationsContext automatically replaces notifications with the same ID
    // when a new operation starts, so manual dismissal is not needed

    setIsStartingScan(true);
    setCorruptionSummary({});
    setLastDetectionTime(null);
    setHasCachedResults(false);

    try {
      // Start background detection - SignalR will send CorruptionDetectionStarted event
      const result = await ApiService.startCorruptionDetection(
        missThreshold,
        compareToCacheLogs,
        detectionMode
      );
      // Wait-queue model: queued/deduplicated responses must not seed a running card -
      // the OperationWaiting event (purple waiting card) owns the UI until promotion.
      if (result.operationId && !result.queued && !result.alreadyRunning) {
        addNotification(
          buildSeededRunningNotification(
            'corruption_detection',
            result.operationId,
            t('signalr.corruptionDetect.starting')
          )
        );
      }
    } catch (err: unknown) {
      notifyError(
        t('management.corruption.errors.startScan', 'Failed to start corruption scan'),
        err,
        {
          logLabel: 'Failed to start corruption scan'
        }
      );
      setIsStartingScan(false);
    }
  }, [
    isScanning,
    mockMode,
    missThreshold,
    compareToCacheLogs,
    detectionMode,
    addNotification,
    t,
    notifyError
  ]);

  // Listen for corruption detection completion via SignalR directly (same rationale as the
  // removal handler below). The event fires exactly once per bulk scan, so no per-operation
  // dedup bookkeeping is needed, and unlike a local "did this page click Scan" flag it still
  // refreshes after an earlier attempt was cancelled or when the scan was started elsewhere.
  useEffect(() => {
    const handleDetectionComplete = (event: CorruptionDetectionCompleteEvent) => {
      setIsStartingScan(false);

      if (!event.success) {
        // Failure/cancel is surfaced in the notification bar, no inline error needed
        return;
      }

      beginLoad(true);

      // Load fresh results from the database (backend already saved them)
      const loadResults = async () => {
        try {
          const result = await ApiService.getCachedCorruptionDetection();
          if (result.hasCachedResults && result.corruptionCounts) {
            setCorruptionSummary(result.corruptionCounts);
            setLastDetectionTime(result.lastDetectionTime || null);
            setHasCachedResults(true);
          } else {
            // Scan completed but found zero corruption
            // Still mark as "has results" so UI shows "No corrupted chunks detected"
            setCorruptionSummary({});
            setLastDetectionTime(new Date().toISOString());
            setHasCachedResults(true);
          }
        } catch (err) {
          // Background auto-refresh after a SignalR-confirmed scan; the manual Load action
          // remains available, so a transient reload failure here is explicit background noise.
          notifyError(
            t('management.corruption.errors.loadCachedData', 'Failed to load corruption data'),
            err,
            { silent: true, logLabel: '[CorruptionManager] Failed to load detection results' }
          );
        } finally {
          markLoaded();
        }
      };
      loadResults();
    };

    on('CorruptionDetectionComplete', handleDetectionComplete);
    return () => {
      off('CorruptionDetectionComplete', handleDetectionComplete);
    };
  }, [on, off, beginLoad, markLoaded, notifyError, t]);

  // Listen for corruption removal completion via SignalR directly. Subscribing to the
  // raw event (instead of deriving from notifications.filter(status === 'completed'))
  // is required for the "Remove All" path: services share a single notification slot,
  // so a fast Started-for-next-service replaces the previous Completed snapshot before
  // React renders, and the notifications-based useEffect would miss it. SignalR
  // handlers fire synchronously per event and can't be coalesced.
  useEffect(() => {
    const handleCorruptionRemovalComplete = (event: CorruptionRemovalCompleteEvent) => {
      if (!event.success) return;
      const serviceName = event.service;
      if (!serviceName) return;

      // A bulk removal (all services OR a selected subset) emits exactly ONE terminal
      // event with service === 'all' (per-service completes are suppressed by the
      // backend). Reload from the database rather than blindly clearing, so a SUBSET
      // removal keeps showing the services that were not selected; a full Remove All
      // simply comes back empty. Single-service removals still arrive per service below.
      if (serviceName === 'all') {
        setExpandedCorruptionService(null);
        setCorruptionDetails({});
        void (async () => {
          try {
            const result = await ApiService.getCachedCorruptionDetection();
            if (result.hasCachedResults && result.corruptionCounts) {
              setCorruptionSummary(result.corruptionCounts);
              setLastDetectionTime(result.lastDetectionTime || null);
            } else {
              setCorruptionSummary({});
            }
          } catch (err) {
            // Background auto-refresh after a SignalR-confirmed removal; already falls back to
            // an empty summary, so this is explicit background noise rather than a blocking error.
            notifyError(
              t('management.corruption.errors.loadCachedData', 'Failed to load corruption data'),
              err,
              {
                silent: true,
                logLabel: '[CorruptionManager] Failed to reload after bulk corruption removal'
              }
            );
            setCorruptionSummary({});
          }
        })();
        return;
      }

      setCorruptionSummary((prev) => {
        if (!(serviceName in prev)) return prev;
        const updated = { ...prev };
        delete updated[serviceName];
        return updated;
      });

      setExpandedCorruptionService((prev) => (prev === serviceName ? null : prev));

      setCorruptionDetails((prev) => {
        if (!(serviceName in prev)) return prev;
        const updated = { ...prev };
        delete updated[serviceName];
        return updated;
      });
    };

    on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    return () => {
      off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    };
  }, [on, off, notifyError, t]);

  // Live percent for the per-service "view details" fetch. Deliberately a separate event from
  // the bulk scan's CorruptionDetectionProgress so this never surfaces as a global notification.
  useEffect(() => {
    const handleDetailsProgress = (event: CorruptionDetailsProgressEvent) => {
      setDetailsProgress((prev) => ({ ...prev, [event.service]: event.percentComplete }));
    };

    on('CorruptionDetailsProgress', handleDetailsProgress);
    return () => {
      off('CorruptionDetailsProgress', handleDetailsProgress);
    };
  }, [on, off]);

  // Clear optimistic pending state when matching running SignalR notifications arrive
  useEffect(() => {
    if (anyCorruptionRemovalPending) {
      const runningRemoval = notifications.find(
        (n) => n.type === 'corruption_removal' && n.status === 'running'
      );
      if (runningRemoval) {
        const service = (runningRemoval.details?.service as string | undefined) ?? '';
        clearCorruptionRemovalOnNotification(
          service,
          notifications,
          (n, k) =>
            n.type === 'corruption_removal' && n.status === 'running' && n.details?.service === k
        );
      }
    }
    if (startingRemoveAll) {
      clearRemoveAllOnNotification(
        'removeAll',
        notifications,
        (n) => n.type === 'corruption_removal' && n.status === 'running'
      );
    }
    if (startingRemoveSelected) {
      clearRemoveSelectedOnNotification(
        'removeSelected',
        notifications,
        (n) => n.type === 'corruption_removal' && n.status === 'running'
      );
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [notifications]);

  // Prune selection when services disappear from the summary (e.g. after removal or a
  // fresh scan) so a stale key never survives a reload; also serves as the post-batch
  // clear for services that were removed. Keys are the corruption service tags.
  useEffect(() => {
    const validKeys = new Set(
      Object.keys(corruptionSummary).filter((k) => corruptionSummary[k] > 0)
    );
    const stale = [...selection.selected].filter((key) => !validKeys.has(key));
    if (stale.length > 0) {
      selection.setMany(stale, false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [corruptionSummary]);

  // Initial load - load cached data without auto-scanning (matches GameCacheDetector pattern)
  // Note: Directory permissions are now handled by useDirectoryPermissions hook
  useEffect(() => {
    if (!hasInitiallyLoaded) {
      // Only load cached data - don't auto-start scan
      loadCachedData();
    }
  }, [hasInitiallyLoaded, loadCachedData]);

  const handleRemoveCorruption = (service: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }
    setPendingCorruptionRemoval(service);
  };

  const confirmRemoveCorruption = async () => {
    if (!pendingCorruptionRemoval || authMode !== 'authenticated') return;

    const service = pendingCorruptionRemoval;
    setPendingCorruptionRemoval(null);
    markCorruptionRemovalStarting(service);

    try {
      const result = await ApiService.removeCorruptedChunks(
        service,
        missThreshold,
        compareToCacheLogs,
        detectionMode
      );
      // Wait-queue model: queued/deduplicated responses must not seed a running card -
      // the OperationWaiting event (or the already-visible card) owns the UI.
      if (result.operationId && !result.queued && !result.alreadyRunning) {
        addNotification(
          buildSeededRunningNotification(
            'corruption_removal',
            result.operationId,
            t('signalr.corruptionRemove.starting', { service: getServiceDisplayName(service) }),
            // Raw tag: notification matching and the backend operate on the service tag.
            { service }
          )
        );
      }
    } catch (err: unknown) {
      console.error('Removal failed:', err);
      onError?.(
        getErrorMessage(err) ||
          t('management.corruption.errors.removeCorrupted', {
            service: getServiceDisplayName(service)
          })
      );
      clearCorruptionRemovalPending(service);
    }
  };

  const handleRemoveAll = () => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }
    setPendingRemoveAll(true);
  };

  const confirmRemoveAll = async () => {
    if (authMode !== 'authenticated') return;

    setPendingRemoveAll(false);
    markRemoveAllStarting('removeAll');

    try {
      await ApiService.removeAllCorruptedChunks(missThreshold, compareToCacheLogs, detectionMode);
      // SignalR will handle progress; clearOnNotification (in notifications useEffect) will clear pending
    } catch (err: unknown) {
      console.error('Remove all corrupted failed:', err);
      onError?.(getErrorMessage(err) || t('management.corruption.errors.removeAllCorrupted'));
      clearRemoveAllPending('removeAll');
    }
  };

  const handleRemoveSelected = () => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }
    if (selection.count === 0) return;
    setPendingRemoveSelected(true);
  };

  const confirmRemoveSelected = async () => {
    if (authMode !== 'authenticated') return;

    const selectedServices = [...selection.selected];
    setPendingRemoveSelected(false);
    if (selectedServices.length === 0) return;
    markRemoveSelectedStarting('removeSelected');

    try {
      // Reuses the bulk endpoint with a subset filter; the backend emits the same single
      // Service="all" aggregate terminal, so notification handling is unchanged.
      const result = await ApiService.removeAllCorruptedChunks(
        missThreshold,
        compareToCacheLogs,
        detectionMode,
        selectedServices
      );
      // No-op response (e.g. the selected services no longer have corruption data): no
      // SignalR notification will arrive to clear the gate, so release it now instead of
      // waiting for the ~5s safety timeout.
      if (!result.started) {
        clearRemoveSelectedPending('removeSelected');
      }
      // Selection prunes to the remaining services once the reload lands (pruning effect).
    } catch (err: unknown) {
      console.error('Remove selected corrupted failed:', err);
      onError?.(getErrorMessage(err) || t('management.corruption.errors.removeAllCorrupted'));
      clearRemoveSelectedPending('removeSelected');
    }
  };

  const toggleCorruptionDetails = async (service: string) => {
    if (expandedCorruptionService === service) {
      setExpandedCorruptionService(null);
      return;
    }

    setExpandedCorruptionService(service);

    if (!corruptionDetails[service] && !loadingDetailsServices.has(service)) {
      setLoadingDetailsServices((prev) => new Set(prev).add(service));
      try {
        const details = await ApiService.getCorruptionDetails(
          service,
          false,
          missThreshold,
          compareToCacheLogs,
          detectionMode
        );
        setCorruptionDetails((prev) => ({ ...prev, [service]: details }));
      } catch (err: unknown) {
        onError?.(
          getErrorMessage(err) ||
            t('management.corruption.errors.loadDetails', {
              service: getServiceDisplayName(service)
            })
        );
        setExpandedCorruptionService((prev) => (prev === service ? null : prev));
      } finally {
        setLoadingDetailsServices((prev) => {
          const next = new Set(prev);
          next.delete(service);
          return next;
        });
        setDetailsProgress((prev) => {
          if (!(service in prev)) return prev;
          const next = { ...prev };
          delete next[service];
          return next;
        });
      }
    }
  };

  const corruptionList = Object.entries(corruptionSummary)
    .filter(([_, count]) => count > 0)
    .sort((a, b) => b[1] - a[1]);

  // Batch selection derives from the CURRENTLY VISIBLE list, matching Remove All semantics.
  const visibleServiceKeys = corruptionList.map(([service]) => service);
  const allVisibleSelected = selection.allSelected(visibleServiceKeys);
  // Shared busy gate for every batch control (same conditions Remove All disables on, plus
  // the optimistic "starting" flags so a second click can't fire mid-start).
  const batchGateActive =
    (isLoading && !hasInitiallyLoaded) ||
    mockMode ||
    anyCorruptionRemovalPending ||
    isCorruptionRemovalActive ||
    startingRemoveAll ||
    startingRemoveSelected ||
    authMode !== 'authenticated' ||
    logsReadOnly ||
    cacheReadOnly ||
    !isDockerAvailable;

  const isReadOnly = logsReadOnly || cacheReadOnly;
  const directoryMissing = !logsExist || !cacheExist;
  const hasPermissionIssue = isReadOnly || directoryMissing;
  const showReadOnlyPlaceholder = showPermissionBlock(
    checkingPermissions,
    hasPermissionIssue || !isDockerAvailable
  );

  // Cancel is handled by UniversalNotificationBar via CANCEL_CONFIGS
  const controlSelectors = (
    <div className="grid grid-cols-1 gap-3 sm:flex sm:flex-wrap sm:items-end">
      <div className="w-full sm:w-auto">
        <EnhancedDropdown
          variant="button"
          options={detectionModeOptions}
          value={detectionMode}
          onChange={(val: string) => setDetectionMode(val)}
          disabled={isScanning || isAnyRemovalRunning}
          dropdownWidth="w-72"
          alignRight={true}
          dropdownTitle={t('management.corruption.detectionModeTitle')}
          compactMode={true}
        />
      </div>
      <div className="w-full sm:w-auto">
        <EnhancedDropdown
          variant="button"
          options={thresholdOptions}
          value={String(missThreshold)}
          onChange={(val: string) => setMissThreshold(Number(val))}
          disabled={isScanning || isAnyRemovalRunning}
          dropdownWidth="w-72"
          alignRight={true}
          dropdownTitle={t('management.corruption.sensitivityTitle')}
          compactMode={true}
        />
      </div>
    </div>
  );

  // Header action row (AccordionSection badge): the warning count badge stays
  // visible outside the menu (a status readout, not an action); every action
  // button now lives in one overflow menu, reachable while the section is
  // collapsed like every other Storage section.
  const headerActions = (
    <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
      {corruptionList.length > 0 && (
        <Badge variant="warning">{corruptionList.reduce((sum, [, count]) => sum + count, 0)}</Badge>
      )}
      {selection.count > 0 && (
        <Badge
          variant="neutral"
          className="rounded-full min-w-[1.25rem] justify-center px-1.5 tabular-nums"
        >
          {selection.count}
        </Badge>
      )}
      <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
        {(close) => (
          <>
            <ActionMenuItem
              icon={<RefreshCw className="w-3.5 h-3.5" />}
              disabled={isRefreshing || isScanning || isAnyRemovalRunning}
              onClick={() => {
                loadCachedData(true);
                close();
              }}
            >
              {t('common.load')}
            </ActionMenuItem>
            <ActionMenuItem
              icon={<Search className="w-3.5 h-3.5" />}
              disabled={isLoading || isScanning || isAnyRemovalRunning}
              onClick={() => {
                startScan();
                close();
              }}
            >
              {t('common.scan')}
            </ActionMenuItem>
            <ActionMenuDivider />
            <ActionMenuDangerItem
              icon={<Trash2 className="w-3.5 h-3.5" />}
              disabled={batchGateActive || selection.count === 0}
              onClick={() => {
                handleRemoveSelected();
                close();
              }}
            >
              {t('management.batchSelect.removeSelectedLabel', 'Remove Selected')}
            </ActionMenuDangerItem>
            <ActionMenuDangerItem
              icon={<Trash2 className="w-3.5 h-3.5" />}
              disabled={
                (isLoading && !hasInitiallyLoaded) ||
                corruptionList.length === 0 ||
                mockMode ||
                anyCorruptionRemovalPending ||
                isCorruptionRemovalActive ||
                authMode !== 'authenticated' ||
                logsReadOnly ||
                cacheReadOnly ||
                !isDockerAvailable
              }
              onClick={() => {
                handleRemoveAll();
                close();
              }}
            >
              {startingRemoveAll
                ? t('management.corruption.removing')
                : t('management.corruption.removeAllServices')}
            </ActionMenuDangerItem>
          </>
        )}
      </SectionActionsMenu>
    </div>
  );

  return (
    <>
      <Card>
        <div className="space-y-3">
          <AccordionSection
            title={t('management.corruption.title')}
            icon={AlertTriangle}
            iconColor="var(--theme-icon-yellow)"
            isExpanded={sectionExpanded}
            onToggle={() => setSectionExpanded((prev) => !prev)}
            badge={headerActions}
          >
            <div className="space-y-3">
              {controlSelectors}
              {/* Previous Results Badge */}
              {hasCachedResults && lastDetectionTime && !isScanning && !isLoading && (
                <Alert color="blue">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium">
                      {t('common.resultsFromPreviousScan')}
                    </span>
                    <span className="text-xs text-themed-muted">{formattedLastDetection}</span>
                  </div>
                </Alert>
              )}

              {/* Scanning Status */}
              {isScanning && <LoadingState message={t('management.corruption.scanningMessage')} />}

              {/* Directory Missing Warning */}
              {directoryMissing && (
                <Alert color="red">
                  <div>
                    <p className="font-medium">
                      {!logsExist && !cacheExist
                        ? t(
                            'management.corruption.alerts.logsAndCacheMissing',
                            'Logs and cache files do not exist'
                          )
                        : !logsExist
                          ? t(
                              'management.corruption.alerts.logsMissing',
                              'Logs directory does not exist'
                            )
                          : t(
                              'management.corruption.alerts.cacheMissing',
                              'Cache directory does not exist'
                            )}
                    </p>
                    <p className="text-sm mt-1">
                      {t(
                        'management.corruption.alerts.directoryNotFound',
                        'The required directories were not found. Ensure they are mounted correctly in docker-compose.'
                      )}
                    </p>
                  </div>
                </Alert>
              )}

              {/* Read-Only Warning */}
              {isReadOnly && !directoryMissing && (
                <Alert color="orange">
                  <div>
                    <p className="font-medium">
                      {logsReadOnly && cacheReadOnly
                        ? t('management.corruption.alerts.logsAndCacheReadOnly')
                        : logsReadOnly
                          ? t('management.corruption.alerts.logsReadOnly')
                          : t('management.corruption.alerts.cacheReadOnly')}
                    </p>
                    <p className="text-sm mt-1">
                      {t('management.corruption.alerts.requiresWriteAccess')}{' '}
                      <code className="bg-themed-tertiary px-1 rounded">:ro</code>{' '}
                      {t('management.corruption.alerts.fromVolumeMounts')}
                    </p>
                  </div>
                </Alert>
              )}

              {/* Docker Socket Warning */}
              {!isDockerAvailable && !hasPermissionIssue && (
                <Alert color="orange">
                  <div className="min-w-0">
                    <p className="font-medium">
                      {t('management.corruption.alerts.dockerSocketUnavailable')}
                    </p>
                    <p className="text-sm mt-1">
                      {t('management.corruption.alerts.requiresNginxSignal')}
                    </p>
                    <p className="text-sm mt-2">
                      {t('management.logRemoval.alerts.dockerSocket.addVolumes')}
                    </p>
                    <code className="block bg-themed-tertiary px-2 py-1 rounded text-xs mt-1 break-all">
                      - /var/run/docker.sock:/var/run/docker.sock
                    </code>
                  </div>
                </Alert>
              )}

              {/* Content */}
              {showReadOnlyPlaceholder ? (
                <ReadOnlyBadge
                  message={
                    directoryMissing
                      ? t(
                          'management.corruption.directoryMissing',
                          'Required directories not found'
                        )
                      : isReadOnly
                        ? t('management.corruption.readOnly')
                        : t('management.corruption.dockerSocketRequired')
                  }
                />
              ) : (
                <>
                  {isLoading && !isScanning ? (
                    <LoadingState message={t('management.corruption.loadingCachedData')} />
                  ) : hasCachedResults && corruptionList.length > 0 ? (
                    <div className="space-y-3">
                      {/* Batch multi-select toolbar: select-all (visible) + selected count.
                          Remove Selected lives in the section header cluster (with Remove All);
                          wraps at 390px. */}
                      <div className="flex flex-wrap items-center gap-3">
                        <Checkbox
                          checked={allVisibleSelected}
                          onChange={() =>
                            selection.setMany(visibleServiceKeys, !allVisibleSelected)
                          }
                          disabled={batchGateActive || visibleServiceKeys.length === 0}
                          label={
                            allVisibleSelected
                              ? t('management.batchSelect.deselectAll')
                              : t('management.batchSelect.selectAll')
                          }
                        />
                        {selection.count > 0 && (
                          <span className="text-sm text-themed-muted">
                            {t('management.batchSelect.selectedCount', { count: selection.count })}
                          </span>
                        )}
                      </div>
                      {corruptionList.map(([service, count]) => (
                        <div key={`corruption-${service}`} className="flex items-start gap-2">
                          <Checkbox
                            checked={selection.isSelected(service)}
                            onChange={() => selection.toggle(service)}
                            disabled={
                              batchGateActive ||
                              removingCorruption === service ||
                              isCorruptionRemovalPending(service)
                            }
                            aria-label={t('management.batchSelect.selectItem', {
                              name: getServiceDisplayName(service)
                            })}
                            className="flex-shrink-0 mt-4"
                          />
                          <div className="flex-1 min-w-0">
                            <AccordionSection
                              title={getServiceDisplayName(service)}
                              count={count}
                              isExpanded={expandedCorruptionService === service}
                              onToggle={() => toggleCorruptionDetails(service)}
                            >
                              {loadingDetailsServices.has(service) ? (
                                <LoadingState
                                  message={t('management.corruption.loadingDetails')}
                                  submessage={
                                    detailsProgress[service] != null
                                      ? `${Math.round(detailsProgress[service])}%`
                                      : undefined
                                  }
                                />
                              ) : corruptionDetails[service] &&
                                corruptionDetails[service].length > 0 ? (
                                <div className="flex flex-col gap-3 max-h-96 overflow-y-auto">
                                  {corruptionDetails[service].map((chunk, idx) => (
                                    <div key={idx} className="p-3 bg-themed-tertiary rounded-lg">
                                      <div className="flex items-start gap-2">
                                        <div className="flex-1 min-w-0">
                                          <div className="mb-1">
                                            <Tooltip content={chunk.url}>
                                              <span className="text-sm font-medium text-themed-primary truncate block font-mono">
                                                {chunk.url}
                                              </span>
                                            </Tooltip>
                                          </div>
                                          <div className="flex items-center gap-3 text-xs text-themed-muted">
                                            <span>
                                              {isRedownloadMode
                                                ? t('management.corruption.redownloadCount')
                                                : t('management.corruption.missCount')}{' '}
                                              <strong className="text-themed-error">
                                                {chunk.miss_count || 0}
                                              </strong>
                                            </span>
                                            {chunk.cache_file_path && (
                                              <Tooltip content={chunk.cache_file_path}>
                                                <span className="truncate">
                                                  {t('management.corruption.cache')}{' '}
                                                  <code className="text-xs">
                                                    {chunk.cache_file_path.split('/').pop() ||
                                                      chunk.cache_file_path.split('\\').pop()}
                                                  </code>
                                                </span>
                                              </Tooltip>
                                            )}
                                          </div>
                                        </div>
                                      </div>
                                    </div>
                                  ))}
                                </div>
                              ) : (
                                <div className="text-center py-6">
                                  <p className="text-sm text-themed-muted">
                                    {t('management.corruption.noDetailsAvailable')}
                                  </p>
                                </div>
                              )}
                              <div className="flex justify-end pt-3 border-t border-themed-secondary mt-3">
                                <Tooltip content={t('management.corruption.deleteCorrupted')}>
                                  <Button
                                    onClick={() => handleRemoveCorruption(service)}
                                    awaitPermissions
                                    loading={
                                      removingCorruption === service ||
                                      isCorruptionRemovalPending(service)
                                    }
                                    disabled={
                                      mockMode ||
                                      anyCorruptionRemovalPending ||
                                      isCorruptionRemovalActive ||
                                      loadingDetailsServices.has(service) ||
                                      authMode !== 'authenticated' ||
                                      logsReadOnly ||
                                      cacheReadOnly ||
                                      !isDockerAvailable
                                    }
                                    variant="filled"
                                    color="red"
                                    size="sm"
                                  >
                                    {removingCorruption !== service &&
                                    !isCorruptionRemovalPending(service)
                                      ? t('management.corruption.removeAll')
                                      : t('management.corruption.removing')}
                                  </Button>
                                </Tooltip>
                              </div>
                            </AccordionSection>
                          </div>
                        </div>
                      ))}
                    </div>
                  ) : hasCachedResults && corruptionList.length === 0 ? (
                    <div className="text-center py-6">
                      <p className="text-sm text-themed-muted">
                        {t('management.corruption.emptyStates.noCorrupted.title')}
                      </p>
                      <p className="text-xs text-themed-muted">
                        {t('management.corruption.emptyStates.noCorrupted.subtitle')}
                      </p>
                    </div>
                  ) : !hasCachedResults && !isScanning && !isLoading ? (
                    <div className="text-center py-6">
                      <p className="text-sm text-themed-muted">
                        {t('management.corruption.emptyStates.noCachedData.title')}
                      </p>
                      <p className="text-xs text-themed-muted">
                        {t('management.corruption.emptyStates.noCachedData.subtitle')}
                      </p>
                    </div>
                  ) : null}
                </>
              )}
            </div>
          </AccordionSection>
        </div>
      </Card>

      {/* Remove All Corrupted Confirmation Modal */}
      <Modal
        opened={pendingRemoveAll}
        onClose={() => setPendingRemoveAll(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.corruption.modal.removeAllTitle')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.corruption.modal.confirmRemoveAll', {
              count: corruptionList.length
            })}
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.corruption.modal.willDelete')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>
                  <strong>{t('management.corruption.modal.cacheFilesLabel')}</strong>{' '}
                  {t('management.corruption.modal.cacheFilesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.logEntriesLabel')}</strong>{' '}
                  {t('management.corruption.modal.logEntriesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.databaseRecordsLabel')}</strong>{' '}
                  {t('management.corruption.modal.databaseRecordsDesc')}
                </li>
              </ul>
            </div>
          </Alert>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('management.cache.alerts.important')}</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.corruption.modal.cannotBeUndone')}</li>
                <li>{t('management.corruption.modal.mayTakeSeveralMinutes')}</li>
                <li>{t('management.corruption.modal.removeAllAcrossAllServices')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingRemoveAll(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="filled" color="red" onClick={confirmRemoveAll}>
              {t('management.corruption.modal.removeAllConfirm')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Remove Selected Corrupted Confirmation Modal */}
      <Modal
        opened={pendingRemoveSelected}
        onClose={() => setPendingRemoveSelected(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.batchSelect.confirmTitle')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.batchSelect.confirmBody', { count: selection.count })}
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.corruption.modal.willDelete')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>
                  <strong>{t('management.corruption.modal.cacheFilesLabel')}</strong>{' '}
                  {t('management.corruption.modal.cacheFilesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.logEntriesLabel')}</strong>{' '}
                  {t('management.corruption.modal.logEntriesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.databaseRecordsLabel')}</strong>{' '}
                  {t('management.corruption.modal.databaseRecordsDesc')}
                </li>
              </ul>
            </div>
          </Alert>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('management.cache.alerts.important')}</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.corruption.modal.cannotBeUndone')}</li>
                <li>{t('management.corruption.modal.mayTakeSeveralMinutes')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingRemoveSelected(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="filled" color="red" onClick={confirmRemoveSelected}>
              {t('management.batchSelect.removeSelected', { count: selection.count })}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Corruption Removal Confirmation Modal */}
      <Modal
        opened={pendingCorruptionRemoval !== null}
        onClose={() => setPendingCorruptionRemoval(null)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.corruption.modal.title')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.corruption.modal.confirmRemove', {
              service: pendingCorruptionRemoval
                ? getServiceDisplayName(pendingCorruptionRemoval)
                : undefined
            })}
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('management.corruption.modal.willDelete')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>
                  <strong>{t('management.corruption.modal.cacheFilesLabel')}</strong>{' '}
                  {t('management.corruption.modal.cacheFilesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.logEntriesLabel')}</strong>{' '}
                  {t('management.corruption.modal.logEntriesDesc')}
                </li>
                <li>
                  <strong>{t('management.corruption.modal.databaseRecordsLabel')}</strong>{' '}
                  {t('management.corruption.modal.databaseRecordsDesc')}
                </li>
              </ul>
            </div>
          </Alert>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('management.cache.alerts.important')}</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.corruption.modal.cannotBeUndone')}</li>
                <li>{t('management.corruption.modal.mayTakeSeveralMinutes')}</li>
                <li>
                  {t('management.corruption.modal.validFilesRemain', {
                    service: pendingCorruptionRemoval
                      ? getServiceDisplayName(pendingCorruptionRemoval)
                      : undefined
                  })}
                </li>
                <li>
                  {t('management.corruption.modal.removesApproximately', {
                    count: corruptionSummary[pendingCorruptionRemoval || ''] || 0
                  })}
                </li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingCorruptionRemoval(null)}>
              {t('common.cancel')}
            </Button>
            <Button variant="filled" color="red" onClick={confirmRemoveCorruption}>
              {t('management.corruption.modal.deleteCacheAndLogs')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CorruptionManager;
