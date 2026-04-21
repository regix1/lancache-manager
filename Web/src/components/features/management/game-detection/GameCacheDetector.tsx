import React, { useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Database, Server, AlertTriangle } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import { AccordionSection } from '@components/ui/AccordionSection';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { useNotifications } from '@contexts/notifications';
import { waitForSignalRCompletion } from '@contexts/notifications/waitForSignalRCompletion';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useCancellableQueue } from '@/hooks/useCancellableQueue';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import { useConfig } from '@contexts/useConfig';
import { useDockerSocket } from '@contexts/useDockerSocket';
import { useSetupStatus } from '@contexts/useSetupStatus';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { useInvalidateImages } from '@components/common/ImageCacheContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { MANAGEMENT_STORAGE_KEYS } from '../sections/managementStorageKeys';
import { LoadingState, EmptyState, ReadOnlyBadge } from '@components/ui/ManagerCard';
import Badge from '@components/ui/Badge';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import CacheRemovalModal from '@components/modals/cache/CacheRemovalModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { getActiveGames, getActiveServices } from './cacheEntityFilters';
import {
  buildLoadedResultsSummary,
  CACHED_DETECTION_RELOAD_DELAY_MS,
  LOADED_RESULTS_SESSION_KEY,
  loadCachedDetectionSnapshot
} from './cacheDetectionData';
import {
  finalizeBulkRemovalNotification,
  runTrackedGameRemoval,
  runTrackedServiceRemoval,
  useCompletedRemovalPruning,
  useScheduledRemovalRefresh
} from './cacheRemovalHelpers';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';

interface GameCacheDetectorProps {
  mockMode?: boolean;
  isAdmin?: boolean;
  onDataRefresh?: () => void;
  refreshKey?: number;
}

const GameCacheDetector: React.FC<GameCacheDetectorProps> = ({
  mockMode = false,
  isAdmin = false,
  onDataRefresh,
  refreshKey = 0
}) => {
  const { t } = useTranslation();
  const { addNotification, updateNotification, notifications, isAnyRemovalRunning } =
    useNotifications();
  const { on, off } = useSignalR();
  const { config } = useConfig();
  const { isDockerAvailable } = useDockerSocket();
  const { cacheReadOnly, checkingPermissions } = useDirectoryPermissions();
  const invalidateImageCache = useInvalidateImages();
  const { setupStatus, refreshSetupStatus } = useSetupStatus();
  const hasProcessedLogs = setupStatus?.hasProcessedLogs ?? false;

  // Derive game detection state from notifications (standardized pattern)
  const activeGameDetectionNotification = notifications.find(
    (n) => n.type === 'game_detection' && n.status === 'running'
  );
  const isDetectionFromNotification = !!activeGameDetectionNotification;

  // Track local starting state for immediate UI feedback before SignalR events arrive
  const [isStartingDetection, setIsStartingDetection] = useState(false);
  // Track explicit "Load" button presses (handleLoadData). Init false so the initial
  // mount fetch (loadCachedGames) does NOT trigger the "Scanning database and cache files…"
  // banner — EvictedItemsList renders straight from props with no banner, and we mirror
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
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [scanType, setScanType] = useState<'full' | 'incremental' | 'load' | null>(null);
  // Derive datasources from config context (guaranteed non-null)
  const datasources =
    config.dataSources && config.dataSources.length > 0
      ? config.dataSources
      : [
          {
            name: 'default',
            cachePath: config.cachePath,
            logsPath: config.logsPath,
            cacheWritable: config.cacheWritable,
            logsWritable: config.logsWritable,
            enabled: true
          }
        ];
  const [selectedDatasource, setSelectedDatasource] = useState<string | null>(null);

  // Accordion state for Services, Games, and Evicted Games sections
  const [sectionExpanded, setSectionExpanded] = useState(() => {
    const saved = localStorage.getItem(MANAGEMENT_STORAGE_KEYS.GAME_CACHE_EXPANDED);
    return saved !== null ? saved === 'true' : false;
  });
  const [servicesExpanded, setServicesExpanded] = useState(true);
  const [gamesExpanded, setGamesExpanded] = useState(true);

  // "Remove All" state — sequential full-removal of every cached game and
  // service. Mirrors the per-item Remove button flow so each entity gets its
  // own log rewrite + cache-file delete + DB cleanup; a single failure does
  // not abort the remaining queue.
  const [showRemoveAllConfirm, setShowRemoveAllConfirm] = useState(false);
  const [removeAllRunning, setRemoveAllRunning] = useState(false);
  const [removeAllProgress, setRemoveAllProgress] = useState<{
    current: number;
    total: number;
    label: string;
  } | null>(null);

  useEffect(() => {
    localStorage.setItem(MANAGEMENT_STORAGE_KEYS.GAME_CACHE_EXPANDED, String(sectionExpanded));
  }, [sectionExpanded]);

  // Format last detection time with timezone awareness
  const formattedLastDetectionTime = useFormattedDateTime(lastDetectionTime);

  // Filter games and services by selected datasource.
  // The main list shows every entity that still has cache files on disk. Fully-
  // evicted entities (is_evicted=true OR zero cache files) are hidden here —
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
        console.error(`[GameCacheDetector] ${errorContext}:`, err);
        return null;
      }
    },
    [applyCachedDetectionSnapshot, clearCachedDetectionSnapshot, invalidateImageCache]
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
      if (mockMode) return;

      const snapshot = await syncCachedDetection('Failed to load cached games and services');
      if (!snapshot?.hasCachedResults) {
        return;
      }

      const alreadyShownThisSession = sessionStorage.getItem(LOADED_RESULTS_SESSION_KEY) === 'true';
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
        sessionStorage.setItem(LOADED_RESULTS_SESSION_KEY, 'true');
      }
    };

    loadCachedGames();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode, refreshKey]); // Re-run when mockMode or refreshKey changes

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

      // Handle game detection failure - ONLY if we were starting detection
      const gameDetectionFailedNotifs = notifications.filter(
        (n) => n.type === 'game_detection' && n.status === 'failed'
      );
      if (gameDetectionFailedNotifs.length > 0) {
        console.error('[GameCacheDetector] Game detection failed');
        setIsStartingDetection(false);
        setScanType(null);
        // Reset ref to allow future detection calls
        detectionInFlightRef.current = false;
        // Note: Operation state now handled by NotificationsContext
      }
    }
  }, [notifications, isStartingDetection, refreshSetupStatus, syncCachedDetection]);

  // Direct SignalR listener for GameDetectionComplete — reloads results regardless of who started the scan.
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

  // Listen for EvictionScanComplete — reloads detection results so evicted games surface immediately
  // without requiring a full Game Cache Detection scan or service restart.
  useEffect(() => {
    const handleEvictionScanComplete = () => {
      scheduleCachedDetectionSync('Failed to reload after eviction scan');
    };

    on('EvictionScanComplete', handleEvictionScanComplete);
    return () => {
      off('EvictionScanComplete', handleEvictionScanComplete);
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
      setGames([]);
      setServices([]);
      setLastDetectionTime(null); // Clear previous detection time when starting new scan

      try {
        // Start background detection - SignalR will send GameDetectionStarted event
        await ApiService.startGameCacheDetection(forceRefresh);
        // Note: NotificationsContext will create a notification via SignalR (GameDetectionStarted event)
        // and recovery is handled by recoverGameDetection
      } catch (err: unknown) {
        const errorMsg =
          (err instanceof Error ? err.message : String(err)) ||
          t('management.gameDetection.failedToStartDetection');
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
  const allExpanded = servicesExpanded && gamesExpanded;

  // Sequential per-item cache-removal queue. The per-item endpoints (remove
  // game/service from cache) return 202 Accepted without an operationId, so
  // we capture it from the matching `*Started` SignalR event and hand it to
  // the hook via `ctx.setOperationId` so the cascade cancel can abort the
  // in-flight op. The hook owns the refs + cascade useEffect; we only supply
  // per-item API selection and the finalize transition.
  type CacheQueueEntry =
    | { kind: 'service'; service: ServiceCacheInfo }
    | { kind: 'game'; game: GameCacheInfo };
  const { run: runCacheRemoval } = useCancellableQueue<CacheQueueEntry>({
    onSettled: () => {
      onDataRefresh?.();
    }
  });

  const handleRemoveAllCached = useCallback(async () => {
    setShowRemoveAllConfirm(false);
    if (!isAdmin) return;

    const services = [...filteredServices];
    const games = [...filteredGames];
    const total = services.length + games.length;
    if (total === 0) return;

    setRemoveAllRunning(true);

    await runCacheRemoval({
      items: [
        ...services.map((service) => ({ kind: 'service' as const, service })),
        ...games.map((game) => ({ kind: 'game' as const, game }))
      ],
      openNotification: () =>
        addNotification({
          type: 'bulk_removal',
          status: 'running',
          message: t('management.sections.data.gameCacheRemoveAllStarting', {
            total,
            defaultValue: 'Removing 0 of {{total}} cached items...'
          }),
          progress: 0,
          // No operationId → handleCancel special-cases bulk_removal
          details: {}
        }),
      onItemStart: (entry, index, _total, notifId) => {
        const label =
          entry.kind === 'service'
            ? entry.service.service_name
            : (entry.game.game_name ?? String(entry.game.game_app_id));
        setRemoveAllProgress({ current: index, total, label });
        updateNotification(notifId, {
          message: t('management.sections.data.gameCacheRemoveAllProgress', {
            current: index,
            total,
            label
          }),
          progress: Math.floor(((index - 1) / total) * 100)
        });
      },
      processItem: async (entry, ctx) => {
        if (entry.kind === 'service') {
          const serviceName = entry.service.service_name;
          const waitPromise = waitForSignalRCompletion<
            { serviceName?: string; operationId?: string },
            { serviceName?: string }
          >({
            signalR: { on, off },
            completeEvent: 'ServiceRemovalComplete',
            startedEvent: 'ServiceRemovalStarted',
            match: (payload) => payload?.serviceName === serviceName,
            onStartedCapture: (payload) =>
              payload?.serviceName === serviceName && typeof payload.operationId === 'string'
                ? { opId: payload.operationId }
                : null,
            onOperationIdCaptured: (opId) => ctx.setOperationId(opId),
            signal: ctx.signal
          });
          await ApiService.removeServiceFromCache(serviceName);
          await waitPromise;
        } else {
          const game = entry.game;
          const gameAppId = game.game_app_id;
          const gameName = game.game_name;
          const isEpic = game.service === 'epicgames' && !!gameName;
          const epicAppId = game.epic_app_id ?? undefined;
          let currentOperationId: string | null = null;
          const matchesGame = (payload?: {
            gameAppId?: number | null;
            epicAppId?: string | null;
            gameName?: string;
            operationId?: string;
          }): boolean => {
            if (!payload) {
              return false;
            }

            if (currentOperationId) {
              return payload.operationId === currentOperationId;
            }

            if (isEpic) {
              if (epicAppId && payload.epicAppId === epicAppId) {
                return true;
              }

              return payload.gameName === gameName;
            }

            return payload.gameAppId === gameAppId;
          };
          const waitPromise = waitForSignalRCompletion<
            {
              gameAppId?: number | null;
              epicAppId?: string | null;
              gameName?: string;
              operationId?: string;
            },
            {
              gameAppId?: number | null;
              epicAppId?: string | null;
              gameName?: string;
              operationId?: string;
            }
          >({
            signalR: { on, off },
            completeEvent: 'GameRemovalComplete',
            startedEvent: 'GameRemovalStarted',
            match: matchesGame,
            onStartedCapture: (payload) =>
              matchesGame(payload) && typeof payload.operationId === 'string'
                ? { opId: payload.operationId }
                : null,
            onOperationIdCaptured: (opId) => {
              currentOperationId = opId;
              ctx.setOperationId(opId);
            },
            signal: ctx.signal
          });

          if (isEpic) {
            const response = await ApiService.removeEpicGameFromCache(gameName);
            currentOperationId = response.operationId;
            ctx.setOperationId(response.operationId);
          } else {
            const response = await ApiService.removeGameFromCache(gameAppId);
            currentOperationId = response.operationId;
            ctx.setOperationId(response.operationId);
          }
          await waitPromise;
        }
      },
      finalize: ({ id, succeeded, failed, cancelled, total }) => {
        setRemoveAllRunning(false);
        setRemoveAllProgress(null);
        finalizeBulkRemovalNotification({
          id,
          succeeded,
          failed,
          total,
          cancelled,
          t,
          updateNotification,
          text: {
            completeKey: 'management.sections.data.gameCacheRemoveAllComplete',
            completeDefaultValue: 'Removed {{count}} cached items',
            partialFailureKey: 'management.sections.data.gameCacheRemoveAllCompleteWithFailures',
            partialFailureDefaultValue: 'Removed {{count}} cached items, but {{failed}} failed',
            cancelledKey: 'management.sections.data.gameCacheRemoveAllCancelled',
            cancelledDefaultValue: 'Bulk removal cancelled after {{count}} items',
            cancelledWithFailuresKey:
              'management.sections.data.gameCacheRemoveAllCancelledWithFailures',
            cancelledWithFailuresDefaultValue:
              'Bulk removal cancelled after {{count}} items, with {{failed}} failures'
          }
        });
      }
    });
  }, [
    filteredGames,
    filteredServices,
    isAdmin,
    runCacheRemoval,
    addNotification,
    updateNotification,
    on,
    off,
    t
  ]);

  // Help content
  // Header actions - scan buttons + expand/collapse all
  const headerActions = (
    <div className="flex items-center gap-2 flex-wrap">
      {hasResults && (
        <Button variant="default" size="sm" onClick={handleExpandCollapseAll}>
          {allExpanded
            ? t('management.gameDetection.collapseAll')
            : t('management.gameDetection.expandAll')}
        </Button>
      )}

      {hasResults && isAdmin && (
        <Tooltip content={t('management.sections.data.gameCacheRemoveAll', 'Remove All')}>
          <Button
            onClick={() => setShowRemoveAllConfirm(true)}
            disabled={
              loading || mockMode || checkingPermissions || removeAllRunning || isAnyRemovalRunning
            }
            loading={removeAllRunning}
            variant="filled"
            color="red"
            size="sm"
          >
            {t('management.sections.data.gameCacheRemoveAll', 'Remove All')}
          </Button>
        </Tooltip>
      )}

      <Tooltip content={t('management.gameDetection.loadPreviousResults')}>
        <Button
          onClick={handleLoadData}
          disabled={loading || mockMode || checkingPermissions}
          variant="default"
          size="sm"
        >
          {loading && scanType === 'load' ? <LoadingSpinner inline size="sm" /> : t('common.load')}
        </Button>
      </Tooltip>

      <Tooltip
        content={
          !hasProcessedLogs
            ? t('management.gameDetection.processLogsFirst')
            : t('management.gameDetection.quickScan')
        }
      >
        <Button
          onClick={handleIncrementalScan}
          disabled={loading || mockMode || checkingPermissions || !hasProcessedLogs}
          variant="default"
          size="sm"
        >
          {loading && scanType === 'incremental' ? (
            <LoadingSpinner inline size="sm" />
          ) : (
            t('management.gameDetection.quick')
          )}
        </Button>
      </Tooltip>

      <Tooltip
        content={
          !hasProcessedLogs
            ? t('management.gameDetection.processLogsFirst')
            : t('management.gameDetection.fullScan')
        }
      >
        <Button
          onClick={handleFullScan}
          disabled={loading || mockMode || checkingPermissions || !hasProcessedLogs}
          variant="filled"
          color="blue"
          size="sm"
        >
          {loading && scanType === 'full' ? (
            <LoadingSpinner inline size="sm" />
          ) : (
            t('management.gameDetection.fullScanButton')
          )}
        </Button>
      </Tooltip>
    </div>
  );

  return (
    <>
      <Card>
        <div className="space-y-4">
          <AccordionSection
            title={t('management.gameDetection.title')}
            icon={HardDrive}
            iconColor="var(--theme-icon-blue)"
            isExpanded={sectionExpanded}
            onToggle={() => setSectionExpanded((prev) => !prev)}
            badge={
              hasResults ? (
                <Badge variant="info">{filteredGames.length + filteredServices.length}</Badge>
              ) : undefined
            }
          >
            <div className="space-y-4">
              {/* Action toolbar */}
              <div className="flex flex-wrap items-center justify-end gap-2">{headerActions}</div>

              {/* Read-Only Warning */}
              {cacheReadOnly && (
                <>
                  <Alert color="orange" className="mb-2">
                    <div>
                      <p className="font-medium">
                        {t('management.gameDetection.alerts.cacheReadOnly.title')}
                      </p>
                      <p className="text-sm mt-1">
                        {t('management.gameDetection.alerts.cacheReadOnly.description')}
                      </p>
                    </div>
                  </Alert>
                  <ReadOnlyBadge />
                </>
              )}

              {/* Datasource Filter */}
              {!cacheReadOnly && datasources.length > 1 && (
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
                    prefix={t('management.gameDetection.filterPrefix')}
                  />
                </div>
              )}

              {/* Loading State */}
              {loading && (
                <LoadingState
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

              {!cacheReadOnly && !loading && (
                <>
                  {/* Previous Results Badge */}
                  {lastDetectionTime && hasResults && (
                    <Alert color="blue">
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-medium">
                          {t('common.resultsFromPreviousScan')}
                        </span>
                        <span className="text-xs text-themed-muted">
                          {formattedLastDetectionTime}
                        </span>
                      </div>
                    </Alert>
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
                          variant="subtle"
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
                      iconColor="var(--theme-icon-cyan)"
                      isExpanded={servicesExpanded}
                      onToggle={() => setServicesExpanded(!servicesExpanded)}
                    >
                      <ServicesList
                        services={filteredServices}
                        isAnyRemovalRunning={isAnyRemovalRunning}
                        isAdmin={isAdmin}
                        cacheReadOnly={cacheReadOnly}
                        dockerSocketAvailable={isDockerAvailable}
                        checkingPermissions={checkingPermissions}
                        onRemoveService={handleServiceRemoveClick}
                      />
                    </AccordionSection>
                  )}

                  {/* Games Section (Accordion) */}
                  {filteredGames.length > 0 && (
                    <AccordionSection
                      title={t('management.gameDetection.gamesSection')}
                      count={filteredGames.length}
                      icon={Database}
                      iconColor="var(--theme-icon-emerald)"
                      isExpanded={gamesExpanded}
                      onToggle={() => setGamesExpanded(!gamesExpanded)}
                    >
                      <GamesList
                        games={filteredGames}
                        isAnyRemovalRunning={isAnyRemovalRunning}
                        isAdmin={isAdmin}
                        cacheReadOnly={cacheReadOnly}
                        dockerSocketAvailable={isDockerAvailable}
                        checkingPermissions={checkingPermissions}
                        onRemoveGame={handleRemoveClick}
                      />
                    </AccordionSection>
                  )}

                  {/* Empty State — shown only when no scan results (games/services) exist */}
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
                            variant="subtle"
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
        </div>
      </Card>

      {/* Game Removal Confirmation Modal */}
      <CacheRemovalModal
        target={gameToRemove ? { type: 'game', data: gameToRemove } : null}
        onClose={() => setGameToRemove(null)}
        onConfirm={confirmRemoval}
      />

      {/* Service Removal Confirmation Modal */}
      <CacheRemovalModal
        target={serviceToRemove ? { type: 'service', data: serviceToRemove } : null}
        onClose={() => setServiceToRemove(null)}
        onConfirm={confirmServiceRemoval}
      />

      {/* Remove All Cached Games/Services Confirmation Modal */}
      <Modal
        opened={showRemoveAllConfirm}
        onClose={() => setShowRemoveAllConfirm(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>
              {t(
                'management.sections.data.gameCacheRemoveAllConfirmTitle',
                'Remove all cached games & services?'
              )}
            </span>
          </div>
        }
      >
        <div className="space-y-4">
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
          <div className="flex justify-end space-x-3 pt-2">
            <Button variant="default" onClick={() => setShowRemoveAllConfirm(false)}>
              {t('management.sections.data.evictionRemoveConfirmCancel')}
            </Button>
            <Button variant="filled" color="red" onClick={handleRemoveAllCached}>
              {t('management.sections.data.gameCacheRemoveAll', 'Remove All')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Remove-all progress indicator — rendered at the bottom so it is visible
          no matter which accordion the user is viewing. */}
      {removeAllRunning && removeAllProgress && (
        <div className="fixed bottom-4 right-4 z-50 max-w-sm bg-themed-secondary border border-themed-secondary rounded-lg p-3 shadow-lg">
          <p className="text-sm text-themed-primary">
            {t('management.sections.data.gameCacheRemoveAllProgress', {
              current: removeAllProgress.current,
              total: removeAllProgress.total,
              label: removeAllProgress.label,
              defaultValue: 'Removing {{current}} of {{total}} — {{label}}'
            })}
          </p>
        </div>
      )}
    </>
  );
};

export default GameCacheDetector;
