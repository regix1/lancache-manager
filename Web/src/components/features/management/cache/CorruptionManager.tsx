import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle,
  ChevronDown,
  ChevronUp,
  CircleCheck,
  RefreshCw,
  Search,
  Trash2,
  Zap
} from 'lucide-react';
import '../managementSectionContent.css';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useDockerSocket } from '@contexts/useDockerSocket';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useNotifications } from '@contexts/notifications';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type {
  CorruptionDetectionCompleteEvent,
  CorruptionRemovalCompleteEvent
} from '@contexts/SignalRContext/types';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useOptimisticPending } from '@/hooks/useOptimisticPending';
import { useSelectionSet } from '@/hooks/useSelectionSet';
import { useFormattedDateTime } from '@/hooks/useFormattedDateTime';
import { useManagerLoading } from '@/hooks/useManagerLoading';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import { showPermissionBlock } from '@utils/permissionUi';
import { getServiceDisplayName } from '@utils/serviceDisplayName';
import { formatCount } from '@utils/formatters';
import { AccordionSection } from '@components/ui/AccordionSection';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Button } from '@components/ui/Button';
import { Checkbox } from '@components/ui/Checkbox';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { ActionMenuDangerItem, ActionMenuDivider, ActionMenuItem } from '@components/ui/ActionMenu';
import { EmptyState, LoadingState, ReadOnlyBadge } from '@components/ui/ManagerCard';
import Badge from '@components/ui/Badge';
import CorruptionChunkList from './CorruptionChunkList';
import CorruptionRemovalWarning from './CorruptionRemovalWarning';
import { projectCorruptionCounts } from './corruptionCountProjection';
import {
  hasOnlyKeys,
  isIsoDate,
  isOptionalNonNegativeInteger,
  isPlainRecord
} from './corruptionContractValidation';
import type {
  CachedCorruptionDetectionResponse,
  CorruptedChunkDetail,
  CorruptionDetectionMethod,
  CorruptionScanCoverage
} from '@/types';
import type { StructuralScanMode } from '@/types/corruptionScan';

interface CorruptionManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
}

const isCountMap = (value: unknown): value is Record<string, number> =>
  value !== null &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  Object.entries(value).every(
    ([key, count]) =>
      key.trim().length > 0 && typeof count === 'number' && Number.isInteger(count) && count >= 0
  );

const isCoverage = (value: unknown): value is CorruptionScanCoverage => {
  if (!isPlainRecord(value)) return false;
  const allowedKeys = [
    'filesSeen',
    'filesChecked',
    'consistent',
    'bytesRead',
    'sparseFiles',
    'skippedByReason',
    'ioErrors'
  ] as const;
  if (!hasOnlyKeys(value, allowedKeys) || !isCountMap(value.skippedByReason)) return false;
  const counts = [
    value.filesSeen,
    value.filesChecked,
    value.consistent,
    value.bytesRead,
    value.sparseFiles,
    value.ioErrors
  ];
  return (
    counts.every((count) => typeof count === 'number' && Number.isInteger(count) && count >= 0) &&
    (value.filesChecked as number) <= (value.filesSeen as number) &&
    (value.consistent as number) <= (value.filesChecked as number) &&
    (value.sparseFiles as number) <= (value.filesSeen as number)
  );
};

const isCorruptionDetectionMethod = (value: unknown): value is CorruptionDetectionMethod =>
  value === 'repeated_miss' || value === 'structural';

const isScanId = (value: unknown): value is string =>
  typeof value === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);

const CorruptionManager: React.FC<CorruptionManagerProps> = ({ authMode, mockMode, onError }) => {
  const { t } = useTranslation();
  const { notifications, addNotification, isAnyRemovalRunning } = useNotifications();
  const { notifyError } = useErrorHandler();
  const { on, off } = useSignalR();
  const { isDockerAvailable } = useDockerSocket();
  const { logsReadOnly, cacheReadOnly, logsExist, cacheExist, checkingPermissions } =
    useDirectoryPermissionsContext();

  const isScanningFromNotification = useOperationBusy({
    types: ['corruption_detection'],
    status: 'running'
  });
  const isScanWaitingFromNotification = useOperationBusy({
    types: ['corruption_detection'],
    status: 'waiting'
  });
  const [startingScanAction, setStartingScanAction] = useState<
    'repeated_miss' | StructuralScanMode | null
  >(null);
  const scanRequestInFlightRef = useRef(false);
  const isStartingScan = startingScanAction !== null;
  const isScanning = isScanningFromNotification || isStartingScan;
  const isScanBusy = isScanning || isScanWaitingFromNotification;
  const isCorruptionRemovalActive = useOperationBusy({
    types: ['corruption_removal'],
    status: ['running', 'waiting']
  });

  const [corruptionCounts, setCorruptionCounts] = useState<Record<string, number>>({});
  const [corruptionDetails, setCorruptionDetails] = useState<
    Record<string, CorruptedChunkDetail[]>
  >({});
  const [detailErrors, setDetailErrors] = useState<Set<string>>(new Set());
  const [loadingDetailsServices, setLoadingDetailsServices] = useState<Set<string>>(new Set());
  const [expandedService, setExpandedService] = useState<string | null>(null);
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [scanCoverage, setScanCoverage] = useState<CorruptionScanCoverage | null>(null);
  const [hasCachedResults, setHasCachedResults] = useState(false);
  const [scanId, setScanId] = useState<string | null>(null);
  const [detectionMethod, setDetectionMethod] =
    useState<CorruptionDetectionMethod>('repeated_miss');
  const [missThreshold, setMissThreshold] = useState(3);
  const [lookbackDays, setLookbackDays] = useState(30);
  const [cachedLoadFailed, setCachedLoadFailed] = useState(false);
  const resultEpochRef = useRef(0);
  const activeDetectionNotification = notifications.find(
    (notification) =>
      notification.type === 'corruption_detection' &&
      (notification.status === 'running' || notification.status === 'waiting')
  );
  const activeNotificationMethod = activeDetectionNotification?.details?.detectionMethod;
  const activeNotificationScanMode = activeDetectionNotification?.details?.scanMode;
  const displayedDetectionMethod =
    isScanBusy && isCorruptionDetectionMethod(activeNotificationMethod)
      ? activeNotificationMethod
      : detectionMethod;
  const activeRunningScanAction: 'repeated_miss' | StructuralScanMode | null = startingScanAction
    ? startingScanAction
    : activeDetectionNotification?.status === 'running'
      ? activeNotificationMethod === 'repeated_miss'
        ? 'repeated_miss'
        : activeNotificationMethod === 'structural' && activeNotificationScanMode
          ? activeNotificationScanMode
          : null
      : null;
  const activeBaselineStatus = activeDetectionNotification?.details?.baselineStatus;
  const activeEffectiveScanMode = activeDetectionNotification?.details?.effectiveScanMode;
  const activeScanResumed = activeDetectionNotification?.details?.resumed === true;
  const isInitialBaselineBuild =
    activeRunningScanAction === 'incremental' &&
    (activeBaselineStatus === 'building' || activeEffectiveScanMode === 'baseline');

  useEffect(() => {
    if (!startingScanAction || !activeDetectionNotification) return;
    setStartingScanAction(null);
    scanRequestInFlightRef.current = false;
  }, [activeDetectionNotification, startingScanAction]);

  const [sectionExpanded, setSectionExpanded] = useState(() => {
    const saved = localStorage.getItem('management-corruption-expanded');
    return saved !== null ? saved === 'true' : false;
  });
  useEffect(() => {
    localStorage.setItem('management-corruption-expanded', String(sectionExpanded));
  }, [sectionExpanded]);

  const { isLoading, isRefreshing, hasInitiallyLoaded, beginLoad, markLoaded, markFailed } =
    useManagerLoading();
  const selection = useSelectionSet<string>();
  const clearSelection = selection.clear;

  const [pendingServiceRemoval, setPendingServiceRemoval] = useState<string | null>(null);
  const {
    isPending: isServiceRemovalPending,
    anyPending: anyServiceRemovalPending,
    markStarting: markServiceRemovalStarting,
    clearPending: clearServiceRemovalPending,
    clearOnNotification: clearServiceRemovalOnNotification
  } = useOptimisticPending<string>();

  const [pendingRemoveAll, setPendingRemoveAll] = useState(false);
  const {
    anyPending: startingRemoveAll,
    markStarting: markRemoveAllStarting,
    clearPending: clearRemoveAllPending,
    clearOnNotification: clearRemoveAllOnNotification
  } = useOptimisticPending<'removeAll'>();

  const [pendingRemoveSelected, setPendingRemoveSelected] = useState(false);
  const {
    anyPending: startingRemoveSelected,
    markStarting: markRemoveSelectedStarting,
    clearPending: clearRemoveSelectedPending,
    clearOnNotification: clearRemoveSelectedOnNotification
  } = useOptimisticPending<'removeSelected'>();

  const projection = useMemo(() => projectCorruptionCounts(corruptionCounts), [corruptionCounts]);
  const formattedLastDetection = useFormattedDateTime(lastDetectionTime);
  const skippedCoverageTotal = useMemo(
    () =>
      scanCoverage
        ? Object.values(scanCoverage.skippedByReason).reduce((total, count) => total + count, 0)
        : 0,
    [scanCoverage]
  );

  const methodOptions = [
    {
      value: 'repeated_miss',
      label: t('management.corruption.methods.repeatedMiss.label'),
      shortLabel: t('management.corruption.methods.repeatedMiss.shortLabel'),
      description: t('management.corruption.methods.repeatedMiss.description')
    },
    {
      value: 'structural',
      label: t('management.corruption.methods.structural.label'),
      shortLabel: t('management.corruption.methods.structural.shortLabel'),
      description: t('management.corruption.methods.structural.description')
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

  const clearLoadedResults = useCallback(() => {
    resultEpochRef.current += 1;
    setCorruptionCounts({});
    setCorruptionDetails({});
    setDetailErrors(new Set());
    setLoadingDetailsServices(new Set());
    setExpandedService(null);
    setLastDetectionTime(null);
    setScanCoverage(null);
    setHasCachedResults(false);
    setScanId(null);
    setPendingServiceRemoval(null);
    setPendingRemoveAll(false);
    setPendingRemoveSelected(false);
    setCachedLoadFailed(false);
    clearSelection();
  }, [clearSelection]);

  const applyCachedScan = useCallback(
    (cached: CachedCorruptionDetectionResponse) => {
      if (!cached.hasCachedResults) {
        clearLoadedResults();
        return false;
      }

      const settings = cached.settings;
      const cachedMethod = cached.detectionMethod;
      const detectionMethodIsValid = isCorruptionDetectionMethod(cached.detectionMethod);
      const settingsAreValid =
        isPlainRecord(settings) &&
        hasOnlyKeys(settings, [
          'threshold',
          'lookbackDays',
          'minStableAgeSeconds',
          'maxPrefixBytes'
        ]) &&
        isOptionalNonNegativeInteger(settings?.threshold) &&
        isOptionalNonNegativeInteger(settings?.lookbackDays) &&
        isOptionalNonNegativeInteger(settings?.minStableAgeSeconds) &&
        isOptionalNonNegativeInteger(settings?.maxPrefixBytes);
      const repeatedMissSettingsAreValid =
        detectionMethodIsValid &&
        cached.detectionMethod === 'repeated_miss' &&
        settingsAreValid &&
        typeof settings?.threshold === 'number' &&
        [3, 5, 10].includes(settings.threshold) &&
        typeof settings.lookbackDays === 'number' &&
        Number.isInteger(settings.lookbackDays) &&
        settings.lookbackDays >= 1 &&
        settings.lookbackDays <= 365 &&
        settings.minStableAgeSeconds == null &&
        settings.maxPrefixBytes == null;
      const structuralSettingsAreValid =
        detectionMethodIsValid &&
        cached.detectionMethod === 'structural' &&
        settingsAreValid &&
        settings?.threshold == null &&
        settings.lookbackDays == null &&
        typeof settings.minStableAgeSeconds === 'number' &&
        settings.minStableAgeSeconds === 600 &&
        typeof settings.maxPrefixBytes === 'number' &&
        settings.maxPrefixBytes === 65_535;
      const detectionMethodCount = isCorruptionDetectionMethod(cachedMethod)
        ? cached.detectionCounts?.[cachedMethod]
        : undefined;
      const detectionCountsAreValid =
        isCountMap(cached.detectionCounts) &&
        detectionMethodIsValid &&
        Object.keys(cached.detectionCounts).every(
          (method) => isCorruptionDetectionMethod(method) && method === cached.detectionMethod
        ) &&
        cached.totalCorruptedChunks != null &&
        detectionMethodCount === cached.totalCorruptedChunks;
      const coverageIsValid =
        detectionMethodIsValid &&
        (cached.detectionMethod === 'structural'
          ? isCoverage(cached.coverage)
          : cached.coverage == null);

      if (
        !isScanId(cached.scanId) ||
        cached.contractVersion !== 4 ||
        !detectionMethodIsValid ||
        (!repeatedMissSettingsAreValid && !structuralSettingsAreValid) ||
        !detectionCountsAreValid ||
        !coverageIsValid ||
        !isCountMap(cached.corruptionCounts) ||
        cached.totalServicesWithCorruption == null ||
        cached.totalCorruptedChunks == null ||
        !isIsoDate(cached.lastDetectionTime)
      ) {
        clearLoadedResults();
        setCachedLoadFailed(true);
        return false;
      }

      const nextProjection = projectCorruptionCounts(cached.corruptionCounts);
      if (
        !nextProjection.isConsistent ||
        nextProjection.serviceTotal !== cached.totalServicesWithCorruption ||
        nextProjection.total !== cached.totalCorruptedChunks
      ) {
        clearLoadedResults();
        setCachedLoadFailed(true);
        return false;
      }

      clearLoadedResults();
      setCorruptionCounts(cached.corruptionCounts);
      setLastDetectionTime(cached.lastDetectionTime ?? null);
      setHasCachedResults(true);
      setScanId(cached.scanId);
      setDetectionMethod(cachedMethod as CorruptionDetectionMethod);
      setScanCoverage(cached.coverage ?? null);
      if (cached.detectionMethod === 'repeated_miss') {
        setMissThreshold(cached.settings!.threshold as number);
        setLookbackDays(cached.settings!.lookbackDays as number);
      }
      setCachedLoadFailed(false);
      return true;
    },
    [clearLoadedResults]
  );

  const loadCachedData = useCallback(
    async (showNotification = false) => {
      beginLoad(showNotification);
      setCachedLoadFailed(false);
      const requestEpoch = resultEpochRef.current;
      try {
        const cached = await ApiService.getCachedCorruptionDetection();
        if (requestEpoch !== resultEpochRef.current) {
          markLoaded();
          return;
        }

        const loaded = applyCachedScan(cached);
        if (loaded) {
          const loadedProjection = projectCorruptionCounts(cached.corruptionCounts ?? {});
          const sessionKey = 'corruptionManager_loadedNotificationShown';
          const alreadyShown = sessionStorage.getItem(sessionKey) === 'true';
          if (showNotification || !alreadyShown) {
            if (loadedProjection.total > 0) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.corruption.notifications.loadedResults', {
                  count: formatCount(loadedProjection.total),
                  services: loadedProjection.serviceTotal
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
        } else if (showNotification && !cached.hasCachedResults) {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: t('management.corruption.notifications.noPreviousResults'),
            details: { notificationType: 'info' }
          });
        }
        markLoaded();
      } catch (error: unknown) {
        if (requestEpoch !== resultEpochRef.current) return;
        setCachedLoadFailed(true);
        notifyError(t('management.corruption.errors.loadCachedData'), error, {
          silent: true,
          logLabel: '[CorruptionManager] Failed to load cached data'
        });
        markFailed();
      }
    },
    [addNotification, applyCachedScan, beginLoad, markFailed, markLoaded, notifyError, t]
  );

  useEffect(() => {
    if (!hasInitiallyLoaded) void loadCachedData();
  }, [hasInitiallyLoaded, loadCachedData]);

  const handleThresholdChange = useCallback(
    (value: string) => {
      const threshold = Number(value);
      if (threshold === missThreshold) return;
      clearLoadedResults();
      setMissThreshold(threshold);
    },
    [clearLoadedResults, missThreshold]
  );

  const handleMethodChange = useCallback(
    (value: string) => {
      if (!isCorruptionDetectionMethod(value) || value === detectionMethod) return;
      clearLoadedResults();
      setDetectionMethod(value);
    },
    [clearLoadedResults, detectionMethod]
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

  const requiresRepeatedMissResources = displayedDetectionMethod === 'repeated_miss';
  const directoryMissing = !cacheExist || (requiresRepeatedMissResources && !logsExist);
  const isReadOnly = cacheReadOnly || (requiresRepeatedMissResources && logsReadOnly);
  const hasRemovalPermissionIssue = directoryMissing || isReadOnly;
  const showReadOnlyPlaceholder = showPermissionBlock(
    checkingPermissions,
    hasRemovalPermissionIssue || (requiresRepeatedMissResources && !isDockerAvailable)
  );
  const corruptionRemovalBusy =
    anyServiceRemovalPending ||
    startingRemoveAll ||
    startingRemoveSelected ||
    isCorruptionRemovalActive;
  const scanBlocked =
    isScanBusy ||
    isAnyRemovalRunning ||
    corruptionRemovalBusy ||
    checkingPermissions ||
    directoryMissing ||
    mockMode ||
    authMode !== 'authenticated';

  const startScan = useCallback(
    async (scanMode?: StructuralScanMode) => {
      const action: 'repeated_miss' | StructuralScanMode =
        detectionMethod === 'repeated_miss' ? 'repeated_miss' : (scanMode ?? 'full');
      if (
        scanBlocked ||
        scanRequestInFlightRef.current ||
        (detectionMethod === 'structural' && !scanMode)
      ) {
        return;
      }

      scanRequestInFlightRef.current = true;
      setStartingScanAction(action);
      clearLoadedResults();
      setCachedLoadFailed(false);
      try {
        const result = await ApiService.startCorruptionDetection(
          detectionMethod,
          missThreshold,
          lookbackDays,
          detectionMethod === 'structural' ? scanMode : undefined
        );
        if (result.operationId && !result.queued && !result.alreadyRunning) {
          const confirmedScanMode =
            detectionMethod === 'structural' ? (result.scanMode ?? scanMode) : undefined;
          addNotification(
            buildSeededRunningNotification(
              'corruption_detection',
              result.operationId,
              t(
                detectionMethod !== 'structural'
                  ? 'signalr.corruptionDetect.startingRepeatedMiss'
                  : confirmedScanMode === 'incremental'
                    ? 'signalr.corruptionDetect.startingStructuralIncremental'
                    : 'signalr.corruptionDetect.startingStructuralFull'
              ),
              {
                detectionMethod,
                ...(confirmedScanMode ? { scanMode: confirmedScanMode } : {})
              }
            )
          );
        } else {
          setStartingScanAction(null);
          scanRequestInFlightRef.current = false;
        }
      } catch (error: unknown) {
        notifyError(t('management.corruption.errors.startScan'), error, {
          logLabel: '[CorruptionManager] Failed to start scan'
        });
        setStartingScanAction(null);
        scanRequestInFlightRef.current = false;
      }
    },
    [
      addNotification,
      clearLoadedResults,
      detectionMethod,
      lookbackDays,
      missThreshold,
      notifyError,
      scanBlocked,
      t
    ]
  );

  useEffect(() => {
    const handleDetectionComplete = (_event: CorruptionDetectionCompleteEvent) => {
      setStartingScanAction(null);
      scanRequestInFlightRef.current = false;

      // The backend preserves the prior authoritative result when a scan fails or is cancelled.
      // Reload on every terminal event so clearing the local view at start never hides that result
      // until a manual Load or page refresh.
      beginLoad(true);
      const requestEpoch = resultEpochRef.current;
      void (async () => {
        try {
          const result = await ApiService.getCachedCorruptionDetection();
          if (requestEpoch !== resultEpochRef.current) return;
          applyCachedScan(result);
        } catch (error: unknown) {
          if (requestEpoch !== resultEpochRef.current) return;
          setCachedLoadFailed(true);
          notifyError(t('management.corruption.errors.loadCachedData'), error, {
            silent: true,
            logLabel: '[CorruptionManager] Failed to load completed scan'
          });
        } finally {
          markLoaded();
        }
      })();
    };

    on('CorruptionDetectionComplete', handleDetectionComplete);
    return () => off('CorruptionDetectionComplete', handleDetectionComplete);
  }, [applyCachedScan, beginLoad, markLoaded, notifyError, off, on, t]);

  useEffect(() => {
    const handleRemovalComplete = (event: CorruptionRemovalCompleteEvent) => {
      if (!event.success || !event.service) return;
      const requestEpoch = resultEpochRef.current;
      void (async () => {
        try {
          const result = await ApiService.getCachedCorruptionDetection();
          if (requestEpoch !== resultEpochRef.current) return;
          applyCachedScan(result);
        } catch (error: unknown) {
          if (requestEpoch !== resultEpochRef.current) return;
          setCachedLoadFailed(true);
          notifyError(t('management.corruption.errors.loadCachedData'), error, {
            silent: true,
            logLabel: '[CorruptionManager] Failed to reload after removal'
          });
        }
      })();
    };

    on('CorruptionRemovalComplete', handleRemovalComplete);
    return () => off('CorruptionRemovalComplete', handleRemovalComplete);
  }, [applyCachedScan, notifyError, off, on, t]);

  useEffect(() => {
    if (anyServiceRemovalPending) {
      const runningRemoval = notifications.find(
        (notification) =>
          notification.type === 'corruption_removal' && notification.status === 'running'
      );
      if (runningRemoval) {
        const service = (runningRemoval.details?.service as string | undefined) ?? '';
        clearServiceRemovalOnNotification(
          service,
          notifications,
          (notification, key) =>
            notification.type === 'corruption_removal' &&
            notification.status === 'running' &&
            notification.details?.service === key
        );
      }
    }
    if (startingRemoveAll) {
      clearRemoveAllOnNotification(
        'removeAll',
        notifications,
        (notification) =>
          notification.type === 'corruption_removal' && notification.status === 'running'
      );
    }
    if (startingRemoveSelected) {
      clearRemoveSelectedOnNotification(
        'removeSelected',
        notifications,
        (notification) =>
          notification.type === 'corruption_removal' && notification.status === 'running'
      );
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [notifications]);

  useEffect(() => {
    const validServices = new Set(projection.rows.map((row) => row.service));
    const stale = [...selection.selected].filter((service) => !validServices.has(service));
    if (stale.length > 0) selection.setMany(stale, false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projection.rows]);

  const activeRemoval = notifications.find(
    (notification) =>
      notification.type === 'corruption_removal' && notification.status === 'running'
  );
  const removingService = (activeRemoval?.details?.service as string | undefined) ?? null;
  const serviceKeys = projection.rows.map((row) => row.service);
  const selectedServices = [...selection.selected].filter((service) =>
    serviceKeys.includes(service)
  );
  const selectedTotal = selectedServices.reduce(
    (total, service) => total + (corruptionCounts[service] ?? 0),
    0
  );
  const allVisibleSelected = selection.allSelected(serviceKeys);
  const removalBlocked =
    !scanId ||
    projection.total === 0 ||
    mockMode ||
    anyServiceRemovalPending ||
    isCorruptionRemovalActive ||
    startingRemoveAll ||
    startingRemoveSelected ||
    authMode !== 'authenticated' ||
    checkingPermissions ||
    hasRemovalPermissionIssue ||
    (requiresRepeatedMissResources && !isDockerAvailable);

  const loadDetails = async (service: string) => {
    if (!scanId || loadingDetailsServices.has(service)) return;
    const requestEpoch = resultEpochRef.current;
    setDetailErrors((current) => {
      const next = new Set(current);
      next.delete(service);
      return next;
    });
    setLoadingDetailsServices((current) => new Set(current).add(service));
    try {
      const details = await ApiService.getCorruptionDetails(service, scanId);
      if (requestEpoch !== resultEpochRef.current) return;
      if (details.length === 0) {
        setDetailErrors((current) => new Set(current).add(service));
        notifyError(t('management.corruption.errors.unsafeDetails'), undefined, {
          silent: true,
          logLabel: '[CorruptionManager] Saved service detail response was empty'
        });
        return;
      }
      setCorruptionDetails((current) => ({ ...current, [service]: details }));
    } catch (error: unknown) {
      if (requestEpoch !== resultEpochRef.current) return;
      setDetailErrors((current) => new Set(current).add(service));
      notifyError(
        t('management.corruption.errors.loadDetails', { service: getServiceDisplayName(service) }),
        error,
        { silent: true, logLabel: '[CorruptionManager] Failed to load service details' }
      );
    } finally {
      if (requestEpoch === resultEpochRef.current) {
        setLoadingDetailsServices((current) => {
          const next = new Set(current);
          next.delete(service);
          return next;
        });
      }
    }
  };

  const toggleDetails = (service: string) => {
    if (expandedService === service) {
      setExpandedService(null);
      return;
    }
    setExpandedService(service);
    if (!corruptionDetails[service]) void loadDetails(service);
  };

  const rowToggleHandlers = (service: string) => {
    const fromNestedControl = (target: EventTarget | null, currentTarget: EventTarget) => {
      if (!(target instanceof HTMLElement) || !(currentTarget instanceof HTMLElement)) return false;
      const control = target.closest(
        'button, input, a, label, [role="button"], [role="checkbox"], [role="listbox"], [role="combobox"]'
      );
      return control !== null && control !== currentTarget;
    };
    return {
      role: 'button' as const,
      tabIndex: 0,
      onClick: (event: React.MouseEvent) => {
        if (!fromNestedControl(event.target, event.currentTarget)) toggleDetails(service);
      },
      onKeyDown: (event: React.KeyboardEvent) => {
        if (
          (event.key === 'Enter' || event.key === ' ') &&
          !fromNestedControl(event.target, event.currentTarget)
        ) {
          event.preventDefault();
          toggleDetails(service);
        }
      }
    };
  };

  const requestServiceRemoval = (service: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }
    if (removalBlocked || (corruptionCounts[service] ?? 0) === 0) return;
    setPendingServiceRemoval(service);
  };

  const confirmServiceRemoval = async () => {
    if (!pendingServiceRemoval || removalBlocked || !scanId) return;
    const service = pendingServiceRemoval;
    setPendingServiceRemoval(null);
    markServiceRemovalStarting(service);
    try {
      const result = await ApiService.removeCorruptedChunks(service, scanId);
      if (result.operationId && !result.queued && !result.alreadyRunning) {
        addNotification(
          buildSeededRunningNotification(
            'corruption_removal',
            result.operationId,
            t(
              detectionMethod === 'structural'
                ? 'signalr.corruptionRemove.startingStructural'
                : 'signalr.corruptionRemove.starting',
              { service: getServiceDisplayName(service) }
            ),
            { service, detectionMethod }
          )
        );
      }
    } catch (error: unknown) {
      notifyError(
        t('management.corruption.errors.removeCorrupted', {
          service: getServiceDisplayName(service)
        }),
        error,
        { logLabel: '[CorruptionManager] Failed to remove service findings' }
      );
      clearServiceRemovalPending(service);
    }
  };

  const confirmRemoveAll = async () => {
    if (removalBlocked || !scanId) return;
    setPendingRemoveAll(false);
    markRemoveAllStarting('removeAll');
    try {
      const result = await ApiService.removeAllCorruptedChunks(scanId);
      if (!result.started) clearRemoveAllPending('removeAll');
    } catch (error: unknown) {
      notifyError(t('management.corruption.errors.removeAllCorrupted'), error, {
        logLabel: '[CorruptionManager] Failed to remove all findings'
      });
      clearRemoveAllPending('removeAll');
    }
  };

  const confirmRemoveSelected = async () => {
    if (removalBlocked || !scanId || selectedServices.length === 0) return;
    setPendingRemoveSelected(false);
    markRemoveSelectedStarting('removeSelected');
    try {
      const result = await ApiService.removeAllCorruptedChunks(scanId, selectedServices);
      if (!result.started) clearRemoveSelectedPending('removeSelected');
    } catch (error: unknown) {
      notifyError(t('management.corruption.errors.removeAllCorrupted'), error, {
        logLabel: '[CorruptionManager] Failed to remove selected findings'
      });
      clearRemoveSelectedPending('removeSelected');
    }
  };

  const controlSelectors = (
    <div className="mgmt-toolbar">
      <div className="flex flex-col gap-2 w-full sm:w-auto sm:flex-row sm:flex-wrap sm:items-center">
        <div className="w-full sm:w-60">
          <EnhancedDropdown
            variant="button"
            options={methodOptions}
            value={displayedDetectionMethod}
            onChange={handleMethodChange}
            disabled={isScanBusy || isAnyRemovalRunning || corruptionRemovalBusy}
            dropdownTitle={t('management.corruption.methodTitle')}
            triggerAriaLabel={t('management.corruption.methodAriaLabel')}
            compactMode={true}
            size="lg"
          />
        </div>
        {displayedDetectionMethod === 'repeated_miss' && (
          <div className="w-full sm:w-56">
            <EnhancedDropdown
              variant="button"
              options={thresholdOptions}
              value={String(missThreshold)}
              onChange={handleThresholdChange}
              disabled={isScanBusy || isAnyRemovalRunning || corruptionRemovalBusy}
              dropdownTitle={t('management.corruption.sensitivityTitle')}
              triggerAriaLabel={t('management.corruption.sensitivityAriaLabel')}
              compactMode={true}
              size="lg"
            />
          </div>
        )}
        {displayedDetectionMethod === 'repeated_miss' && (
          <div className="w-full sm:w-56">
            <EnhancedDropdown
              variant="button"
              options={lookbackOptions}
              value={String(lookbackDays)}
              onChange={handleLookbackChange}
              disabled={isScanBusy || isAnyRemovalRunning || corruptionRemovalBusy}
              dropdownTitle={t('management.corruption.evidenceLookbackTitle')}
              triggerAriaLabel={t('management.corruption.evidenceLookbackAriaLabel')}
              compactMode={true}
              size="lg"
            />
          </div>
        )}
      </div>
      {displayedDetectionMethod === 'structural' && (
        <p className="mgmt-scanmeta">{t('management.corruption.structuralScanHelp')}</p>
      )}
      {hasCachedResults && lastDetectionTime && !isScanBusy && !isLoading && (
        <p className="mgmt-scanmeta">
          {t('common.resultsFromPreviousScan')} · {formattedLastDetection} ·{' '}
          {t(
            detectionMethod === 'structural'
              ? 'management.corruption.methods.structural.label'
              : 'management.corruption.methods.repeatedMiss.label'
          )}
          {detectionMethod === 'repeated_miss' && (
            <> · {t('management.corruption.evidenceWindow', { days: lookbackDays })}</>
          )}
          {detectionMethod === 'structural' && scanCoverage && (
            <>
              {' '}
              ·{' '}
              {t('management.corruption.coverageSummary', {
                checked: formatCount(scanCoverage.filesChecked),
                skipped: formatCount(skippedCoverageTotal),
                errors: formatCount(scanCoverage.ioErrors)
              })}
            </>
          )}
        </p>
      )}
    </div>
  );

  const headerActions = (
    <div className="mgmt-corruption-header-actions flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
      {projection.total > 0 && (
        <Badge variant="neutral" className="badge-count badge-count-warning">
          {t('management.corruption.flaggedCount', {
            count: projection.total,
            formattedCount: formatCount(projection.total)
          })}
        </Badge>
      )}
      {selectedServices.length > 0 && (
        <Badge variant="neutral" className="badge-count">
          {selectedServices.length}
        </Badge>
      )}
      <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
        {(close) => (
          <>
            {displayedDetectionMethod === 'structural' ? (
              <>
                <ActionMenuItem
                  icon={<Search className="w-3.5 h-3.5" />}
                  disabled={scanBlocked}
                  onClick={() => {
                    void startScan('full');
                    close();
                  }}
                >
                  {t('management.corruption.fullScan')}
                </ActionMenuItem>
                <ActionMenuItem
                  icon={<Zap className="w-3.5 h-3.5" />}
                  disabled={scanBlocked}
                  onClick={() => {
                    void startScan('incremental');
                    close();
                  }}
                >
                  {t('management.corruption.incrementalScan')}
                </ActionMenuItem>
              </>
            ) : (
              <ActionMenuItem
                icon={<Search className="w-3.5 h-3.5" />}
                disabled={scanBlocked}
                onClick={() => {
                  void startScan();
                  close();
                }}
              >
                {t('common.scan')}
              </ActionMenuItem>
            )}
            <ActionMenuItem
              icon={<RefreshCw className="w-3.5 h-3.5" />}
              disabled={isRefreshing || isScanBusy || isAnyRemovalRunning || corruptionRemovalBusy}
              onClick={() => {
                void loadCachedData(true);
                close();
              }}
            >
              {t('common.load')}
            </ActionMenuItem>
            <ActionMenuDivider />
            <ActionMenuDangerItem
              icon={<Trash2 className="w-3.5 h-3.5" />}
              disabled={removalBlocked || selectedServices.length === 0}
              onClick={() => {
                setPendingRemoveSelected(true);
                close();
              }}
            >
              {t(
                detectionMethod === 'structural'
                  ? 'management.corruption.removeSelectedInvalid'
                  : 'management.corruption.removeSelectedSuspects'
              )}
            </ActionMenuDangerItem>
            <ActionMenuDangerItem
              icon={<Trash2 className="w-3.5 h-3.5" />}
              disabled={removalBlocked}
              onClick={() => {
                setPendingRemoveAll(true);
                close();
              }}
            >
              {startingRemoveAll
                ? t('management.corruption.removing')
                : t(
                    detectionMethod === 'structural'
                      ? 'management.corruption.removeAllInvalid'
                      : 'management.corruption.removeAllSuspects'
                  )}
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
        onToggle={() => setSectionExpanded((expanded) => !expanded)}
        badge={headerActions}
      >
        <div className="space-y-3">
          {controlSelectors}

          {isScanning && (
            <LoadingState
              message={t(
                displayedDetectionMethod !== 'structural'
                  ? 'management.corruption.scanningRepeatedMissMessage'
                  : isInitialBaselineBuild
                    ? 'management.corruption.buildingStructuralBaselineMessage'
                    : activeScanResumed && activeRunningScanAction === 'incremental'
                      ? 'management.corruption.resumingStructuralIncrementalMessage'
                      : activeRunningScanAction === 'incremental'
                        ? 'management.corruption.scanningStructuralIncrementalMessage'
                        : activeRunningScanAction === 'full'
                          ? 'management.corruption.scanningStructuralFullMessage'
                          : 'management.corruption.scanningStructuralMessage'
              )}
            />
          )}

          {cachedLoadFailed && !isLoading && (
            <Alert color="red">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                  <p className="font-medium">{t('management.corruption.errors.loadCachedData')}</p>
                  <p className="text-sm mt-1">
                    {t('management.corruption.errors.loadCachedRetry')}
                  </p>
                </div>
                <Button size="sm" onClick={() => void loadCachedData()}>
                  {t('common.retry')}
                </Button>
              </div>
            </Alert>
          )}

          {directoryMissing && (
            <Alert color="red">
              <div>
                <p className="font-medium">
                  {!logsExist && !cacheExist
                    ? requiresRepeatedMissResources
                      ? t('management.corruption.alerts.logsAndCacheMissing')
                      : t('management.corruption.alerts.cacheMissing')
                    : requiresRepeatedMissResources && !logsExist
                      ? t('management.corruption.alerts.logsMissing')
                      : t('management.corruption.alerts.cacheMissing')}
                </p>
                <p className="text-sm mt-1">
                  {t('management.corruption.alerts.directoryNotFound')}
                </p>
              </div>
            </Alert>
          )}

          {isReadOnly && !directoryMissing && (
            <Alert color="orange">
              <div>
                <p className="font-medium">
                  {requiresRepeatedMissResources && logsReadOnly && cacheReadOnly
                    ? t('management.corruption.alerts.logsAndCacheReadOnly')
                    : requiresRepeatedMissResources && logsReadOnly
                      ? t('management.corruption.alerts.logsReadOnly')
                      : t('management.corruption.alerts.cacheReadOnly')}
                </p>
                <p className="text-sm mt-1">
                  {t(
                    requiresRepeatedMissResources
                      ? 'management.corruption.alerts.requiresWriteAccess'
                      : 'management.corruption.alerts.requiresCacheWriteAccess'
                  )}{' '}
                  <code className="bg-themed-tertiary px-1 rounded">:ro</code>{' '}
                  {t('management.corruption.alerts.fromVolumeMounts')}
                </p>
              </div>
            </Alert>
          )}

          {requiresRepeatedMissResources && !isDockerAvailable && !hasRemovalPermissionIssue && (
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

          {showReadOnlyPlaceholder && (
            <ReadOnlyBadge
              message={
                directoryMissing
                  ? t('management.corruption.directoryMissing')
                  : isReadOnly
                    ? t('management.corruption.readOnly')
                    : t('management.corruption.dockerSocketRequired')
              }
            />
          )}

          {isLoading && !isScanning ? (
            <LoadingState message={t('management.corruption.loadingCachedData')} />
          ) : hasCachedResults && projection.rows.length > 0 ? (
            <div className="space-y-3">
              <div className="flex flex-wrap items-center gap-3">
                <Checkbox
                  checked={allVisibleSelected}
                  onChange={() => selection.setMany(serviceKeys, !allVisibleSelected)}
                  disabled={removalBlocked || serviceKeys.length === 0}
                  label={
                    allVisibleSelected
                      ? t('management.batchSelect.deselectAll')
                      : t('management.batchSelect.selectAll')
                  }
                />
              </div>
              <div className="mgmt-list">
                {projection.rows.map(({ service, count }) => {
                  const isExpanded = expandedService === service;
                  const isRemoving =
                    removingService === service || isServiceRemovalPending(service);
                  return (
                    <div key={`corruption-${service}`}>
                      <div
                        className="mgmt-row mgmt-row--interactive flex-wrap cursor-pointer"
                        {...rowToggleHandlers(service)}
                      >
                        <Checkbox
                          checked={selection.isSelected(service)}
                          onChange={() => selection.toggle(service)}
                          disabled={removalBlocked || isRemoving}
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
                        <div className="mgmt-row__actions mgmt-corruption-actions flex-wrap justify-end">
                          <Badge variant="neutral" className="badge-count badge-count-warning">
                            {t('management.corruption.flaggedCount', {
                              count,
                              formattedCount: formatCount(count)
                            })}
                          </Badge>
                          <Button
                            variant="filled"
                            color="gray"
                            size="sm"
                            className="mgmt-row__toggle"
                            onClick={() => toggleDetails(service)}
                            aria-label={
                              isExpanded
                                ? t('management.corruption.collapseDetails', {
                                    service: getServiceDisplayName(service)
                                  })
                                : t('management.corruption.expandDetails', {
                                    service: getServiceDisplayName(service)
                                  })
                            }
                            aria-expanded={isExpanded}
                          >
                            {isExpanded ? (
                              <ChevronUp className="w-4 h-4" />
                            ) : (
                              <ChevronDown className="w-4 h-4" />
                            )}
                          </Button>
                          <Button
                            onClick={() => requestServiceRemoval(service)}
                            awaitPermissions
                            loading={isRemoving}
                            disabled={removalBlocked || loadingDetailsServices.has(service)}
                            variant="filled"
                            color="red"
                            size="sm"
                            stableWidth
                          >
                            {isRemoving
                              ? t('management.corruption.removing')
                              : t(
                                  detectionMethod === 'structural'
                                    ? 'management.corruption.removeInvalid'
                                    : 'management.corruption.removeSuspect'
                                )}
                          </Button>
                        </div>
                      </div>
                      <CollapsibleRegion open={isExpanded} contentClassName="mgmt-row-detail">
                        {loadingDetailsServices.has(service) ? (
                          <LoadingState message={t('management.corruption.loadingDetails')} />
                        ) : detailErrors.has(service) ? (
                          <Alert color="red">
                            <div className="flex flex-wrap items-center justify-between gap-3">
                              <p className="text-sm">
                                {t('management.corruption.errors.loadDetails', {
                                  service: getServiceDisplayName(service)
                                })}
                              </p>
                              <Button size="sm" onClick={() => void loadDetails(service)}>
                                {t('common.retry')}
                              </Button>
                            </div>
                          </Alert>
                        ) : corruptionDetails[service]?.length > 0 ? (
                          <CorruptionChunkList chunks={corruptionDetails[service]} />
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
            </div>
          ) : hasCachedResults ? (
            <EmptyState
              icon={CircleCheck}
              title={t(
                detectionMethod === 'structural'
                  ? 'management.corruption.emptyStates.noCorrupted.structuralTitle'
                  : 'management.corruption.emptyStates.noCorrupted.repeatedMissTitle'
              )}
              subtitle={t(
                detectionMethod === 'structural'
                  ? 'management.corruption.emptyStates.noCorrupted.structuralSubtitle'
                  : 'management.corruption.emptyStates.noCorrupted.repeatedMissSubtitle'
              )}
            />
          ) : !isScanBusy && !cachedLoadFailed ? (
            <EmptyState
              icon={Search}
              title={t('management.corruption.emptyStates.noCachedData.title')}
              subtitle={t(
                detectionMethod === 'structural'
                  ? 'management.corruption.emptyStates.noCachedData.structuralSubtitle'
                  : 'management.corruption.emptyStates.noCachedData.repeatedMissSubtitle'
              )}
            />
          ) : null}
        </div>
      </AccordionSection>

      <Modal
        opened={pendingRemoveAll}
        onClose={() => setPendingRemoveAll(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>
              {t(
                detectionMethod === 'structural'
                  ? 'management.corruption.modal.removeAllStructuralTitle'
                  : 'management.corruption.modal.removeAllRepeatedMissTitle'
              )}
            </span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t(
              detectionMethod === 'structural'
                ? 'management.corruption.modal.confirmRemoveAllStructural'
                : 'management.corruption.modal.confirmRemoveAllRepeatedMiss',
              {
                services: projection.serviceTotal,
                candidates: formatCount(projection.total)
              }
            )}
          </p>
          <CorruptionRemovalWarning
            detectionMethod={detectionMethod}
            extraCautions={<li>{t('management.corruption.modal.removeAllAcrossAllServices')}</li>}
          />
          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingRemoveAll(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() => void confirmRemoveAll()}
              disabled={removalBlocked}
            >
              {t(
                detectionMethod === 'structural'
                  ? 'management.corruption.modal.removeAllStructuralConfirm'
                  : 'management.corruption.modal.removeAllRepeatedMissConfirm'
              )}
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        opened={pendingRemoveSelected}
        onClose={() => setPendingRemoveSelected(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>
              {t(
                detectionMethod === 'structural'
                  ? 'management.corruption.modal.removeSelectedStructuralTitle'
                  : 'management.corruption.modal.removeSelectedRepeatedMissTitle'
              )}
            </span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t(
              detectionMethod === 'structural'
                ? 'management.corruption.modal.confirmRemoveSelectedStructural'
                : 'management.corruption.modal.confirmRemoveSelectedRepeatedMiss',
              {
                services: selectedServices.length,
                candidates: formatCount(selectedTotal)
              }
            )}
          </p>
          <CorruptionRemovalWarning detectionMethod={detectionMethod} />
          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingRemoveSelected(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() => void confirmRemoveSelected()}
              disabled={removalBlocked || selectedServices.length === 0}
            >
              {t(
                detectionMethod === 'structural'
                  ? 'management.corruption.modal.removeSelectedStructuralConfirm'
                  : 'management.corruption.modal.removeSelectedRepeatedMissConfirm',
                { count: selectedServices.length }
              )}
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        opened={pendingServiceRemoval !== null}
        onClose={() => setPendingServiceRemoval(null)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>
              {t(
                detectionMethod === 'structural'
                  ? 'management.corruption.modal.structuralTitle'
                  : 'management.corruption.modal.repeatedMissTitle'
              )}
            </span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t(
              detectionMethod === 'structural'
                ? 'management.corruption.modal.confirmRemoveStructural'
                : 'management.corruption.modal.confirmRemoveRepeatedMiss',
              {
                service: pendingServiceRemoval
                  ? getServiceDisplayName(pendingServiceRemoval)
                  : undefined
              }
            )}
          </p>
          <CorruptionRemovalWarning
            detectionMethod={detectionMethod}
            extraCautions={
              <>
                <li>
                  {t('management.corruption.modal.validFilesRemain', {
                    service: pendingServiceRemoval
                      ? getServiceDisplayName(pendingServiceRemoval)
                      : undefined
                  })}
                </li>
                <li>
                  {t('management.corruption.modal.removesApproximately', {
                    count: corruptionCounts[pendingServiceRemoval ?? ''] ?? 0
                  })}
                </li>
              </>
            }
          />
          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setPendingServiceRemoval(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() => void confirmServiceRemoval()}
              disabled={removalBlocked || !pendingServiceRemoval}
            >
              {t(
                detectionMethod === 'structural'
                  ? 'management.corruption.modal.removeInvalidFiles'
                  : 'management.corruption.modal.removeSuspects'
              )}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default CorruptionManager;
