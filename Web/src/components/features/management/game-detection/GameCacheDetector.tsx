import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Database, Server } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
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
  // Track local loading state for loading cached data (quick synchronous operation)
  // Starts true so there is a visible Loader2 spinner while the initial getCachedGameDetection() fetch completes
  const [isLoadingData, setIsLoadingData] = useState(true);
  // Ref to prevent duplicate API calls (handles rapid button clicks before state updates)
  const detectionInFlightRef = useRef(false);
  // Combined loading state: either notification says running OR we're in starting phase OR loading cached data
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
            cachePath: config.cachePath || '/cache',
            logsPath: config.logsPath || '/logs',
            cacheWritable: config.cacheWritable ?? false,
            logsWritable: config.logsWritable ?? false,
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
  const [evictedItemsExpanded, setEvictedItemsExpanded] = useState(() => {
    const saved = localStorage.getItem('management-evicted-items-expanded');
    return saved !== null ? saved === 'true' : true;
  });

  useEffect(() => {
    localStorage.setItem('management-evicted-items-expanded', String(evictedItemsExpanded));
  }, [evictedItemsExpanded]);

  useEffect(() => {
    localStorage.setItem('management-game-cache-expanded', String(sectionExpanded));
  }, [sectionExpanded]);

  // Evicted Games — derived from local games state.
  // Includes partially-evicted games (evicted_downloads_count > 0) as well as fully-evicted ones.
  // Using local state instead of gameDetectionData from context ensures evictedGames
  // updates immediately in the same render cycle when setGames() is called on scan completion.
  const evictedGames = useMemo(
    () =>
      games.filter((game) => (game.evicted_downloads_count ?? 0) > 0 || game.is_evicted === true),
    [games]
  );

  const [evictedGameToRemove, setEvictedGameToRemove] = useState<GameCacheInfo | null>(null);
  const [partialEvictedTarget, setPartialEvictedTarget] = useState<
    GameCacheInfo | ServiceCacheInfo | null
  >(null);

  const handleEvictedGameRemoveClick = (game: GameCacheInfo) => {
    if (!isAdmin) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('common.fullAuthRequired'),
        details: { notificationType: 'error' }
      });
      return;
    }
    // Route to partial eviction if not fully evicted
    if (game.is_evicted !== true && (game.evicted_downloads_count ?? 0) > 0) {
      setPartialEvictedTarget(game);
    } else {
      setEvictedGameToRemove(game);
    }
  };

  const handleEvictedServiceRemoveClick = (service: ServiceCacheInfo) => {
    if (!isAdmin) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('common.fullAuthRequired'),
        details: { notificationType: 'error' }
      });
      return;
    }
    // Route to partial eviction if not fully evicted
    if (service.is_evicted !== true && (service.evicted_downloads_count ?? 0) > 0) {
      setPartialEvictedTarget(service);
    } else {
      handleServiceRemoveClick(service);
    }
  };

  const confirmPartialEvictedRemoval = async () => {
    if (!partialEvictedTarget) return;

    const isService = 'service_name' in partialEvictedTarget;

    if (isService) {
      const service = partialEvictedTarget as ServiceCacheInfo;
      const serviceName = service.service_name;
      addNotification({
        type: 'service_removal',
        status: 'running',
        message: t('management.gameDetection.removingService', { name: serviceName }),
        details: { service: serviceName }
      });
      setPartialEvictedTarget(null);
      try {
        await ApiService.removeEvictedForService(serviceName);
        onDataRefresh?.();
      } catch (err: unknown) {
        const errorMsg =
          (err instanceof Error ? err.message : String(err)) ||
          t('management.gameDetection.failedToRemoveService');
        console.error('Partial evicted service removal error:', errorMsg);
      }
    } else {
      const game = partialEvictedTarget as GameCacheInfo;
      const gameAppId = game.game_app_id;
      const gameName = game.game_name;
      const isEpic = game.service === 'epicgames';
      addNotification({
        type: 'game_removal',
        status: 'running',
        message: t('management.gameDetection.removingGame', { name: gameName }),
        details: { gameAppId, gameName }
      });
      setPartialEvictedTarget(null);
      try {
        if (isEpic && game.epic_app_id) {
          await ApiService.removeEvictedForEpicGame(game.epic_app_id);
        } else {
          await ApiService.removeEvictedForGame(gameAppId);
        }
        onDataRefresh?.();
      } catch (err: unknown) {
        const errorMsg =
          (err instanceof Error ? err.message : String(err)) ||
          t('management.gameDetection.failedToRemoveGame');
        console.error('Partial evicted game removal error:', errorMsg);
      }
    }
  };

  const confirmEvictedGameRemoval = async () => {
    if (!evictedGameToRemove) return;

    const gameAppId = evictedGameToRemove.game_app_id;
    const gameName = evictedGameToRemove.game_name;
    const isEpic = evictedGameToRemove.service === 'epicgames';

    addNotification({
      type: 'game_removal',
      status: 'running',
      message: t('management.gameDetection.removingGame', { name: gameName }),
      details: { gameAppId, gameName }
    });

    setEvictedGameToRemove(null);

    try {
      if (isEpic) {
        await ApiService.removeEpicGameFromCache(gameName);
      } else {
        await ApiService.removeGameFromCache(gameAppId);
      }
      onDataRefresh?.();
    } catch (err: unknown) {
      const errorMsg =
        (err instanceof Error ? err.message : String(err)) ||
        t('management.gameDetection.failedToRemoveGame');
      console.error('Evicted game removal error:', errorMsg);
    }
  };

  // Format last detection time with timezone awareness
  const formattedLastDetectionTime = useFormattedDateTime(lastDetectionTime);

  // Filter games and services by selected datasource.
  // Evicted games/services are excluded from the main list — they are shown in the Evicted Items section.
  // Note: Items with empty/missing datasources (legacy data) are shown regardless of filter.
  const activeGames = games.filter((g) => !g.is_evicted);
  const filteredGames = selectedDatasource
    ? activeGames.filter(
        (g) => !g.datasources?.length || g.datasources.includes(selectedDatasource)
      )
    : activeGames;
  const evictedServices = useMemo(
    () =>
      services.filter(
        (service: ServiceCacheInfo) =>
          (service.evicted_downloads_count ?? 0) > 0 || service.is_evicted === true
      ),
    [services]
  );
  const activeServices = services.filter((s: ServiceCacheInfo) => !s.is_evicted);
  const filteredServices = selectedDatasource
    ? activeServices.filter(
        (s) => !s.datasources?.length || s.datasources.includes(selectedDatasource)
      )
    : activeServices;
  const filteredEvictedGames = selectedDatasource
    ? evictedGames.filter(
        (g) => !g.datasources?.length || g.datasources.includes(selectedDatasource)
      )
    : evictedGames;
  const filteredEvictedServices = selectedDatasource
    ? evictedServices.filter(
        (s) => !s.datasources?.length || s.datasources.includes(selectedDatasource)
      )
    : evictedServices;

  // Auto-collapse sections if they have many items (> 10)
  useEffect(() => {
    if (filteredServices.length > 10) {
      setServicesExpanded(false);
    } else {
      setServicesExpanded(true);
    }
    if (filteredGames.length > 10) {
      setGamesExpanded(false);
    } else {
      setGamesExpanded(true);
    }
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
      } finally {
        // Clear the initial loading state after the fetch completes (success or failure)
        setIsLoadingData(false);
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
    // Handle completed game removals
    const gameRemovalNotifs = notifications.filter(
      (n) => n.type === 'game_removal' && n.status === 'completed'
    );
    gameRemovalNotifs.forEach((notif) => {
      const gameAppId = notif.details?.gameAppId;
      const gameName = notif.details?.gameName;

      // Remove from UI (backend already removed from database)
      if (gameAppId) {
        // Steam game: match by appId
        setGames((prev) => prev.filter((g) => g.game_app_id !== gameAppId));
      } else if (gameName) {
        // Epic game: match by name (gameAppId is 0)
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
      // Game will be removed from list when SignalR GameRemovalComplete event arrives

      // Trigger a refetch after removal likely completes to refresh downloads
      setTimeout(() => {
        onDataRefresh?.();
      }, 30000); // Refresh after 30 seconds
    } catch (err: unknown) {
      const errorMsg =
        (err instanceof Error ? err.message : String(err)) ||
        t('management.gameDetection.failedToRemoveGame');

      // Update the game_removal notification to failed
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
      // Service will be removed from list when SignalR ServiceRemovalComplete event arrives

      // Trigger a refetch after removal likely completes to refresh downloads
      setTimeout(() => {
        onDataRefresh?.();
      }, 30000); // Refresh after 30 seconds
    } catch (err: unknown) {
      const errorMsg =
        (err instanceof Error ? err.message : String(err)) ||
        t('management.gameDetection.failedToRemoveService');

      // Update notification to failed (ID is "service_removal-{serviceName}")
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
    const allExpanded = servicesExpanded && gamesExpanded && evictedItemsExpanded;
    setServicesExpanded(!allExpanded);
    setGamesExpanded(!allExpanded);
    setEvictedItemsExpanded(!allExpanded);
  };

  const hasResults =
    filteredGames.length > 0 ||
    filteredServices.length > 0 ||
    filteredEvictedGames.length > 0 ||
    filteredEvictedServices.length > 0;
  const allExpanded = servicesExpanded && gamesExpanded && evictedItemsExpanded;

  // Help content
  // Header actions - scan buttons + expand/collapse all
  const headerActions = (
    <div className="flex items-center gap-2">
      {hasResults && (
        <Button variant="default" size="sm" onClick={handleExpandCollapseAll}>
          {allExpanded
            ? t('management.gameDetection.collapseAll')
            : t('management.gameDetection.expandAll')}
        </Button>
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
                <span className="themed-badge status-badge-info">
                  {filteredGames.length +
                    filteredServices.length +
                    filteredEvictedGames.length +
                    filteredEvictedServices.length}
                </span>
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

                  {/* Evicted Items Section (Accordion) */}
                  <AccordionSection
                    title={t('management.gameDetection.evictedItems')}
                    count={filteredEvictedGames.length + filteredEvictedServices.length}
                    icon={Database}
                    iconColor="var(--theme-icon-orange)"
                    isExpanded={evictedItemsExpanded}
                    onToggle={() => setEvictedItemsExpanded((prev) => !prev)}
                  >
                    {loading ? (
                      <LoadingState message={t('management.gameDetection.loadingEvictedGames')} />
                    ) : filteredEvictedGames.length === 0 &&
                      filteredEvictedServices.length === 0 ? (
                      <EmptyState
                        icon={Database}
                        title={t('management.gameDetection.noEvictedItems')}
                      />
                    ) : (
                      <div className="space-y-4">
                        {filteredEvictedServices.length > 0 && (
                          <ServicesList
                            services={filteredEvictedServices}
                            totalServices={filteredEvictedServices.length}
                            notifications={notifications}
                            isAnyRemovalRunning={isAnyRemovalRunning}
                            isAdmin={isAdmin}
                            cacheReadOnly={cacheReadOnly}
                            dockerSocketAvailable={isDockerAvailable}
                            checkingPermissions={checkingPermissions}
                            onRemoveService={handleEvictedServiceRemoveClick}
                            variant="evicted"
                          />
                        )}
                        {filteredEvictedGames.length > 0 && (
                          <GamesList
                            games={filteredEvictedGames}
                            totalGames={filteredEvictedGames.length}
                            notifications={notifications}
                            isAnyRemovalRunning={isAnyRemovalRunning}
                            isAdmin={isAdmin}
                            cacheReadOnly={cacheReadOnly}
                            dockerSocketAvailable={isDockerAvailable}
                            checkingPermissions={checkingPermissions}
                            onRemoveGame={handleEvictedGameRemoveClick}
                            variant="evicted"
                          />
                        )}
                      </div>
                    )}
                  </AccordionSection>

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

      {/* Evicted Game Removal Confirmation Modal */}
      <CacheRemovalModal
        target={evictedGameToRemove ? { type: 'game', data: evictedGameToRemove } : null}
        onClose={() => setEvictedGameToRemove(null)}
        onConfirm={confirmEvictedGameRemoval}
      />

      {/* Partial Eviction Removal Confirmation Modal */}
      {partialEvictedTarget !== null &&
        (() => {
          const isService = 'service_name' in partialEvictedTarget;
          const name = isService
            ? (partialEvictedTarget as ServiceCacheInfo).service_name
            : (partialEvictedTarget as GameCacheInfo).game_name;
          const evictedCount = partialEvictedTarget.evicted_downloads_count ?? 0;
          const evictedBytes = partialEvictedTarget.evicted_bytes ?? 0;
          const titleKey = isService
            ? 'modals.cacheRemoval.titlePartialEvictedService'
            : 'modals.cacheRemoval.titlePartialEvictedGame';
          const descKey = isService
            ? 'modals.cacheRemoval.confirmPartialEvictedService'
            : 'modals.cacheRemoval.confirmPartialEvictedGame';
          return (
            <CacheRemovalModal
              target={
                isService
                  ? { type: 'service', data: partialEvictedTarget as ServiceCacheInfo }
                  : { type: 'game', data: partialEvictedTarget as GameCacheInfo }
              }
              onClose={() => setPartialEvictedTarget(null)}
              onConfirm={confirmPartialEvictedRemoval}
              titleOverride={t(titleKey)}
              descriptionOverride={t(descKey, { name, count: evictedCount })}
              evictedCount={evictedCount}
              evictedBytes={evictedBytes}
            />
          );
        })()}
    </>
  );
};

export default GameCacheDetector;
