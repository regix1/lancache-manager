import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useOptimisticPending } from '@/hooks/useOptimisticPending';
import { useSelectionSet } from '@/hooks/useSelectionSet';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, ChevronDown, ChevronUp, RefreshCw, Search, Trash2 } from 'lucide-react';
import '../managementSectionContent.css';
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
import { AccordionSection } from '@components/ui/AccordionSection';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
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
import CorruptionChunkList from './CorruptionChunkList';
import CorruptionRemovalWarning from './CorruptionRemovalWarning';
import { projectCorruptionCounts } from './corruptionCountProjection';
import type {
  CachedCorruptionDetectionResponse,
  CorruptedChunkDetail,
  CorruptionDetectionMode
} from '@/types';

interface CorruptionManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
}

const isCountMap = (value: unknown): value is Record<string, number> =>
  value !== null &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.values(value).every(
    (count) => typeof count === 'number' && Number.isInteger(count) && count >= 0
  );

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
  // The review-only findings live in their own top-level accordion with its own expand state.
  const [expandedReviewService, setExpandedReviewService] = useState<string | null>(null);
  const [reviewSectionOpen, setReviewSectionOpen] = useState(false);
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
  const clearSelection = selection.clear;
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
  const [scanId, setScanId] = useState<string | null>(null);
  const [removableServiceCounts, setRemovableServiceCounts] = useState<Record<string, number>>({});
  const [reviewOnlyServiceCounts, setReviewOnlyServiceCounts] = useState<Record<string, number>>(
    {}
  );
  const [missThreshold, setMissThreshold] = useState(3);
  const [detectionMode, setDetectionMode] = useState<CorruptionDetectionMode>('cache_and_logs');
  const [lookbackDays, setLookbackDays] = useState(30);
  const resultEpochRef = useRef(0);
  const [sectionExpanded, setSectionExpanded] = useState(() => {
    const saved = localStorage.getItem('management-corruption-expanded');
    return saved !== null ? saved === 'true' : false;
  });

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

  const lookbackOptions = [
    { days: 1, key: 'lookback1Day' },
    { days: 7, key: 'lookback7Days' },
    { days: 30, key: 'lookback30Days' },
    { days: 90, key: 'lookback90Days' },
    { days: 365, key: 'lookback365Days' }
  ].map(({ days, key }) => ({
    value: String(days),
    label: t(`management.corruption.${key}`),
    shortLabel: t(`management.corruption.${key}`),
    description: t('management.corruption.evidenceLookbackDescription', { days })
  }));

  const corruptionProjection = useMemo(
    () =>
      projectCorruptionCounts(corruptionSummary, removableServiceCounts, reviewOnlyServiceCounts),
    [corruptionSummary, removableServiceCounts, reviewOnlyServiceCounts]
  );

  const formattedLastDetection = useFormattedDateTime(lastDetectionTime);

  const clearLoadedResults = useCallback(() => {
    resultEpochRef.current += 1;
    setCorruptionSummary({});
    setCorruptionDetails({});
    setExpandedCorruptionService(null);
    setLoadingDetailsServices(new Set());
    setDetailsProgress({});
    setLastDetectionTime(null);
    setHasCachedResults(false);
    setScanId(null);
    setRemovableServiceCounts({});
    setReviewOnlyServiceCounts({});
    setPendingCorruptionRemoval(null);
    setPendingRemoveAll(false);
    setPendingRemoveSelected(false);
    clearSelection();
  }, [clearSelection]);

  const applyCachedScan = useCallback(
    (cached: CachedCorruptionDetectionResponse) => {
      clearLoadedResults();

      if (
        !cached.hasCachedResults ||
        !cached.scanId ||
        !cached.detectionMode ||
        cached.threshold == null ||
        cached.contractVersion !== 2 ||
        cached.lookbackDays == null ||
        !Number.isInteger(cached.lookbackDays) ||
        cached.lookbackDays < 1 ||
        cached.lookbackDays > 365 ||
        !isCountMap(cached.corruptionCounts) ||
        !isCountMap(cached.removableServiceCounts) ||
        !isCountMap(cached.reviewOnlyServiceCounts) ||
        cached.totalServicesWithCorruption == null ||
        cached.totalCorruptedChunks == null ||
        cached.removableTotal == null ||
        cached.reviewOnlyTotal == null
      ) {
        return false;
      }

      const projection = projectCorruptionCounts(
        cached.corruptionCounts,
        cached.removableServiceCounts,
        cached.reviewOnlyServiceCounts
      );
      if (
        !projection.isConsistent ||
        projection.rows.length !== cached.totalServicesWithCorruption ||
        projection.allTotal !== cached.totalCorruptedChunks ||
        projection.removableTotal !== cached.removableTotal ||
        projection.reviewOnlyTotal !== cached.reviewOnlyTotal
      ) {
        return false;
      }

      setCorruptionSummary(cached.corruptionCounts);
      setRemovableServiceCounts(cached.removableServiceCounts);
      setReviewOnlyServiceCounts(cached.reviewOnlyServiceCounts);
      setLastDetectionTime(cached.lastDetectionTime ?? null);
      setHasCachedResults(true);
      setScanId(cached.scanId);
      setDetectionMode(cached.detectionMode);
      setMissThreshold(cached.threshold);
      setLookbackDays(cached.lookbackDays);
      return true;
    },
    [clearLoadedResults]
  );

  const handleDetectionModeChange = useCallback(
    (value: string) => {
      const mode = value as CorruptionDetectionMode;
      if (mode === detectionMode) return;
      clearLoadedResults();
      setDetectionMode(mode);
    },
    [clearLoadedResults, detectionMode]
  );

  const handleThresholdChange = useCallback(
    (value: string) => {
      const threshold = Number(value);
      if (threshold === missThreshold) return;
      clearLoadedResults();
      setMissThreshold(threshold);
    },
    [clearLoadedResults, missThreshold]
  );

  const handleLookbackChange = useCallback(
    (value: string) => {
      const days = Number(value);
      if (days === lookbackDays) return;
      clearLoadedResults();
      setLookbackDays(days);
    },
    [clearLoadedResults, lookbackDays]
  );

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
      const requestEpoch = resultEpochRef.current;
      try {
        const cached = await ApiService.getCachedCorruptionDetection();
        if (requestEpoch !== resultEpochRef.current) {
          markLoaded();
          return;
        }

        const loaded = applyCachedScan(cached);
        if (loaded) {
          const projection = projectCorruptionCounts(
            cached.corruptionCounts ?? {},
            cached.removableServiceCounts ?? {},
            cached.reviewOnlyServiceCounts ?? {}
          );

          // Show notification only when explicitly requested or once per session
          const sessionKey = 'corruptionManager_loadedNotificationShown';
          const alreadyShownThisSession = sessionStorage.getItem(sessionKey) === 'true';
          if (showNotification || !alreadyShownThisSession) {
            if (projection.allTotal > 0) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.corruption.notifications.loadedResults', {
                  removable: formatCount(projection.removableTotal),
                  review: formatCount(projection.reviewOnlyTotal),
                  services: projection.rows.length
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
    [addNotification, t, beginLoad, markLoaded, markFailed, notifyError, applyCachedScan]
  );

  // Start a background scan
  const startScan = useCallback(async () => {
    if (isScanning || mockMode) return;

    // Note: NotificationsContext automatically replaces notifications with the same ID
    // when a new operation starts, so manual dismissal is not needed

    setIsStartingScan(true);
    clearLoadedResults();

    try {
      // Start background detection - SignalR will send CorruptionDetectionStarted event
      const result = await ApiService.startCorruptionDetection(
        missThreshold,
        detectionMode,
        lookbackDays
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
    detectionMode,
    lookbackDays,
    clearLoadedResults,
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
      const requestEpoch = resultEpochRef.current;

      // Load fresh results from the database (backend already saved them)
      const loadResults = async () => {
        try {
          const result = await ApiService.getCachedCorruptionDetection();
          if (requestEpoch !== resultEpochRef.current) return;
          applyCachedScan(result);
        } catch (err) {
          if (requestEpoch !== resultEpochRef.current) return;
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
  }, [on, off, beginLoad, markLoaded, notifyError, t, applyCachedScan]);

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

      // The backend updates the immutable scan snapshot after successful exact-path
      // removal. Reload it for both service and bulk completions instead of guessing
      // which candidates remain locally.
      const requestEpoch = resultEpochRef.current;
      void (async () => {
        try {
          const result = await ApiService.getCachedCorruptionDetection();
          if (requestEpoch !== resultEpochRef.current) return;
          applyCachedScan(result);
        } catch (err) {
          if (requestEpoch !== resultEpochRef.current) return;
          notifyError(
            t('management.corruption.errors.loadCachedData', 'Failed to load corruption data'),
            err,
            {
              silent: true,
              logLabel: '[CorruptionManager] Failed to reload after corruption removal'
            }
          );
          clearLoadedResults();
        }
      })();
    };

    on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    return () => {
      off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    };
  }, [on, off, notifyError, t, applyCachedScan, clearLoadedResults]);

  // Live percent for the per-service "view details" fetch. Deliberately a separate event from
  // the bulk scan's CorruptionDetectionProgress so this never surfaces as a global notification.
  useEffect(() => {
    if (!scanId) return;
    const handleDetailsProgress = (event: CorruptionDetailsProgressEvent) => {
      setDetailsProgress((prev) => ({ ...prev, [event.service]: event.percentComplete }));
    };

    on('CorruptionDetailsProgress', handleDetailsProgress);
    return () => {
      off('CorruptionDetailsProgress', handleDetailsProgress);
    };
  }, [on, off, scanId]);

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
      Object.keys(removableServiceCounts).filter((key) => removableServiceCounts[key] > 0)
    );
    const stale = [...selection.selected].filter((key) => !validKeys.has(key));
    if (stale.length > 0) {
      selection.setMany(stale, false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [removableServiceCounts]);

  // Initial load - load cached data without auto-scanning (matches GameCacheDetector pattern)
  // Note: Directory permissions are now handled by useDirectoryPermissions hook
  useEffect(() => {
    if (!hasInitiallyLoaded) {
      // Only load cached data - don't auto-start scan
      loadCachedData();
    }
  }, [hasInitiallyLoaded, loadCachedData]);

  const isServiceRemovable = useCallback(
    (service: string) => Boolean(scanId && (removableServiceCounts[service] ?? 0) > 0),
    [scanId, removableServiceCounts]
  );
  const selectedRemovableServices = [...selection.selected].filter(isServiceRemovable);
  const selectedRemovableTotal = selectedRemovableServices.reduce(
    (total, service) => total + (removableServiceCounts[service] ?? 0),
    0
  );

  const handleRemoveCorruption = (service: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }
    if (!isServiceRemovable(service)) {
      onError?.(t('management.corruption.reviewOnlyAction'));
      return;
    }
    setPendingCorruptionRemoval(service);
  };

  const confirmRemoveCorruption = async () => {
    if (
      !pendingCorruptionRemoval ||
      authMode !== 'authenticated' ||
      !scanId ||
      !isServiceRemovable(pendingCorruptionRemoval)
    )
      return;

    const service = pendingCorruptionRemoval;
    setPendingCorruptionRemoval(null);
    markCorruptionRemovalStarting(service);

    try {
      const result = await ApiService.removeCorruptedChunks(service, scanId);
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
    if (!scanId || corruptionProjection.removableTotal === 0) {
      onError?.(t('management.corruption.reviewOnlyAction'));
      return;
    }
    setPendingRemoveAll(true);
  };

  const confirmRemoveAll = async () => {
    if (authMode !== 'authenticated' || !scanId || corruptionProjection.removableTotal === 0)
      return;

    setPendingRemoveAll(false);
    markRemoveAllStarting('removeAll');

    try {
      await ApiService.removeAllCorruptedChunks(scanId);
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
    if (!scanId || selectedRemovableTotal === 0) {
      if (corruptionProjection.removableTotal === 0)
        onError?.(t('management.corruption.reviewOnlyAction'));
      return;
    }
    setPendingRemoveSelected(true);
  };

  const confirmRemoveSelected = async () => {
    if (authMode !== 'authenticated') return;

    setPendingRemoveSelected(false);
    if (selectedRemovableServices.length === 0 || !scanId) return;
    markRemoveSelectedStarting('removeSelected');

    try {
      // Reuses the bulk endpoint with a subset filter; the backend emits the same single
      // Service="all" aggregate terminal, so notification handling is unchanged.
      const result = await ApiService.removeAllCorruptedChunks(scanId, selectedRemovableServices);
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

  // Shared lazy fetch for a service's chunk details, used by both the removable list and the
  // review-only accordion (a service can appear in both). Returns false only on a real error
  // for the current scan epoch, so the caller can collapse the row it just opened.
  const loadCorruptionDetails = async (service: string): Promise<boolean> => {
    if (!scanId || corruptionDetails[service] || loadingDetailsServices.has(service)) {
      return true;
    }
    const requestEpoch = resultEpochRef.current;
    setLoadingDetailsServices((prev) => new Set(prev).add(service));
    try {
      const details = await ApiService.getCorruptionDetails(service, scanId);
      if (requestEpoch !== resultEpochRef.current) return true;
      setCorruptionDetails((prev) => ({ ...prev, [service]: details }));
      return true;
    } catch (err: unknown) {
      if (requestEpoch !== resultEpochRef.current) return true;
      onError?.(
        getErrorMessage(err) ||
          t('management.corruption.errors.loadDetails', {
            service: getServiceDisplayName(service)
          })
      );
      return false;
    } finally {
      if (requestEpoch === resultEpochRef.current) {
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

  const toggleCorruptionDetails = async (service: string) => {
    if (expandedCorruptionService === service) {
      setExpandedCorruptionService(null);
      return;
    }
    setExpandedCorruptionService(service);
    const loaded = await loadCorruptionDetails(service);
    if (!loaded) setExpandedCorruptionService((prev) => (prev === service ? null : prev));
  };

  const toggleReviewDetails = async (service: string) => {
    if (expandedReviewService === service) {
      setExpandedReviewService(null);
      return;
    }
    setExpandedReviewService(service);
    const loaded = await loadCorruptionDetails(service);
    if (!loaded) setExpandedReviewService((prev) => (prev === service ? null : prev));
  };

  const corruptionList = corruptionProjection.rows;
  // The main list only shows services you can act on; everything review-only is pulled into a
  // single top-level "Review only" accordion below. A service with both appears in both.
  const removableList = corruptionList.filter((row) => row.removable > 0);
  const reviewList = corruptionList.filter((row) => row.reviewOnly > 0);

  // Batch selection derives from the CURRENTLY VISIBLE list, matching Remove All semantics.
  const visibleServiceKeys = corruptionList.map((row) => row.service);
  const removableServiceKeys = visibleServiceKeys.filter(isServiceRemovable);
  const allVisibleSelected = selection.allSelected(removableServiceKeys);
  // Shared busy gate for every batch control (same conditions Remove All disables on, plus
  // the optimistic "starting" flags so a second click can't fire mid-start).
  const batchGateActive =
    (isLoading && !hasInitiallyLoaded) ||
    !scanId ||
    corruptionProjection.removableTotal === 0 ||
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
    <div className="mgmt-toolbar">
      <div className="flex flex-col gap-2 w-full sm:w-auto sm:flex-row sm:flex-wrap sm:items-center">
        <div className="w-full sm:w-auto">
          <EnhancedDropdown
            variant="button"
            options={detectionModeOptions}
            value={detectionMode}
            onChange={handleDetectionModeChange}
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
            onChange={handleThresholdChange}
            disabled={isScanning || isAnyRemovalRunning}
            dropdownWidth="w-72"
            alignRight={true}
            dropdownTitle={t('management.corruption.sensitivityTitle')}
            compactMode={true}
          />
        </div>
        {detectionMode !== 'logs_only' && (
          <div className="w-full sm:w-auto">
            <EnhancedDropdown
              variant="button"
              options={lookbackOptions}
              value={String(lookbackDays)}
              onChange={handleLookbackChange}
              disabled={isScanning || isAnyRemovalRunning}
              dropdownWidth="w-72"
              alignRight={true}
              dropdownTitle={t('management.corruption.evidenceLookbackTitle')}
              compactMode={true}
            />
          </div>
        )}
      </div>
      {hasCachedResults && lastDetectionTime && !isScanning && !isLoading && (
        <p className="mgmt-scanmeta">
          {t('common.resultsFromPreviousScan')} · {formattedLastDetection}
          {detectionMode !== 'logs_only' && (
            <> · {t('management.corruption.evidenceWindow', { days: lookbackDays })}</>
          )}
        </p>
      )}
    </div>
  );

  // Header action row (AccordionSection badge): the labelled count badges stay
  // visible outside the menu (status readouts, not actions); every action
  // button now lives in one overflow menu, reachable while the section is
  // collapsed like every other Storage section.
  const headerActions = (
    <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
      {corruptionProjection.removableTotal > 0 && (
        <Badge variant="neutral" className="badge-count badge-count-warning">
          {t('management.corruption.removableCount', {
            count: formatCount(corruptionProjection.removableTotal)
          })}
        </Badge>
      )}
      {corruptionProjection.reviewOnlyTotal > 0 && (
        <Badge variant="neutral" className="badge-count">
          {t('management.corruption.reviewCount', {
            count: formatCount(corruptionProjection.reviewOnlyTotal)
          })}
        </Badge>
      )}
      {selectedRemovableServices.length > 0 && (
        <Badge variant="neutral" className="badge-count">
          {selectedRemovableServices.length}
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
              disabled={batchGateActive || selectedRemovableServices.length === 0}
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
                !scanId ||
                corruptionProjection.removableTotal === 0 ||
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
      <AccordionSection
        title={t('management.corruption.title')}
        description={t('management.corruption.summary')}
        icon={AlertTriangle}
        iconColor="var(--theme-icon-yellow)"
        isExpanded={sectionExpanded}
        onToggle={() => setSectionExpanded((prev) => !prev)}
        badge={headerActions}
      >
        <div className="space-y-3">
          {controlSelectors}

          {/* Scanning Status */}
          {isScanning && <LoadingState message={t('management.corruption.scanningMessage')} />}

          {hasCachedResults && corruptionProjection.reviewOnlyTotal > 0 && (
            <Alert color="yellow">
              <div>
                <p className="font-medium">
                  {t(
                    corruptionProjection.removableTotal > 0
                      ? 'management.corruption.mixedReviewTitle'
                      : 'management.corruption.reviewOnlyTitle'
                  )}
                </p>
                <p className="text-sm mt-1">
                  {t(
                    corruptionProjection.removableTotal > 0
                      ? 'management.corruption.mixedReviewDescription'
                      : 'management.corruption.reviewOnlyDescription',
                    { count: formatCount(corruptionProjection.reviewOnlyTotal) }
                  )}
                </p>
              </div>
            </Alert>
          )}

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
                  ? t('management.corruption.directoryMissing', 'Required directories not found')
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
                  {removableList.length > 0 && (
                    <>
                      {/* Batch multi-select toolbar: select-all over the removable services.
                          The selected count shows once in the section header badge; Remove
                          Selected lives in the header cluster (with Remove All). */}
                      <div className="flex flex-wrap items-center gap-3">
                        <Checkbox
                          checked={allVisibleSelected}
                          onChange={() =>
                            selection.setMany(removableServiceKeys, !allVisibleSelected)
                          }
                          disabled={batchGateActive || removableServiceKeys.length === 0}
                          label={
                            allVisibleSelected
                              ? t('management.batchSelect.deselectAll')
                              : t('management.batchSelect.selectAll')
                          }
                        />
                      </div>
                      <div className="mgmt-list">
                        {removableList.map((row) => {
                          const { service, removable } = row;
                          const isRowExpanded = expandedCorruptionService === service;
                          const isRowRemoving =
                            removingCorruption === service || isCorruptionRemovalPending(service);
                          const serviceCanRemove = isServiceRemovable(service);
                          return (
                            <div key={`corruption-${service}`}>
                              <div className="mgmt-row mgmt-row--interactive flex-wrap">
                                <Checkbox
                                  checked={selection.isSelected(service)}
                                  onChange={() => selection.toggle(service)}
                                  disabled={batchGateActive || isRowRemoving || !serviceCanRemove}
                                  aria-label={t('management.batchSelect.selectItem', {
                                    name: getServiceDisplayName(service)
                                  })}
                                  className="flex-shrink-0"
                                />
                                <div className="mgmt-row__body">
                                  <p className="mgmt-row__title mgmt-row__title--service truncate">
                                    {getServiceDisplayName(service)}
                                  </p>
                                </div>
                                <div className="mgmt-row__actions flex-wrap justify-end">
                                  {removable > 0 && (
                                    <Badge
                                      variant="neutral"
                                      className="badge-count badge-count-warning"
                                    >
                                      {t('management.corruption.removableCount', {
                                        count: formatCount(removable)
                                      })}
                                    </Badge>
                                  )}
                                  <Button
                                    variant="filled"
                                    color="gray"
                                    size="sm"
                                    onClick={() => toggleCorruptionDetails(service)}
                                    aria-expanded={isRowExpanded}
                                  >
                                    {isRowExpanded ? (
                                      <ChevronUp className="w-4 h-4" />
                                    ) : (
                                      <ChevronDown className="w-4 h-4" />
                                    )}
                                  </Button>
                                  <Tooltip
                                    content={
                                      serviceCanRemove
                                        ? t('management.corruption.deleteCorrupted')
                                        : t('management.corruption.reviewOnlyAction')
                                    }
                                  >
                                    <Button
                                      onClick={() => handleRemoveCorruption(service)}
                                      awaitPermissions
                                      loading={isRowRemoving}
                                      disabled={
                                        mockMode ||
                                        anyCorruptionRemovalPending ||
                                        isCorruptionRemovalActive ||
                                        loadingDetailsServices.has(service) ||
                                        authMode !== 'authenticated' ||
                                        logsReadOnly ||
                                        cacheReadOnly ||
                                        !isDockerAvailable ||
                                        !serviceCanRemove
                                      }
                                      variant="filled"
                                      color="red"
                                      size="sm"
                                    >
                                      {isRowRemoving
                                        ? t('management.corruption.removing')
                                        : t('common.remove')}
                                    </Button>
                                  </Tooltip>
                                </div>
                              </div>
                              <CollapsibleRegion
                                open={isRowExpanded}
                                contentClassName="mgmt-row-detail"
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
                                  <CorruptionChunkList
                                    chunks={corruptionDetails[service]}
                                    variant="removable"
                                  />
                                ) : (
                                  <p className="py-4 text-center text-sm text-themed-muted">
                                    {t('management.corruption.noDetailsAvailable')}
                                  </p>
                                )}
                              </CollapsibleRegion>
                            </div>
                          );
                        })}
                      </div>
                    </>
                  )}

                  {reviewList.length > 0 && (
                    <AccordionSection
                      title={t('management.corruption.reviewSectionTitle')}
                      count={corruptionProjection.reviewOnlyTotal}
                      surface="well"
                      isExpanded={reviewSectionOpen}
                      onToggle={() => setReviewSectionOpen((open) => !open)}
                    >
                      <p className="mgmt-scanmeta mb-3">
                        {t('management.corruption.reviewSectionNote')}
                      </p>
                      <div className="mgmt-list">
                        {reviewList.map((row) => {
                          const { service, reviewOnly } = row;
                          const isRowExpanded = expandedReviewService === service;
                          return (
                            <div key={`corruption-review-${service}`}>
                              <div className="mgmt-row mgmt-row--interactive flex-wrap">
                                <div className="mgmt-row__body">
                                  <p className="mgmt-row__title mgmt-row__title--service truncate">
                                    {getServiceDisplayName(service)}
                                  </p>
                                </div>
                                <div className="mgmt-row__actions flex-wrap justify-end">
                                  <Badge variant="neutral" className="badge-count">
                                    {t('management.corruption.reviewCount', {
                                      count: formatCount(reviewOnly)
                                    })}
                                  </Badge>
                                  <Button
                                    variant="filled"
                                    color="gray"
                                    size="sm"
                                    onClick={() => toggleReviewDetails(service)}
                                    aria-expanded={isRowExpanded}
                                  >
                                    {isRowExpanded ? (
                                      <ChevronUp className="w-4 h-4" />
                                    ) : (
                                      <ChevronDown className="w-4 h-4" />
                                    )}
                                  </Button>
                                </div>
                              </div>
                              <CollapsibleRegion
                                open={isRowExpanded}
                                contentClassName="mgmt-row-detail"
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
                                  <CorruptionChunkList
                                    chunks={corruptionDetails[service]}
                                    variant="review"
                                  />
                                ) : (
                                  <p className="py-4 text-center text-sm text-themed-muted">
                                    {t('management.corruption.noDetailsAvailable')}
                                  </p>
                                )}
                              </CollapsibleRegion>
                            </div>
                          );
                        })}
                      </div>
                    </AccordionSection>
                  )}
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
              services: corruptionProjection.removableServiceTotal,
              candidates: formatCount(corruptionProjection.removableTotal)
            })}
          </p>

          <CorruptionRemovalWarning
            extraCautions={<li>{t('management.corruption.modal.removeAllAcrossAllServices')}</li>}
          />

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingRemoveAll(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={confirmRemoveAll}
              disabled={!scanId || corruptionProjection.removableTotal === 0}
            >
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
            {t('management.corruption.modal.confirmRemoveSelected', {
              services: selectedRemovableServices.length,
              candidates: formatCount(selectedRemovableTotal)
            })}
          </p>

          <CorruptionRemovalWarning />

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingRemoveSelected(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={confirmRemoveSelected}
              disabled={!scanId || selectedRemovableTotal === 0}
            >
              {t('management.batchSelect.removeSelected', {
                count: selectedRemovableServices.length
              })}
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

          <CorruptionRemovalWarning
            extraCautions={
              <>
                <li>
                  {t('management.corruption.modal.validFilesRemain', {
                    service: pendingCorruptionRemoval
                      ? getServiceDisplayName(pendingCorruptionRemoval)
                      : undefined
                  })}
                </li>
                <li>
                  {t('management.corruption.modal.removesApproximately', {
                    count: removableServiceCounts[pendingCorruptionRemoval || ''] || 0
                  })}
                </li>
              </>
            }
          />

          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingCorruptionRemoval(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={confirmRemoveCorruption}
              disabled={!pendingCorruptionRemoval || !isServiceRemovable(pendingCorruptionRemoval)}
            >
              {t('management.corruption.modal.deleteCacheAndLogs')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CorruptionManager;
