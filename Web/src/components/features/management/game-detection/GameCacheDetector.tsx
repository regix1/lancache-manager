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
import { useNotifications, NOTIFICATION_IDS } from '@contexts/notifications';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useConfig } from '@contexts/useConfig';
import { useDockerSocket } from '@contexts/useDockerSocket';
import { useSetupStatus } from '@contexts/useSetupStatus';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { useInvalidateImages } from '@components/common/ImageCacheContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { LoadingState, EmptyState, ReadOnlyBadge } from '@components/ui/ManagerCard';
import Badge from '@components/ui/Badge';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import CacheRemovalModal from '@components/modals/cache/CacheRemovalModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
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
    const saved = localStorage.getItem('management-game-cache-expanded');
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
    localStorage.setItem('management-game-cache-expanded', String(sectionExpanded));
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
  const activeGames = games.filter((g) => !g.is_evicted && (g.cache_files_found ?? 0) > 0);
  const filteredGames = selectedDatasource
    ? activeGames.filter(
        (g) => !g.datasources?.length || g.datasources.includes(selectedDatasource)
      )
    : activeGames;
  const activeServices = services.filter(
    (s: ServiceCacheInfo) => !s.is_evicted && (s.cache_files_found ?? 0) > 0
  );
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

  // Load cached games and services from backend on mount and when refreshKey changes
  useEffect(() => {
    const loadCachedGames = async () => {
      if (mockMode) return;

      try {
        const result = await ApiService.getCachedGameDetection();
        if (result.hasCachedResults) {
          // Load games if array exists and has items
          if (result.games && result.games.length > 0) {
            setGames(result.games);
          } else {
            setGames([]);
          }

          // Load services if array exists and has items
          if (result.services && result.services.length > 0) {
            setServices(result.services);
          } else {
            setServices([]);
          }

          // Store the last detection time
          if (result.lastDetectionTime) {
            setLastDetectionTime(result.lastDetectionTime);
          }

          // Only show "Loaded previous results" notification once per session
          // This avoids the annoying repeated notification every time user visits the tab
          const sessionKey = 'gameCacheDetector_loadedNotificationShown';
          const alreadyShownThisSession = sessionStorage.getItem(sessionKey) === 'true';
          const isActivelyScanning = loading || scanType === 'full' || scanType === 'incremental';

          if (!isActivelyScanning && !alreadyShownThisSession) {
            const parts = [];
            if (result.totalGamesDetected && result.totalGamesDetected > 0) {
              parts.push(
                `${result.totalGamesDetected} game${result.totalGamesDetected !== 1 ? 's' : ''}`
              );
            }
            if (result.totalServicesDetected && result.totalServicesDetected > 0) {
              parts.push(
                `${result.totalServicesDetected} service${result.totalServicesDetected !== 1 ? 's' : ''}`
              );
            }

            if (parts.length > 0) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.gameDetection.loadedPreviousResults', {
                  results: parts.join(' and ')
                }),
                details: { notificationType: 'info' }
              });
              sessionStorage.setItem(sessionKey, 'true');
            }
          }
        } else {
          // No cached results - clear the display
          setGames([]);
          setServices([]);
          setLastDetectionTime(null);
        }
      } catch (err) {
        console.error('[GameCacheDetector] Failed to load cached games and services:', err);
      }
    };

    loadCachedGames();
    if (refreshKey === 0) {
      // Note: datasources are now derived from ConfigContext (no need to load separately)
      // Note: hasProcessedLogs is now provided by useSetupStatus context (anonymous endpoint)
      // Note: Recovery is now handled by NotificationsContext's recoverGameDetection
      // which queries the backend and creates the notification on page load
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode, refreshKey]); // Re-run when mockMode or refreshKey changes

  // Listen for notification events from SignalR (consolidated)
  useEffect(() => {
    // Handle completed game removals — remove from local state when backend confirms done
    const gameRemovalNotifs = notifications.filter(
      (n) => n.type === 'game_removal' && n.status === 'completed'
    );
    gameRemovalNotifs.forEach((notif) => {
      const gameAppId = notif.details?.gameAppId;
      const gameName = notif.details?.gameName;

      if (gameAppId) {
        setGames((prev) => prev.filter((g) => g.game_app_id !== gameAppId));
      } else if (gameName) {
        setGames((prev) => prev.filter((g) => g.game_name !== gameName));
      }
    });

    // Handle completed service removals
    const serviceRemovalNotifs = notifications.filter(
      (n) => n.type === 'service_removal' && n.status === 'completed'
    );
    serviceRemovalNotifs.forEach((notif) => {
      const serviceName = notif.details?.service;
      if (!serviceName) return;

      setServices((prev) => prev.filter((s) => s.service_name !== serviceName));
    });

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
            const result = await ApiService.getCachedGameDetection();
            if (result.hasCachedResults) {
              // Set games if array exists and has items (don't rely on totalGamesDetected being truthy)
              if (result.games && result.games.length > 0) {
                setGames(result.games);
              }
              // Set services if array exists and has items
              if (result.services && result.services.length > 0) {
                setServices(result.services);
              }
              if (result.lastDetectionTime) {
                setLastDetectionTime(result.lastDetectionTime);
              }
            }
            // Bust the image cache so newly-discovered game banners load fresh
            invalidateImageCache?.();
          } catch (err) {
            console.error('[GameCacheDetector] Failed to load detection results:', err);
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
  }, [notifications, isStartingDetection, invalidateImageCache, refreshSetupStatus]);

  // Direct SignalR listener for GameDetectionComplete — reloads results regardless of who started the scan.
  // This handles the case where an external process (e.g., a scheduled scan or another browser tab)
  // triggers a scan while isStartingDetection is false, so the notification-based flow above would not reload.
  useEffect(() => {
    const handleDetectionComplete = () => {
      // Small delay to allow backend to finish writing cached results before we fetch
      setTimeout(() => {
        ApiService.getCachedGameDetection()
          .then((result) => {
            if (result.hasCachedResults) {
              if (result.games && result.games.length > 0) {
                setGames(result.games);
              }
              if (result.services && result.services.length > 0) {
                setServices(result.services);
              }
              if (result.lastDetectionTime) {
                setLastDetectionTime(result.lastDetectionTime);
              }
              invalidateImageCache?.();
            }
          })
          .catch((err) => {
            console.error('[GameCacheDetector] Failed to reload results after external scan:', err);
          });
      }, 500);
    };

    on('GameDetectionComplete', handleDetectionComplete);
    return () => {
      off('GameDetectionComplete', handleDetectionComplete);
    };
  }, [on, off, invalidateImageCache]);

  // Listen for GameRemovalComplete to immediately remove the game from the list
  useEffect(() => {
    const handleGameRemovalComplete = () => {
      setTimeout(() => {
        ApiService.getCachedGameDetection()
          .then((result) => {
            if (result.hasCachedResults) {
              if (result.games) {
                setGames(result.games);
              }
              if (result.services) {
                setServices(result.services);
              }
            }
          })
          .catch((err) => {
            console.error('[GameCacheDetector] Failed to reload after game removal:', err);
          });
      }, 500);
    };

    on('GameRemovalComplete', handleGameRemovalComplete);
    on('EvictionRemovalComplete', handleGameRemovalComplete);
    return () => {
      off('GameRemovalComplete', handleGameRemovalComplete);
      off('EvictionRemovalComplete', handleGameRemovalComplete);
    };
  }, [on, off]);

  // Listen for EvictionScanComplete — reloads detection results so evicted games surface immediately
  // without requiring a full Game Cache Detection scan or service restart.
  useEffect(() => {
    const handleEvictionScanComplete = () => {
      // Small delay to allow backend to finish writing cached results before we fetch
      setTimeout(() => {
        ApiService.getCachedGameDetection()
          .then((result) => {
            if (result.hasCachedResults) {
              if (result.games) {
                setGames(result.games);
              }
              if (result.services) {
                setServices(result.services);
              }
            }
          })
          .catch((err) => {
            console.error('[GameCacheDetector] Failed to reload after eviction scan:', err);
          });
      }, 500);
    };

    on('EvictionScanComplete', handleEvictionScanComplete);
    return () => {
      off('EvictionScanComplete', handleEvictionScanComplete);
    };
  }, [on, off]);

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
      const result = await ApiService.getCachedGameDetection();
      if (result.hasCachedResults) {
        // Load games if array exists and has items
        if (result.games && result.games.length > 0) {
          setGames(result.games);
        } else {
          setGames([]);
        }

        // Load services if array exists and has items
        if (result.services && result.services.length > 0) {
          setServices(result.services);
        } else {
          setServices([]);
        }

        // Store the last detection time
        if (result.lastDetectionTime) {
          setLastDetectionTime(result.lastDetectionTime);
        }

        // Show notification
        const parts = [];
        if (result.totalGamesDetected && result.totalGamesDetected > 0) {
          parts.push(
            `${result.totalGamesDetected} game${result.totalGamesDetected !== 1 ? 's' : ''}`
          );
        }
        if (result.totalServicesDetected && result.totalServicesDetected > 0) {
          parts.push(
            `${result.totalServicesDetected} service${result.totalServicesDetected !== 1 ? 's' : ''}`
          );
        }

        if (parts.length > 0) {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: t('management.gameDetection.loadedFromPreviousScan', {
              results: parts.join(' and ')
            }),
            details: { notificationType: 'success' }
          });
        } else {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: t('management.gameDetection.noPreviousResults'),
            details: { notificationType: 'info' }
          });
        }
      } else {
        setGames([]);
        setServices([]);
        setLastDetectionTime(null);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: t('management.gameDetection.noPreviousResults'),
          details: { notificationType: 'info' }
        });
      }
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

    const gameAppId = gameToRemove.game_app_id;
    const gameName = gameToRemove.game_name;
    const isEpic = gameToRemove.service === 'epicgames';

    // Add notification for tracking (shows in notification bar and on Remove button)
    addNotification({
      type: 'game_removal',
      status: 'running',
      message: t('management.gameDetection.removingGame', { name: gameName }),
      details: {
        gameAppId: gameAppId,
        gameName: gameName
      }
    });

    // Close modal immediately - progress shown via notifications
    setGameToRemove(null);

    try {
      if (isEpic) {
        await ApiService.removeEpicGameFromCache(gameName);
      } else {
        await ApiService.removeGameFromCache(gameAppId);
      }

      // Fire-and-forget: API returned 202 Accepted, removal is happening in background
      // Game disappears when notification becomes 'completed' (via useEffect above)

      // Trigger a refetch after removal likely completes to refresh downloads
      setTimeout(() => {
        onDataRefresh?.();
      }, 30000);
    } catch (err: unknown) {
      const errorMsg =
        (err instanceof Error ? err.message : String(err)) ||
        t('management.gameDetection.failedToRemoveGame');

      updateNotification(NOTIFICATION_IDS.GAME_REMOVAL, {
        status: 'failed',
        error: errorMsg
      });

      console.error('Game removal error:', err);
    }
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

    const serviceName = serviceToRemove.service_name;

    // Add notification for tracking (shows in notification bar and on Remove button)
    // Note: ID will be "service_removal-{serviceName}" for SignalR handler to find it
    addNotification({
      type: 'service_removal',
      status: 'running',
      message: t('management.gameDetection.removingService', { name: serviceName }),
      details: {
        service: serviceName
      }
    });

    // Close modal immediately - progress shown via notifications
    setServiceToRemove(null);

    try {
      await ApiService.removeServiceFromCache(serviceName);

      // Fire-and-forget: API returned 202 Accepted, removal is happening in background
      // Service disappears when notification becomes 'completed' (via useEffect above)

      // Trigger a refetch after removal likely completes to refresh downloads
      setTimeout(() => {
        onDataRefresh?.();
      }, 30000);
    } catch (err: unknown) {
      const errorMsg =
        (err instanceof Error ? err.message : String(err)) ||
        t('management.gameDetection.failedToRemoveService');

      const notifId = `service_removal-${serviceName}`;
      updateNotification(notifId, {
        status: 'failed',
        error: errorMsg
      });

      console.error('Service removal error:', err);
    }
  };

  // Expand/Collapse all handler
  const handleExpandCollapseAll = () => {
    const allExpanded = servicesExpanded && gamesExpanded;
    setServicesExpanded(!allExpanded);
    setGamesExpanded(!allExpanded);
  };

  const hasResults = filteredGames.length > 0 || filteredServices.length > 0;
  const allExpanded = servicesExpanded && gamesExpanded;

  // Resolve on GameRemovalComplete with matching gameAppId OR ServiceRemovalComplete
  // with matching serviceName — the per-item removal endpoints return 202 Accepted
  // (no operationId), so we match on the entity identifier from the SignalR payload.
  // 2-minute safety timeout so a dropped event can't hang the queue.
  const waitForGameRemoval = useCallback(
    (gameAppId: number): Promise<void> =>
      new Promise((resolve) => {
        let settled = false;
        // Capture operationId from Started so the bulk cancel useEffect can
        // abort the in-flight item if the user cancels the bulk notification.
        const startedHandler = (data: { gameAppId?: number; operationId?: string } | undefined) => {
          if (data?.gameAppId === gameAppId && typeof data.operationId === 'string') {
            currentItemOperationIdRef.current = data.operationId;
          }
        };
        const completeHandler = (data: { gameAppId?: number } | undefined) => {
          if (data?.gameAppId === gameAppId && !settled) {
            settled = true;
            off('GameRemovalStarted', startedHandler);
            off('GameRemovalComplete', completeHandler);
            resolve();
          }
        };
        on('GameRemovalStarted', startedHandler);
        on('GameRemovalComplete', completeHandler);
        setTimeout(() => {
          if (!settled) {
            settled = true;
            off('GameRemovalStarted', startedHandler);
            off('GameRemovalComplete', completeHandler);
            resolve();
          }
        }, 120_000);
      }),
    [on, off]
  );

  const waitForServiceRemoval = useCallback(
    (serviceName: string): Promise<void> =>
      new Promise((resolve) => {
        let settled = false;
        const startedHandler = (
          data: { serviceName?: string; operationId?: string } | undefined
        ) => {
          if (data?.serviceName === serviceName && typeof data.operationId === 'string') {
            currentItemOperationIdRef.current = data.operationId;
          }
        };
        const completeHandler = (data: { serviceName?: string } | undefined) => {
          if (data?.serviceName === serviceName && !settled) {
            settled = true;
            off('ServiceRemovalStarted', startedHandler);
            off('ServiceRemovalComplete', completeHandler);
            resolve();
          }
        };
        on('ServiceRemovalStarted', startedHandler);
        on('ServiceRemovalComplete', completeHandler);
        setTimeout(() => {
          if (!settled) {
            settled = true;
            off('ServiceRemovalStarted', startedHandler);
            off('ServiceRemovalComplete', completeHandler);
            resolve();
          }
        }, 120_000);
      }),
    [on, off]
  );

  // Ref mirror of notifications so the Remove-All loop can detect a cancellation
  // (the bulk_removal notification has no operationId → the cancel button just
  // removes it from state → the loop sees it disappear and exits gracefully).
  const notificationsRef = useRef(notifications);
  useEffect(() => {
    notificationsRef.current = notifications;
  }, [notifications]);

  // Cascade bulk cancel → current-item cancel. `removeGameFromCache` /
  // `removeServiceFromCache` don't return operationId, so we capture it from
  // the matching GameRemovalStarted / ServiceRemovalStarted SignalR event.
  // When the bulk notification is removed (user clicked ✕), we fire a server-
  // side cancel on the captured operationId so the current item aborts
  // immediately instead of running to completion.
  const bulkNotifIdRef = useRef<string | null>(null);
  const currentItemOperationIdRef = useRef<string | null>(null);
  useEffect(() => {
    const activeId = bulkNotifIdRef.current;
    if (!activeId) return;
    const stillPresent = notifications.some((n) => n.id === activeId);
    if (stillPresent) return;
    bulkNotifIdRef.current = null;
    const currentOp = currentItemOperationIdRef.current;
    if (currentOp) {
      currentItemOperationIdRef.current = null;
      ApiService.cancelOperation(currentOp).catch(() => {
        /* best-effort */
      });
    }
  }, [notifications]);

  const handleRemoveAllCached = useCallback(async () => {
    setShowRemoveAllConfirm(false);
    if (!isAdmin) return;

    const services = [...filteredServices];
    const games = [...filteredGames];
    const total = services.length + games.length;
    if (total === 0) return;

    setRemoveAllRunning(true);
    const notifId = addNotification({
      type: 'bulk_removal',
      status: 'running',
      message: t('management.sections.data.gameCacheRemoveAllStarting', {
        total,
        defaultValue: 'Removing 0 of {{total}} cached items...'
      }),
      progress: 0,
      details: {} // No operationId → cancel button just removes this notification
    });
    bulkNotifIdRef.current = notifId;

    const wasCancelled = () => !notificationsRef.current.find((n) => n.id === notifId);

    let completed = 0;
    let cancelled = false;

    for (const service of services) {
      if (wasCancelled()) {
        cancelled = true;
        break;
      }
      completed += 1;
      setRemoveAllProgress({ current: completed, total, label: service.service_name });
      updateNotification(notifId, {
        message: t('management.sections.data.gameCacheRemoveAllProgress', {
          current: completed,
          total,
          label: service.service_name
        }),
        progress: Math.floor(((completed - 1) / total) * 100)
      });
      try {
        await ApiService.removeServiceFromCache(service.service_name);
        await waitForServiceRemoval(service.service_name);
      } catch (err) {
        console.error('[RemoveAll] Service removal failed:', service.service_name, err);
      }
    }

    if (!cancelled) {
      for (const game of games) {
        if (wasCancelled()) {
          cancelled = true;
          break;
        }
        completed += 1;
        const label = game.game_name ?? String(game.game_app_id);
        setRemoveAllProgress({ current: completed, total, label });
        updateNotification(notifId, {
          message: t('management.sections.data.gameCacheRemoveAllProgress', {
            current: completed,
            total,
            label
          }),
          progress: Math.floor(((completed - 1) / total) * 100)
        });
        try {
          const isEpic = game.service === 'epicgames' && !!game.game_name;
          if (isEpic) {
            await ApiService.removeEpicGameFromCache(game.game_name);
          } else {
            await ApiService.removeGameFromCache(game.game_app_id);
          }
          await waitForGameRemoval(game.game_app_id);
        } catch (err) {
          console.error('[RemoveAll] Game removal failed:', game.game_app_id, err);
        }
      }
    }

    setRemoveAllRunning(false);
    setRemoveAllProgress(null);
    bulkNotifIdRef.current = null;
    currentItemOperationIdRef.current = null;

    if (notificationsRef.current.find((n) => n.id === notifId)) {
      updateNotification(notifId, {
        status: 'completed',
        progress: 100,
        message: t('management.sections.data.gameCacheRemoveAllComplete', {
          count: completed,
          defaultValue: 'Removed {{count}} cached items'
        })
      });
    }

    onDataRefresh?.();
  }, [
    filteredGames,
    filteredServices,
    isAdmin,
    waitForGameRemoval,
    waitForServiceRemoval,
    onDataRefresh,
    addNotification,
    updateNotification,
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
                        totalServices={filteredServices.length}
                        notifications={notifications}
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
                        totalGames={filteredGames.length}
                        notifications={notifications}
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
