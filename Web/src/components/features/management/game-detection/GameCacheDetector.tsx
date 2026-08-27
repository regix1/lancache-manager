import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import {
  HardDrive,
  Database,
  Server,
  RefreshCw,
  Search,
  Zap,
  ChevronsDownUp,
  ChevronsUpDown,
  Trash2
} from 'lucide-react';
import ApiService from '@services/api.service';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionHeaderActions, SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import { ActionMenuItem, ActionMenuDangerItem, ActionMenuDivider } from '@components/ui/ActionMenu';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler } from '@/hooks/useErrorHandler';
import { getErrorMessage } from '@utils/error';
import { buildSeededRunningNotification } from '@contexts/notifications/seedOperationNotification';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useBulkRemoval, type BulkQueueEntry } from '@contexts/BulkRemovalContext';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useCacheRemovalActive } from '@hooks/useCacheRemovalActive';
import { useDiskObjectCapability } from '@hooks/useDiskObjectCapability';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { DiskObjectActionGate } from '@components/features/management/DiskObjectActionGate';
import { useSelectionSet, type SelectionSet } from '@/hooks/useSelectionSet';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import { useConfig } from '@contexts/useConfig';
import { useSetupStatus } from '@contexts/useSetupStatus';
import { useDirectoryPermissionsContext } from '@contexts/useDirectoryPermissionsContext';
import { useInvalidateImages } from '@components/common/ImageCacheContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import CardDirectoryNotice from '@components/features/management/CardDirectoryNotice';
import { MANAGEMENT_STORAGE_KEYS } from '../sections/managementStorageKeys';
import { LoadingState, EmptyState } from '@components/ui/ManagerCard';
import '../managementSectionContent.css';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import CacheRemovalModal from '@components/modals/cache/CacheRemovalModal';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { NginxReopenActionGate } from '@components/features/management/NginxReopenActionGate';
import { getActiveGames, getActiveServices } from './cacheEntityFilters';
import { getGameUniqueId } from './gameUtils';
import { classifyGameFromCacheInfo, shouldPinOperationIdFromResponse } from './gameRemovalEntity';
import {
  buildLoadedResultsSummary,
  CACHED_DETECTION_RELOAD_DELAY_MS,
  LOADED_RESULTS_SESSION_KEY,
  loadCachedDetectionSnapshot
} from './cacheDetectionData';
import {
  runTrackedGameRemoval,
  runTrackedServiceRemoval,
  useCompletedRemovalPruning,
  useScheduledRemovalRefresh
} from './cacheRemovalHelpers';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';
import { isCardDiskActionBlocked, resolveCardNotice } from '@utils/cardDirectoryNotice';
import { resolveDatasources } from '@utils/datasources';
import { getNginxReopenGateForEntities } from '@utils/nginxReopenAvailability';
import { sessionStore } from '@utils/storage';
import { translateRecoveryStage } from '@utils/stageKeyMessage';
import { useSectionExpanded } from '@hooks/useSectionExpanded';

interface GameCacheDetectorProps {
  mockMode?: boolean;
  isAdmin?: boolean;
  onDataRefresh?: () => void;
  refreshKey?: number;
}

/** How often the removal confirmation asks the running cache-file count where it has got to. */
const COUNT_POLL_INTERVAL_MS = 1000;

const GameCacheDetector: React.FC<GameCacheDetectorProps> = ({
  mockMode = false,
  isAdmin = false,
  onDataRefresh,
  refreshKey = 0
}) => {
  const { t } = useTranslation();
  const { addNotification, updateNotification, notifications } = useNotifications();
  const { notifyError } = useErrorHandler();
  const { on, off, isConnected } = useSignalR();
  const { config } = useConfig();
  const { cacheReadOnly, logsReadOnly, cacheExist, logsExist, checkingPermissions } =
    useDirectoryPermissionsContext();
  const invalidateImageCache = useInvalidateImages();
  const { setupStatus, refreshSetupStatus } = useSetupStatus();
  const hasProcessedLogs = setupStatus?.hasProcessedLogs ?? false;

  // Derive game detection state from notifications (standardized pattern)
  const isDetectionFromNotification = useOperationBusy({ types: ['game_detection'] });
  // A QUEUED detection scan also disables the scan buttons (re-click would only
  // queue a duplicate), without flipping the blocking body loader that the
  // running flag drives.
  const isDetectionQueued = useOperationBusy({ types: ['game_detection'], status: 'waiting' });

  // Any running/queued removal in the game-cache domain (single, evicted, or bulk)
  // disables Remove All - single removes and Remove All gate together. Removals
  // from unrelated cards (corruption/logs/cache) still enqueue.
  const isCacheRemovalActive = useCacheRemovalActive();
  // Bulk removal needs one resolved cache-key scheme for every enabled datasource so a
  // cross-datasource request cannot partially succeed.
  const { available: diskObjectsAvailable, denialReason: diskObjectDenialReason } =
    useDiskObjectCapability();

  // Track local starting state for immediate UI feedback before SignalR events arrive
  const [isStartingDetection, setIsStartingDetection] = useState(false);
  // Track explicit "Load" button presses (handleLoadData). Init false so the initial
  // mount fetch (loadCachedGames) does NOT trigger the "Scanning database and cache files…"
  // banner - EvictedItemsList renders straight from props with no banner, and we mirror
  // that here for a simultaneous-paint UX. The Load button still flips this true on click.
  const [isLoadingData, setIsLoadingData] = useState(false);
  // Ref to prevent duplicate API calls (handles rapid button clicks before state updates)
  const detectionInFlightRef = useRef(false);
  // Combined loading state: notification says running, starting phase, or explicit Load click.
  const loading = isDetectionFromNotification || isStartingDetection || isLoadingData;
  const [games, setGames] = useState<GameCacheInfo[]>([]);
  const [services, setServices] = useState<ServiceCacheInfo[]>([]);
  const [gameToRemove, setGameToRemove] = useState<GameCacheInfo | null>(null);
  const [serviceToRemove, setServiceToRemove] = useState<ServiceCacheInfo | null>(null);

  // The removal confirmation states what the removal will actually delete, counted against the
  // disk while the dialog is open. Null means there is no number to confirm against yet.
  const [cacheFileCount, setCacheFileCount] = useState<number | null>(null);
  const [countStage, setCountStage] = useState<{
    key: string;
    context: Record<string, string | number | boolean | null>;
  } | null>(null);
  const [countFailed, setCountFailed] = useState(false);
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [scanType, setScanType] = useState<'full' | 'incremental' | 'load' | null>(null);
  const datasources = resolveDatasources(config);
  const [selectedDatasource, setSelectedDatasource] = useState<string | null>(null);

  // Accordion state for Services, Games, and Evicted Games sections
  const [sectionExpanded, setSectionExpanded] = useSectionExpanded(
    MANAGEMENT_STORAGE_KEYS.GAME_CACHE_EXPANDED,
    false
  );
  useAccordionGroupItem('storage-game-detection', sectionExpanded, () =>
    setSectionExpanded((prev) => !prev)
  );
  const [servicesExpanded, setServicesExpanded] = useState(true);
  useAccordionGroupItem('storage-game-detection-services', servicesExpanded, () =>
    setServicesExpanded(!servicesExpanded)
  );
  const [gamesExpanded, setGamesExpanded] = useState(true);
  useAccordionGroupItem('storage-game-detection-games', gamesExpanded, () =>
    setGamesExpanded(!gamesExpanded)
  );

  // "Remove All" state - sequential full-removal of every cached game and
  // service. Mirrors the per-item Remove button flow so each entity gets its
  // own log rewrite + cache-file delete + DB cleanup; a single failure does
  // not abort the remaining queue.
  const [showRemoveAllConfirm, setShowRemoveAllConfirm] = useState(false);
  const [removeAllRunning, setRemoveAllRunning] = useState(false);
  const [isLoadingInitialCache, setIsLoadingInitialCache] = useState(() => !mockMode);

  // Format last detection time with timezone awareness
  const formattedLastDetectionTime = useFormattedDateTime(lastDetectionTime);

  // Filter games and services by selected datasource.
  // The main list shows every entity that still has cache files on disk. Fully-
  // evicted entities (is_evicted=true OR zero cache files) are hidden here -
  // the Evicted Items card owns those. Partially-evicted entities (some
  // downloads evicted but cache files still present) MUST appear in BOTH
  // lists: the main card shows what's still on disk; Evicted Items shows the
  // evicted downloads so the user can clean them up without losing the cached
  // portion.
  // Note: Items with empty/missing datasources (legacy data) are shown regardless of filter.
  const activeGames = getActiveGames(games);
  const filteredGames = selectedDatasource
    ? activeGames.filter(
        (g) => !g.datasources?.length || g.datasources.includes(selectedDatasource)
      )
    : activeGames;
  const activeServices = getActiveServices(services);
  const filteredServices = selectedDatasource
    ? activeServices.filter(
        (s) => !s.datasources?.length || s.datasources.includes(selectedDatasource)
      )
    : activeServices;

  // Auto-collapse sections only on the empty→populated transition (fresh scan or
  // initial load). This avoids overriding the user's manual toggle when subsequent
  // updates (game removals, SignalR reloads, partial refreshes) change the counts.
  const prevServicesLenRef = useRef(0);
  const prevGamesLenRef = useRef(0);
  useEffect(() => {
    if (prevServicesLenRef.current === 0 && filteredServices.length > 0) {
      setServicesExpanded(filteredServices.length <= 10);
    }
    if (prevGamesLenRef.current === 0 && filteredGames.length > 0) {
      setGamesExpanded(filteredGames.length <= 10);
    }
    prevServicesLenRef.current = filteredServices.length;
    prevGamesLenRef.current = filteredGames.length;
  }, [filteredServices.length, filteredGames.length]);

  const applyCachedDetectionSnapshot = useCallback(
    (snapshot: {
      games: GameCacheInfo[];
      services: ServiceCacheInfo[];
      lastDetectionTime: string | null;
    }) => {
      setGames(snapshot.games);
      setServices(snapshot.services);
      setLastDetectionTime(snapshot.lastDetectionTime);
    },
    []
  );

  const clearCachedDetectionSnapshot = useCallback(() => {
    setGames([]);
    setServices([]);
    setLastDetectionTime(null);
  }, []);

  const syncCachedDetection = useCallback(
    async (errorContext: string, options?: { invalidateImages?: boolean }) => {
      try {
        const snapshot = await loadCachedDetectionSnapshot();

        if (snapshot.hasCachedResults) {
          applyCachedDetectionSnapshot(snapshot);
        } else {
          clearCachedDetectionSnapshot();
        }

        if (options?.invalidateImages) {
          invalidateImageCache?.();
        }

        return snapshot;
      } catch (err) {
        // Shared background-resync helper (initial load + post-removal/scan auto-refresh); every
        // caller already has a working fallback (empty state or stale list), so a failure here is
        // explicit background noise rather than a blocking error.
        notifyError(t('management.gameDetection.errors.syncFailed'), err, {
          silent: true,
          logLabel: `[GameCacheDetector] ${errorContext}`
        });
        return null;
      }
    },
    [
      applyCachedDetectionSnapshot,
      clearCachedDetectionSnapshot,
      invalidateImageCache,
      notifyError,
      t
    ]
  );
  const scheduleCachedDetectionReload = useTimeoutCallback(CACHED_DETECTION_RELOAD_DELAY_MS);
  const scheduleRemovalRefresh = useScheduledRemovalRefresh();

  const scheduleCachedDetectionSync = useCallback(
    (errorContext: string, invalidateImages = false) => {
      scheduleCachedDetectionReload(() => {
        void syncCachedDetection(errorContext, { invalidateImages });
      });
    },
    [scheduleCachedDetectionReload, syncCachedDetection]
  );

  // Load cached games and services from backend on mount and when refreshKey changes
  useEffect(() => {
    const loadCachedGames = async () => {
      if (mockMode) {
        setIsLoadingInitialCache(false);
        return;
      }

      setIsLoadingInitialCache(true);
      try {
        const snapshot = await syncCachedDetection('Failed to load cached games and services');
        if (!snapshot?.hasCachedResults) {
          return;
        }

        const alreadyShownThisSession = sessionStore.getItem(LOADED_RESULTS_SESSION_KEY) === 'true';
        const isActivelyScanning = loading || scanType === 'full' || scanType === 'incremental';
        const resultsSummary = buildLoadedResultsSummary(snapshot);

        if (!isActivelyScanning && !alreadyShownThisSession && resultsSummary) {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: t('management.gameDetection.loadedPreviousResults', {
              results: resultsSummary
            }),
            details: { notificationType: 'info' }
          });
          sessionStore.setItem(LOADED_RESULTS_SESSION_KEY, 'true');
        }
      } finally {
        setIsLoadingInitialCache(false);
      }
    };

    void loadCachedGames();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode, refreshKey]); // Re-run when mockMode or refreshKey changes

  // A dropped socket can swallow the completion event of a scan or removal that
  // finished while it was down; resync the cached detection snapshot on reconnect.
  // Skip in mock mode: the global socket stays connected there, so a resync would pull
  // real cache data into the mock view.
  useReconnectRefetch(isConnected, () => {
    if (!mockMode) syncCachedDetection('Failed to refresh cached results on reconnect');
  });

  // Listen for notification events from SignalR (consolidated)
  useCompletedRemovalPruning({
    notifications,
    setGames,
    setServices
  });

  useEffect(() => {
    // Handle database reset completion
    const databaseResetNotifs = notifications.filter(
      (n) => n.type === 'database_reset' && n.status === 'completed'
    );
    if (databaseResetNotifs.length > 0) {
      setGames([]);
      setServices([]);
      refreshSetupStatus();
    }

    // Handle log processing completion
    const logProcessingNotifs = notifications.filter(
      (n) => n.type === 'log_processing' && n.status === 'completed'
    );
    if (logProcessingNotifs.length > 0) {
      refreshSetupStatus();
    }

    // Handle game detection completion - ONLY if we were starting detection
    if (isStartingDetection) {
      const gameDetectionNotifs = notifications.filter(
        (n) => n.type === 'game_detection' && n.status === 'completed'
      );
      if (gameDetectionNotifs.length > 0) {
        // Load results BEFORE clearing loading state so the UI transitions
        // directly from "loading" to "results" without an empty-games flash.
        const loadResults = async () => {
          try {
            await syncCachedDetection('Failed to load detection results', {
              invalidateImages: true
            });
          } finally {
            // Clear loading state AFTER results are applied so there is no
            // intermediate render with games=[] and loading=false.
            setIsStartingDetection(false);
            setScanType(null);
            // Reset ref to allow future detection calls
            detectionInFlightRef.current = false;
          }
        };
        loadResults();
      }

      // Handle game detection failure or cancellation - ONLY if we were starting detection.
      // A cancelled scan is terminal exactly like a failed one: without it the card would
      // stay in its loading state and detectionInFlightRef would block the next scan.
      const gameDetectionEndedNotifs = notifications.filter(
        (n) => n.type === 'game_detection' && (n.status === 'failed' || n.status === 'cancelled')
      );
      if (gameDetectionEndedNotifs.length > 0) {
        if (gameDetectionEndedNotifs.some((n) => n.status === 'failed')) {
          console.error('[GameCacheDetector] Game detection failed');
        }
        setIsStartingDetection(false);
        setScanType(null);
        // Reset ref to allow future detection calls
        detectionInFlightRef.current = false;
        // Note: Operation state now handled by NotificationsContext
      }
    }
  }, [notifications, isStartingDetection, refreshSetupStatus, syncCachedDetection]);

  // Direct SignalR listener for GameDetectionComplete - reloads results regardless of who started the scan.
  // This handles the case where an external process (e.g., a scheduled scan or another browser tab)
  // triggers a scan while isStartingDetection is false, so the notification-based flow above would not reload.
  useEffect(() => {
    const handleDetectionComplete = () => {
      scheduleCachedDetectionSync('Failed to reload results after external scan', true);
    };

    on('GameDetectionComplete', handleDetectionComplete);
    return () => {
      off('GameDetectionComplete', handleDetectionComplete);
    };
  }, [on, off, scheduleCachedDetectionSync]);

  // Listen for GameRemovalComplete to immediately remove the game from the list
  useEffect(() => {
    const handleGameRemovalComplete = () => {
      scheduleCachedDetectionSync('Failed to reload after game removal');
    };

    on('GameRemovalComplete', handleGameRemovalComplete);
    on('EvictionRemovalComplete', handleGameRemovalComplete);
    return () => {
      off('GameRemovalComplete', handleGameRemovalComplete);
      off('EvictionRemovalComplete', handleGameRemovalComplete);
    };
  }, [on, off, scheduleCachedDetectionSync]);

  // Reload after an eviction scan or successful cache clear so newly evicted games surface
  // immediately without requiring a full Game Cache Detection scan or service restart.
  useEffect(() => {
    const handleEvictionStateChanged = () => {
      scheduleCachedDetectionSync('Failed to reload after cache eviction state changed');
    };

    on('CacheClearingComplete', handleEvictionStateChanged);
    on('EvictionScanComplete', handleEvictionStateChanged);
    return () => {
      off('CacheClearingComplete', handleEvictionStateChanged);
      off('EvictionScanComplete', handleEvictionStateChanged);
    };
  }, [on, off, scheduleCachedDetectionSync]);

  const startDetection = useCallback(
    async (forceRefresh: boolean, scanTypeLabel: 'full' | 'incremental') => {
      if (mockMode) {
        const errorMsg = t('management.gameDetection.detectionDisabledMockMode');
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMsg,
          details: { notificationType: 'error' }
        });
        return;
      }

      // Prevent duplicate API calls - check ref first (handles rapid clicks before state updates)
      if (detectionInFlightRef.current || loading) {
        console.warn('[GameCacheDetector] Detection already in progress, ignoring duplicate call');
        return;
      }

      // Set ref immediately to block any concurrent calls
      detectionInFlightRef.current = true;

      setIsStartingDetection(true);
      setScanType(scanTypeLabel);

      try {
        // Start background detection - SignalR will send GameDetectionStarted event
        const result = await ApiService.startGameCacheDetection(forceRefresh);
        // Wait-queue model: queued/deduplicated responses must not seed a running card -
        // the OperationWaiting event (or the already-visible card) owns the UI.
        if (shouldPinOperationIdFromResponse(result)) {
          addNotification(
            buildSeededRunningNotification(
              'game_detection',
              result.operationId,
              t('signalr.gameDetect.starting.default')
            )
          );
        }
      } catch (err: unknown) {
        const errorMsg =
          getErrorMessage(err) || t('management.gameDetection.failedToStartDetection');
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMsg,
          details: { notificationType: 'error' }
        });
        console.error('Detection error:', err);
        setIsStartingDetection(false);
        setScanType(null);
        // Reset ref on error so user can retry
        detectionInFlightRef.current = false;
      }
    },
    [mockMode, loading, t, addNotification]
  );

  const handleFullScan = useCallback(() => startDetection(true, 'full'), [startDetection]);
  const handleIncrementalScan = useCallback(
    () => startDetection(false, 'incremental'),
    [startDetection]
  );

  const handleLoadData = async () => {
    if (mockMode) return;

    setScanType('load');
    setIsLoadingData(true);

    try {
      const snapshot = await syncCachedDetection('Failed to load previous results');
      if (!snapshot) {
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.gameDetection.failedToLoadPreviousResults'),
          details: { notificationType: 'error' }
        });
        return;
      }

      const resultsSummary = snapshot ? buildLoadedResultsSummary(snapshot) : null;

      addNotification({
        type: 'generic',
        status: 'completed',
        message: resultsSummary
          ? t('management.gameDetection.loadedFromPreviousScan', {
              results: resultsSummary
            })
          : t('management.gameDetection.noPreviousResults'),
        details: { notificationType: resultsSummary ? 'success' : 'info' }
      });
    } catch (err) {
      console.error('[GameCacheDetector] Failed to load data:', err);
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.gameDetection.failedToLoadPreviousResults'),
        details: { notificationType: 'error' }
      });
    } finally {
      setIsLoadingData(false);
      setScanType(null);
    }
  };

  const handleRemoveClick = (game: GameCacheInfo) => {
    if (!isAdmin) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('common.fullAuthRequired'),
        details: { notificationType: 'error' }
      });
      return;
    }
    setGameToRemove(game);
  };

  const confirmRemoval = async () => {
    if (!gameToRemove) return;

    // Close modal immediately - progress shown via notifications
    const game = gameToRemove;
    setGameToRemove(null);
    await runTrackedGameRemoval({
      game,
      t,
      addNotification,
      updateNotification,
      scheduleRemovalRefresh,
      onDataRefresh
    });
  };

  const handleServiceRemoveClick = (service: ServiceCacheInfo) => {
    if (!isAdmin) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('common.fullAuthRequired'),
        details: { notificationType: 'error' }
      });
      return;
    }
    setServiceToRemove(service);
  };

  // Counting walks every URL the service ever logged and stat-probes each slice, so it runs as a
  // tracked operation with progress rather than a request that blocks. Closing the dialog cancels
  // it, and the confirm button stays disabled until the real number lands, so the number a user
  // answers is the number the removal will delete rather than the last scan's snapshot.
  useEffect(() => {
    if (!gameToRemove && !serviceToRemove) return;

    // One start call per removal route, chosen the same way the removal itself is routed, so the
    // count always walks the entity the confirm button will delete.
    const startCount = (game: GameCacheInfo | null) => {
      if (!game) {
        return ApiService.startServiceCacheFileCount(serviceToRemove!.service_name);
      }

      const entity = classifyGameFromCacheInfo(game);
      if (entity.kind === 'epicGame') {
        return ApiService.startEpicGameCacheFileCount(game.game_name);
      }
      if (entity.kind === 'namedGame') {
        return ApiService.startNamedGameCacheFileCount(entity.service, entity.gameName);
      }
      return ApiService.startGameCacheFileCount(entity.gameAppId);
    };

    let stopped = false;
    let settled = false;
    let startedOperationId: string | null = null;
    let pollTimer: ReturnType<typeof setTimeout> | null = null;

    setCacheFileCount(null);
    setCountStage(null);
    setCountFailed(false);

    const poll = async (operationId: string) => {
      try {
        const status = await ApiService.getCacheFileCountStatus(operationId);
        if (stopped) return;

        if (status.isProcessing) {
          if (status.stageKey) {
            setCountStage({ key: status.stageKey, context: status.context ?? {} });
          }
          pollTimer = setTimeout(() => void poll(operationId), COUNT_POLL_INTERVAL_MS);
          return;
        }

        settled = true;
        // Only the count this dialog started can answer for it. A number left behind by an
        // earlier count is exactly the stale figure this whole change exists to stop showing.
        if (status.operationId === operationId && status.cacheFilesFound !== undefined) {
          setCacheFileCount(status.cacheFilesFound);
        } else {
          setCountFailed(true);
        }
      } catch (err) {
        console.error('[GameCacheDetector] Cache file count failed:', err);
        if (!stopped) {
          settled = true;
          setCountFailed(true);
        }
      }
    };

    void (async () => {
      try {
        const started = await startCount(gameToRemove);
        startedOperationId = started.operationId;
        if (stopped) {
          void ApiService.cancelOperation(started.operationId);
          return;
        }
        void poll(started.operationId);
      } catch (err) {
        console.error('[GameCacheDetector] Could not start the cache file count:', err);
        if (!stopped) {
          settled = true;
          setCountFailed(true);
        }
      }
    })();

    return () => {
      stopped = true;
      if (pollTimer) clearTimeout(pollTimer);
      if (startedOperationId && !settled) {
        void ApiService.cancelOperation(startedOperationId);
      }
    };
  }, [gameToRemove, serviceToRemove]);

  // Translated at render rather than inside the effect, so switching language cannot restart a
  // walk that has been running for minutes.
  const countStatusMessage = countFailed
    ? t('modals.cacheRemoval.countFailed')
    : translateRecoveryStage(countStage?.key, countStage?.context, 'modals.cacheRemoval.counting');

  const confirmServiceRemoval = async () => {
    if (!serviceToRemove) return;

    // Close modal immediately - progress shown via notifications
    const service = serviceToRemove;
    setServiceToRemove(null);
    await runTrackedServiceRemoval({
      service,
      t,
      addNotification,
      updateNotification,
      scheduleRemovalRefresh,
      onDataRefresh
    });
  };

  // Expand/Collapse all handler
  const handleExpandCollapseAll = () => {
    const allExpanded = servicesExpanded && gamesExpanded;
    setServicesExpanded(!allExpanded);
    setGamesExpanded(!allExpanded);
  };

  const hasResults = filteredGames.length > 0 || filteredServices.length > 0;
  const actionsPending = isLoadingInitialCache || !hasResults;
  const showBlockingLoader =
    isDetectionFromNotification || isStartingDetection || (isLoadingData && !hasResults);
  const allExpanded = servicesExpanded && gamesExpanded;

  // Sequential per-item cache-removal queue. The app-root BulkRemovalProvider
  // owns the run loop, per-item API/SignalR pipeline (capturing each op's id for
  // cascade cancel), AbortController plumbing, and the finalize transition; this
  // component only builds the item list. Progress lives on the bulk_removal
  // notification. The run survives an in-app tab switch because the provider
  // never unmounts.
  const { runCacheRemoval } = useBulkRemoval();

  // Client-only multi-select for the Services (S3) and Games (S4) lists. Keyed
  // by the same list keys CacheEntityList uses (service_name / getGameUniqueId).
  const servicesSelection: SelectionSet<string> = useSelectionSet<string>();
  const gamesSelection: SelectionSet<string> = useSelectionSet<string>();

  const [confirmRemoveSelected, setConfirmRemoveSelected] = useState(false);

  // Selected items derived from the CURRENTLY FILTERED lists, so a stale key can
  // never contribute to the count or the removal batch even before the prune
  // effect below runs.
  const selectedServiceItems = useMemo(
    () => filteredServices.filter((s) => servicesSelection.isSelected(s.service_name)),
    [filteredServices, servicesSelection]
  );
  const selectedGameItems = useMemo(
    () => filteredGames.filter((g) => gamesSelection.isSelected(getGameUniqueId(g))),
    [filteredGames, gamesSelection]
  );
  // Services + games remove TOGETHER in one confirmed run (mirrors Remove All),
  // so the shared Remove Selected control counts and clears both sets at once.
  const selectedCombinedCount = selectedServiceItems.length + selectedGameItems.length;
  const selectedNginxReopenGate = getNginxReopenGateForEntities(datasources, [
    ...selectedServiceItems,
    ...selectedGameItems
  ]);
  const allNginxReopenGate = getNginxReopenGateForEntities(datasources, [
    ...filteredServices,
    ...filteredGames
  ]);
  const selectedNginxReopenMessage = selectedNginxReopenGate.messageKey
    ? t(selectedNginxReopenGate.messageKey)
    : '';
  const allNginxReopenMessage = allNginxReopenGate.messageKey
    ? t(allNginxReopenGate.messageKey)
    : '';
  const directoryNoticeConditions = {
    cacheWrite: true,
    cacheRead: false,
    logsWrite: true,
    nginx: true
  };
  const directoryNoticeLiveState = {
    cacheReadOnly,
    logsReadOnly,
    cacheExist,
    logsExist,
    checkingPermissions,
    nginxReopenGate: allNginxReopenGate
  };
  const directoryNotice = resolveCardNotice(directoryNoticeConditions, directoryNoticeLiveState);
  const diskActionBlocked = isCardDiskActionBlocked(
    directoryNoticeConditions,
    directoryNoticeLiveState
  );

  // Prune selection keys that dropped out of the visible list on refresh/scan so
  // a removed item never lingers in the set (plan §6). Keyed on a stable
  // signature of the visible keys to avoid running the effect every render
  // (filteredServices/filteredGames are recomputed fresh each render).
  const serviceKeySignature = filteredServices.map((s) => s.service_name).join('|');
  const gameKeySignature = filteredGames.map((g) => getGameUniqueId(g)).join('|');
  useEffect(() => {
    const valid = new Set(filteredServices.map((s) => s.service_name));
    const stale = [...servicesSelection.selected].filter((k) => !valid.has(k));
    if (stale.length > 0) servicesSelection.setMany(stale, false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serviceKeySignature]);
  useEffect(() => {
    const valid = new Set(filteredGames.map((g) => getGameUniqueId(g)));
    const stale = [...gamesSelection.selected].filter((k) => !valid.has(k));
    if (stale.length > 0) gamesSelection.setMany(stale, false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gameKeySignature]);

  // Adapter objects passed to CacheEntityList (via ServicesList/GamesList). The
  // select-all checkbox uses allSelected/setMany over the filtered keys.
  const servicesSelectionProp = useMemo(
    () => ({
      isSelected: servicesSelection.isSelected,
      onToggle: servicesSelection.toggle,
      allSelected: servicesSelection.allSelected,
      setMany: servicesSelection.setMany
    }),
    [
      servicesSelection.isSelected,
      servicesSelection.toggle,
      servicesSelection.allSelected,
      servicesSelection.setMany
    ]
  );
  const gamesSelectionProp = useMemo(
    () => ({
      isSelected: gamesSelection.isSelected,
      onToggle: gamesSelection.toggle,
      allSelected: gamesSelection.allSelected,
      setMany: gamesSelection.setMany
    }),
    [
      gamesSelection.isSelected,
      gamesSelection.toggle,
      gamesSelection.allSelected,
      gamesSelection.setMany
    ]
  );

  // Remove the selected services AND games in ONE runCacheRemoval batch (one
  // confirm, one progress card), exactly like Remove All but scoped to the
  // selected subset. Both sets clear on settle.
  const handleRemoveSelected = useCallback(async () => {
    setConfirmRemoveSelected(false);
    if (!isAdmin || !selectedNginxReopenGate.available) return;
    const items: BulkQueueEntry[] = [
      ...selectedServiceItems.map((service) => ({ kind: 'service' as const, service })),
      ...selectedGameItems.map((game) => ({ kind: 'game' as const, game }))
    ];
    if (items.length === 0) return;
    await runCacheRemoval(items, {
      onRunningChange: setRemoveAllRunning,
      onSettled: () => {
        servicesSelection.clear();
        gamesSelection.clear();
        onDataRefresh?.();
      }
    });
  }, [
    isAdmin,
    selectedServiceItems,
    selectedGameItems,
    runCacheRemoval,
    servicesSelection,
    gamesSelection,
    onDataRefresh,
    selectedNginxReopenGate.available
  ]);

  const handleRemoveAllCached = useCallback(async () => {
    setShowRemoveAllConfirm(false);
    if (!isAdmin || !allNginxReopenGate.available) return;

    const services = [...filteredServices];
    const games = [...filteredGames];
    const total = services.length + games.length;
    if (total === 0) return;

    const items: BulkQueueEntry[] = [
      ...services.map((service) => ({ kind: 'service' as const, service })),
      ...games.map((game) => ({ kind: 'game' as const, game }))
    ];

    await runCacheRemoval(items, {
      onRunningChange: setRemoveAllRunning,
      onSettled: () => {
        onDataRefresh?.();
      }
    });
  }, [
    allNginxReopenGate.available,
    filteredGames,
    filteredServices,
    isAdmin,
    runCacheRemoval,
    onDataRefresh
  ]);

  // Help content
  // Header actions - scan buttons + expand/collapse all
  const headerActions = (
    <SectionHeaderActions>
      {selectedCombinedCount > 0 && (
        <SectionHeaderChip variant="neutral" className="badge-count">
          {selectedCombinedCount}
        </SectionHeaderChip>
      )}
      <SectionActionsMenu label={t('management.actions.menuLabel')}>
        {(close) => (
          <>
            <ActionMenuItem
              icon={
                allExpanded ? (
                  <ChevronsDownUp className="w-3.5 h-3.5" />
                ) : (
                  <ChevronsUpDown className="w-3.5 h-3.5" />
                )
              }
              disabled={actionsPending || !sectionExpanded}
              onClick={() => {
                handleExpandCollapseAll();
                close();
              }}
            >
              {allExpanded
                ? t('management.gameDetection.collapseAll')
                : t('management.gameDetection.expandAll')}
            </ActionMenuItem>

            <ActionMenuItem
              icon={<RefreshCw className="w-3.5 h-3.5" />}
              disabled={loading || mockMode}
              onClick={() => {
                handleLoadData();
                close();
              }}
            >
              {t('common.load')}
            </ActionMenuItem>

            <ActionMenuItem
              icon={<Zap className="w-3.5 h-3.5" />}
              disabled={loading || isDetectionQueued || mockMode || !hasProcessedLogs}
              onClick={() => {
                handleIncrementalScan();
                close();
              }}
            >
              {t('management.gameDetection.quick')}
            </ActionMenuItem>

            <ActionMenuItem
              icon={<Search className="w-3.5 h-3.5" />}
              disabled={loading || isDetectionQueued || mockMode || !hasProcessedLogs}
              onClick={() => {
                handleFullScan();
                close();
              }}
            >
              {t('management.gameDetection.fullScanButton')}
            </ActionMenuItem>

            {isAdmin && (
              <>
                <ActionMenuDivider />
                <DiskObjectActionGate
                  available={diskObjectsAvailable}
                  tooltip={
                    diskObjectDenialReason ?? t('management.capability.diskObjectsUnavailable')
                  }
                  position="left"
                  className="block w-full"
                >
                  <NginxReopenActionGate
                    available={selectedNginxReopenGate.available}
                    tooltip={selectedNginxReopenMessage}
                    position="left"
                    className="block w-full"
                  >
                    <ActionMenuDangerItem
                      icon={<Trash2 className="w-3.5 h-3.5" />}
                      disabled={
                        selectedCombinedCount === 0 ||
                        loading ||
                        mockMode ||
                        diskActionBlocked ||
                        checkingPermissions ||
                        isCacheRemovalActive ||
                        !diskObjectsAvailable ||
                        !selectedNginxReopenGate.available
                      }
                      onClick={() => {
                        setConfirmRemoveSelected(true);
                        close();
                      }}
                    >
                      {t('management.batchSelect.removeSelectedLabel')}
                    </ActionMenuDangerItem>
                  </NginxReopenActionGate>
                </DiskObjectActionGate>

                <DiskObjectActionGate
                  available={diskObjectsAvailable}
                  tooltip={
                    diskObjectDenialReason ?? t('management.capability.diskObjectsUnavailable')
                  }
                  position="left"
                  className="block w-full"
                >
                  <NginxReopenActionGate
                    available={allNginxReopenGate.available}
                    tooltip={allNginxReopenMessage}
                    position="left"
                    className="block w-full"
                  >
                    <ActionMenuDangerItem
                      icon={<Trash2 className="w-3.5 h-3.5" />}
                      disabled={
                        actionsPending ||
                        loading ||
                        mockMode ||
                        diskActionBlocked ||
                        checkingPermissions ||
                        isCacheRemovalActive ||
                        removeAllRunning ||
                        !diskObjectsAvailable ||
                        !allNginxReopenGate.available
                      }
                      onClick={() => {
                        setShowRemoveAllConfirm(true);
                        close();
                      }}
                    >
                      {t('management.sections.data.gameCacheRemoveAll')}
                    </ActionMenuDangerItem>
                  </NginxReopenActionGate>
                </DiskObjectActionGate>
              </>
            )}
          </>
        )}
      </SectionActionsMenu>
    </SectionHeaderActions>
  );

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.gameDetection.help.aboutTitle')}>
        {t('management.gameDetection.subtitle')}
      </HelpSection>
    </HelpPopover>
  );

  return (
    <>
      <AccordionSection
        title={t('management.gameDetection.title')}
        shortTitle={t('management.gameDetection.titleShort')}
        titleAccessory={helpAccessory}
        count={hasResults ? filteredGames.length + filteredServices.length : undefined}
        icon={HardDrive}
        isExpanded={sectionExpanded}
        onToggle={() => setSectionExpanded((prev) => !prev)}
        badge={headerActions}
      >
        <div className="space-y-3">
          <CardDirectoryNotice notice={directoryNotice} />

          {/* Datasource Filter */}
          {cacheExist && datasources.length > 1 && (
            <div className="flex justify-end">
              <EnhancedDropdown
                variant="button"
                options={[
                  {
                    value: '',
                    label: t('management.gameDetection.placeholders.allDatasources')
                  },
                  ...datasources.map(
                    (ds): DropdownOption => ({
                      value: ds.name,
                      label: ds.name
                    })
                  )
                ]}
                value={selectedDatasource || ''}
                onChange={(value) => setSelectedDatasource(value || null)}
                placeholder={t('management.gameDetection.placeholders.allDatasources')}
                cleanStyle
                size="sm"
                prefix={t('management.gameDetection.filterPrefix')}
              />
            </div>
          )}

          {/* Loading State */}
          {showBlockingLoader && (
            <LoadingState
              variant="spinner"
              message={
                datasources.length > 1
                  ? t('management.gameDetection.scanningMultipleDatasources', {
                      count: datasources.length
                    })
                  : t('management.gameDetection.scanningSingle')
              }
              submessage={t('management.gameDetection.scanningNote')}
            />
          )}

          {cacheExist && (
            <>
              {/* Previous Results Summary */}
              {lastDetectionTime && hasResults && (
                <div className="space-y-2">
                  <p className="mgmt-scanmeta">
                    {t('common.resultsFromPreviousScan')} · {formattedLastDetectionTime}
                  </p>
                  <div className="mgmt-stat-grid">
                    <div className="mgmt-stat">
                      <p className="mgmt-stat__label caps-label caps-label--sm">
                        {t('management.gameDetection.servicesSection')}
                      </p>
                      <p className="mgmt-stat__value">{filteredServices.length}</p>
                    </div>
                    <div className="mgmt-stat">
                      <p className="mgmt-stat__label caps-label caps-label--sm">
                        {t('management.gameDetection.gamesSection')}
                      </p>
                      <p className="mgmt-stat__value">{filteredGames.length}</p>
                    </div>
                  </div>
                </div>
              )}

              {/* Filter indicator */}
              {selectedDatasource && hasResults && (
                <Alert color="blue">
                  <div className="flex items-center justify-between">
                    <span className="text-sm">
                      {t('management.gameDetection.filteredBy', {
                        datasource: selectedDatasource,
                        gameCount: filteredGames.length,
                        serviceCount: filteredServices.length
                      })}
                    </span>
                    <Button
                      variant="filled"
                      color="secondary"
                      size="xs"
                      onClick={() => setSelectedDatasource(null)}
                    >
                      {t('management.gameDetection.clearFilter')}
                    </Button>
                  </div>
                </Alert>
              )}

              {/* Services Section (Accordion) */}
              {filteredServices.length > 0 && (
                <AccordionSection
                  title={t('management.gameDetection.servicesSection')}
                  count={filteredServices.length}
                  icon={Server}
                  isExpanded={servicesExpanded}
                  onToggle={() => setServicesExpanded(!servicesExpanded)}
                  surface="well"
                >
                  <ServicesList
                    services={filteredServices}
                    isAdmin={isAdmin}
                    datasourceConfigs={datasources}
                    onRemoveService={handleServiceRemoveClick}
                    diskActionBlocked={diskActionBlocked}
                    selection={servicesSelectionProp}
                  />
                </AccordionSection>
              )}

              {/* Games Section (Accordion) */}
              {filteredGames.length > 0 && (
                <AccordionSection
                  title={t('management.gameDetection.gamesSection')}
                  count={filteredGames.length}
                  icon={Database}
                  isExpanded={gamesExpanded}
                  onToggle={() => setGamesExpanded(!gamesExpanded)}
                  surface="well"
                >
                  <GamesList
                    games={filteredGames}
                    isAdmin={isAdmin}
                    datasourceConfigs={datasources}
                    onRemoveGame={handleRemoveClick}
                    diskActionBlocked={diskActionBlocked}
                    selection={gamesSelectionProp}
                  />
                </AccordionSection>
              )}

              {/* Empty State - shown only when no scan results (games/services) exist */}
              {filteredGames.length === 0 && filteredServices.length === 0 && !loading && (
                <EmptyState
                  title={
                    selectedDatasource
                      ? t('management.gameDetection.emptyState.noGamesServicesDatasource', {
                          datasource: selectedDatasource
                        })
                      : t('management.gameDetection.emptyState.noGamesServices')
                  }
                  subtitle={
                    !hasProcessedLogs
                      ? t('management.gameDetection.emptyState.processLogsFirst')
                      : t('management.gameDetection.emptyState.clickFullScan')
                  }
                  action={
                    selectedDatasource ? (
                      <Button
                        variant="filled"
                        color="secondary"
                        size="sm"
                        onClick={() => setSelectedDatasource(null)}
                      >
                        {t('management.gameDetection.clearFilter')}
                      </Button>
                    ) : undefined
                  }
                />
              )}
            </>
          )}
        </div>
      </AccordionSection>

      {/* Game Removal Confirmation Modal */}
      <CacheRemovalModal
        target={gameToRemove ? { type: 'game', data: gameToRemove } : null}
        onClose={() => setGameToRemove(null)}
        onConfirm={confirmRemoval}
        fileCount={cacheFileCount ?? undefined}
        statusMessage={countStatusMessage}
        confirmDisabled={cacheFileCount === null}
      />

      {/* Service Removal Confirmation Modal */}
      <CacheRemovalModal
        target={serviceToRemove ? { type: 'service', data: serviceToRemove } : null}
        onClose={() => setServiceToRemove(null)}
        onConfirm={confirmServiceRemoval}
        fileCount={cacheFileCount ?? undefined}
        statusMessage={countStatusMessage}
        confirmDisabled={cacheFileCount === null}
      />

      {/* Remove All Cached Games/Services Confirmation Modal */}
      <ConfirmationModal
        opened={showRemoveAllConfirm}
        onClose={() => setShowRemoveAllConfirm(false)}
        onConfirm={handleRemoveAllCached}
        title={t('management.sections.data.gameCacheRemoveAllConfirmTitle')}
        confirmLabel={t('management.sections.data.gameCacheRemoveAll')}
      >
        <p className="text-themed-secondary">
          {t('management.sections.data.gameCacheRemoveAllConfirmMessage', {
            count: filteredGames.length + filteredServices.length,
            defaultValue:
              'This will permanently delete cache files, log entries, and database records for all {{count}} currently-cached games and services. Items are removed one at a time. The operation cannot be undone.'
          })}
        </p>
        <Alert color="yellow">
          <p className="text-sm">
            {t('management.sections.data.gameCacheRemoveAllConfirmWarning', {
              defaultValue:
                'This is irreversible. Any client that re-downloads these games will have to pull the full payload from upstream, not the cache.'
            })}
          </p>
        </Alert>
      </ConfirmationModal>

      {/* Remove Selected (combined services + games) Confirmation Modal */}
      <ConfirmationModal
        opened={confirmRemoveSelected}
        onClose={() => setConfirmRemoveSelected(false)}
        onConfirm={handleRemoveSelected}
        title={t('management.batchSelect.confirmTitle')}
        confirmLabel={t('management.batchSelect.removeSelected', {
          count: selectedCombinedCount
        })}
        confirmColor="red"
      >
        <p className="text-themed-secondary">
          {t('management.batchSelect.confirmBodyCacheFiles', { count: selectedCombinedCount })}
        </p>
      </ConfirmationModal>
    </>
  );
};

export default GameCacheDetector;
