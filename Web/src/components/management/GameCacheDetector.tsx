import React, { useState, useEffect, useRef, useMemo } from 'react';
import {
  HardDrive,
  Loader2,
  Database,
  Trash2,
  AlertTriangle,
  ChevronDown,
  ChevronUp,
  FolderOpen,
  Search,
  Lock
} from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import { useNotifications } from '@contexts/NotificationsContext';
import { useBackendOperation } from '@hooks/useBackendOperation';
import type { GameCacheInfo, ServiceCacheInfo } from '../../types';

interface GameCacheDetectorProps {
  mockMode?: boolean;
  isAuthenticated?: boolean;
  onDataRefresh?: () => void;
  refreshKey?: number;
}

const GameCacheDetector: React.FC<GameCacheDetectorProps> = ({
  mockMode = false,
  isAuthenticated = false,
  onDataRefresh,
  refreshKey = 0
}) => {
  const { addNotification, updateNotification, removeNotification, notifications } =
    useNotifications();
  const gameDetectionOp = useBackendOperation('activeGameDetection', 'gameDetection', 120);
  const [loading, setLoading] = useState(false);
  const [games, setGames] = useState<GameCacheInfo[]>([]);
  const [services, setServices] = useState<ServiceCacheInfo[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [totalGames, setTotalGames] = useState<number>(0);
  const [totalServices, setTotalServices] = useState<number>(0);
  const [gameToRemove, setGameToRemove] = useState<GameCacheInfo | null>(null);
  const [serviceToRemove, setServiceToRemove] = useState<ServiceCacheInfo | null>(null);
  const [expandedGameId, setExpandedGameId] = useState<number | null>(null);
  const [expandedServiceName, setExpandedServiceName] = useState<string | null>(null);
  const [expandingGameId, setExpandingGameId] = useState<number | null>(null);
  const [expandingServiceName, setExpandingServiceName] = useState<string | null>(null);
  const [showAllPaths, setShowAllPaths] = useState<Record<number | string, boolean>>({});
  const [showAllUrls, setShowAllUrls] = useState<Record<number | string, boolean>>({});
  const pollingIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const detectionNotificationIdRef = useRef<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [currentPage, setCurrentPage] = useState(1);
  const itemsPerPage = 20;
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [hasProcessedLogs, setHasProcessedLogs] = useState(false);
  const [checkingLogs, setCheckingLogs] = useState(true);

  const MAX_INITIAL_PATHS = 50; // Only show 50 paths initially to prevent lag
  const MAX_INITIAL_URLS = 20; // Only show 20 URLs initially

  // Cleanup polling on unmount
  useEffect(() => {
    return () => {
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
      }
    };
  }, []);

  // Load cached games and services from backend on mount and when refreshKey changes
  useEffect(() => {
    const loadCachedGames = async () => {
      if (mockMode) return;

      try {
        const result = await ApiService.getCachedGameDetection();
        if (result.hasCachedResults) {
          // Load games if available
          if (result.games && result.totalGamesDetected) {
            setGames(result.games);
            setTotalGames(result.totalGamesDetected);
          } else {
            setGames([]);
            setTotalGames(0);
          }

          // Load services if available
          if (result.services && result.totalServicesDetected) {
            setServices(result.services);
            setTotalServices(result.totalServicesDetected);
          } else {
            setServices([]);
            setTotalServices(0);
          }
        } else {
          // No cached results - clear the display
          setGames([]);
          setTotalGames(0);
          setServices([]);
          setTotalServices(0);
        }
      } catch (err) {
        console.error('[GameCacheDetector] Failed to load cached games and services:', err);
        // Silent fail - not critical
      }
    };

    loadCachedGames();
    if (refreshKey === 0) {
      // Only check permissions and logs on initial mount
      loadDirectoryPermissions();
      checkIfLogsProcessed(); // Check database for LogEntries
      restoreGameDetection(); // Restore interrupted operation if any
    }
  }, [mockMode, refreshKey]); // Re-run when mockMode or refreshKey changes

  const restoreGameDetection = async () => {
    try {
      const operation = await gameDetectionOp.load();
      if (operation?.data) {
        const data = operation.data as any;
        if (data.operationId) {
          setLoading(true);

          // Start polling for the status of the restored operation
          if (pollingIntervalRef.current) {
            clearInterval(pollingIntervalRef.current);
          }
          pollingIntervalRef.current = setInterval(() => {
            pollDetectionStatus(data.operationId);
          }, 5000); // Poll every 5 seconds

          // Poll immediately to get current status
          pollDetectionStatus(data.operationId);
        }
      }
    } catch (err) {
      console.error('[GameCacheDetector] Failed to restore game detection operation:', err);
    }
  };

  const loadDirectoryPermissions = async () => {
    try {
      setCheckingPermissions(true);
      const data = await ApiService.getDirectoryPermissions();
      setCacheReadOnly(data.cache.readOnly);
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      setCacheReadOnly(false); // Assume writable on error
    } finally {
      setCheckingPermissions(false);
    }
  };

  const checkIfLogsProcessed = async () => {
    try {
      setCheckingLogs(true);
      // Check database LogEntries count (not log file counts)
      // Game detection requires LogEntries in the database, not just log files
      const dbLogCount = await ApiService.getDatabaseLogEntriesCount();
      setHasProcessedLogs(dbLogCount > 0);
    } catch (err) {
      console.error('Failed to check if logs are processed:', err);
      setHasProcessedLogs(false); // Assume no logs on error
    } finally {
      setCheckingLogs(false);
    }
  };

  // Reset page when search query changes
  useEffect(() => {
    setCurrentPage(1);
  }, [searchQuery]);

  // Listen for notification events from SignalR (consolidated)
  useEffect(() => {
    // Handle completed game removals
    const gameRemovalNotifs = notifications.filter(
      (n) => n.type === 'game_removal' && n.status === 'completed'
    );
    gameRemovalNotifs.forEach((notif) => {
      const gameAppId = notif.details?.gameAppId;
      if (!gameAppId) return;

      // Remove from UI (backend already removed from database)
      setGames((prev) => prev.filter((g) => g.game_app_id !== gameAppId));
      setTotalGames((prev) => prev - 1);
    });

    // Handle completed service removals
    const serviceRemovalNotifs = notifications.filter(
      (n) => n.type === 'service_removal' && n.status === 'completed'
    );
    serviceRemovalNotifs.forEach((notif) => {
      const serviceName = notif.details?.service;
      if (!serviceName) return;

      // Remove from UI (backend already removed from database)
      setServices((prev) => prev.filter((s) => s.service_name !== serviceName));
      setTotalServices((prev) => prev - 1);
    });

    // Handle database reset completion
    const databaseResetNotifs = notifications.filter(
      (n) => n.type === 'database_reset' && n.status === 'completed'
    );
    if (databaseResetNotifs.length > 0) {
      console.log(
        '[GameCacheDetector] Database reset detected, clearing games/services and re-checking database LogEntries'
      );
      setGames([]);
      setTotalGames(0);
      setServices([]);
      setTotalServices(0);
      checkIfLogsProcessed();
    }

    // Handle log processing completion
    const logProcessingNotifs = notifications.filter(
      (n) => n.type === 'log_processing' && n.status === 'completed'
    );
    if (logProcessingNotifs.length > 0) {
      console.log('[GameCacheDetector] Log processing completed, re-checking database LogEntries');
      checkIfLogsProcessed();
    }
  }, [notifications]);

  // Memoized filtered, sorted, and paginated games list
  const filteredAndSortedGames = useMemo(() => {
    // Filter by search query (search in game name or app ID)
    const filtered = games.filter(
      (game) =>
        game.game_name.toLowerCase().includes(searchQuery.toLowerCase()) ||
        game.game_app_id.toString().includes(searchQuery)
    );

    // Sort alphabetically by game name (case-insensitive)
    filtered.sort((a, b) =>
      a.game_name.localeCompare(b.game_name, undefined, { sensitivity: 'base' })
    );

    return filtered;
  }, [games, searchQuery]);

  const paginatedGames = useMemo(() => {
    const startIndex = (currentPage - 1) * itemsPerPage;
    const endIndex = startIndex + itemsPerPage;
    return filteredAndSortedGames.slice(startIndex, endIndex);
  }, [filteredAndSortedGames, currentPage, itemsPerPage]);

  const totalPages = Math.ceil(filteredAndSortedGames.length / itemsPerPage);

  const pollDetectionStatus = async (operationId: string) => {
    try {
      const status = await ApiService.getGameDetectionStatus(operationId);

      if (status.status === 'complete') {
        // Detection complete
        if (pollingIntervalRef.current) {
          clearInterval(pollingIntervalRef.current);
          pollingIntervalRef.current = null;
        }

        // Remove the "Detecting games in cache..." notification FIRST
        // Do this before any await to ensure notification always clears
        // Handle both manually started (stored in ref) and recovered (hardcoded id) cases
        if (detectionNotificationIdRef.current) {
          removeNotification(detectionNotificationIdRef.current);
          detectionNotificationIdRef.current = null;
        }
        removeNotification('game_detection'); // Also remove recovery notification if it exists

        setLoading(false);

        // Clear operation state - detection is complete (non-blocking)
        gameDetectionOp.clear().catch((err) => console.error('Failed to clear operation state:', err));

        // Update games if available
        if (status.games && status.totalGamesDetected !== undefined) {
          setGames(status.games);
          setTotalGames(status.totalGamesDetected);
        }

        // Update services if available
        if (status.services && status.totalServicesDetected !== undefined) {
          setServices(status.services);
          setTotalServices(status.totalServicesDetected);
        }

        // Backend already saved to database - no need to save locally

        // Show notification with detection results
        const gamesCount = status.totalGamesDetected || 0;
        const servicesCount = status.totalServicesDetected || 0;

        if (gamesCount > 0 || servicesCount > 0) {
          const parts = [];
          if (gamesCount > 0) {
            parts.push(`${gamesCount} game${gamesCount !== 1 ? 's' : ''}`);
          }
          if (servicesCount > 0) {
            parts.push(`${servicesCount} service${servicesCount !== 1 ? 's' : ''}`);
          }

          addNotification({
            type: 'generic',
            status: 'completed',
            message: `Detected ${parts.join(' and ')} with cache files`,
            details: { notificationType: 'success' }
          });
        }
      } else if (status.status === 'failed') {
        // Detection failed
        if (pollingIntervalRef.current) {
          clearInterval(pollingIntervalRef.current);
          pollingIntervalRef.current = null;
        }

        // Remove the "Detecting games in cache..." notification FIRST
        // Do this before any await to ensure notification always clears
        // Handle both manually started (stored in ref) and recovered (hardcoded id) cases
        if (detectionNotificationIdRef.current) {
          removeNotification(detectionNotificationIdRef.current);
          detectionNotificationIdRef.current = null;
        }
        removeNotification('game_detection'); // Also remove recovery notification if it exists

        setLoading(false);

        // Clear operation state - detection failed (non-blocking)
        gameDetectionOp.clear().catch((err) => console.error('Failed to clear operation state:', err));

        const errorMsg = status.error || 'Detection failed';
        setError(errorMsg);
        addNotification({
          type: 'generic',
          status: 'failed',
          message: errorMsg,
          details: { notificationType: 'error' }
        });
      }
      // If status is 'running', continue polling
    } catch (err: any) {
      // Log errors but continue polling - detection might just be slow
      if (err.name === 'TimeoutError' || err.message?.includes('timeout')) {
        console.warn('[GameCacheDetector] Timeout checking detection status, will retry...');
      } else {
        console.error('[GameCacheDetector] Error polling detection status:', err);
      }
      // Continue polling - don't give up
    }
  };

  const handleDetect = async () => {
    if (mockMode) {
      const errorMsg = 'Detection disabled in mock mode';
      setError(errorMsg);
      addNotification({
        type: 'generic',
        status: 'failed',
        message: errorMsg,
        details: { notificationType: 'error' }
      });
      return;
    }

    setLoading(true);
    setError(null);
    setGames([]);
    setTotalGames(0);
    setServices([]);
    setTotalServices(0);

    try {
      // Start background detection
      const result = await ApiService.startGameCacheDetection();

      // Save operation state for restoration on page refresh
      await gameDetectionOp.save({ operationId: result.operationId });

      // Add notification to show detection is in progress
      const notificationId = addNotification({
        type: 'generic',
        status: 'running',
        message: 'Detecting games in cache...',
        details: { notificationType: 'info' }
      });
      detectionNotificationIdRef.current = notificationId;

      // Start polling for status
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
      }
      pollingIntervalRef.current = setInterval(() => {
        pollDetectionStatus(result.operationId);
      }, 5000); // Poll every 5 seconds - detection can take a while

      // Poll immediately
      pollDetectionStatus(result.operationId);
    } catch (err: any) {
      const errorMsg = err.message || 'Failed to start game detection';
      setError(errorMsg);
      addNotification({
        type: 'generic',
        status: 'failed',
        message: errorMsg,
        details: { notificationType: 'error' }
      });
      console.error('Game detection error:', err);
      setLoading(false);
    }
  };

  const handleRemoveClick = (game: GameCacheInfo) => {
    if (!isAuthenticated) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Full authentication required for management operations',
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

    // Add notification for tracking (shows in notification bar and on Remove button)
    // Note: ID will be "game_removal-{gameAppId}" for SignalR handler to find it
    addNotification({
      type: 'game_removal',
      status: 'running',
      message: `Removing ${gameName}...`,
      details: {
        gameAppId: gameAppId,
        gameName: gameName
      }
    });

    // Close modal immediately - progress shown via notifications
    setGameToRemove(null);
    setError(null);

    try {
      const result = await ApiService.removeGameFromCache(gameAppId);

      // Fire-and-forget: API returned 202 Accepted, removal is happening in background
      // Game will be removed from list when SignalR GameRemovalComplete event arrives
      console.log(`Game removal started for AppID ${gameAppId}: ${result.message}`);

      // Trigger a refetch after removal likely completes to refresh downloads
      setTimeout(() => {
        onDataRefresh?.();
      }, 30000); // Refresh after 30 seconds
    } catch (err: any) {
      const errorMsg = err.message || 'Failed to remove game from cache';

      // Update notification to failed (ID is "game_removal-{gameAppId}")
      const notifId = `game_removal-${gameAppId}`;
      updateNotification(notifId, {
        status: 'failed',
        error: errorMsg
      });

      console.error('Game removal error:', err);
    }
  };

  const handleServiceRemoveClick = (service: ServiceCacheInfo) => {
    if (!isAuthenticated) {
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Full authentication required for management operations',
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
      message: `Removing ${serviceName} service...`,
      details: {
        service: serviceName
      }
    });

    // Close modal immediately - progress shown via notifications
    setServiceToRemove(null);
    setError(null);

    try {
      const result = await ApiService.removeServiceFromCache(serviceName);

      // Fire-and-forget: API returned 202 Accepted, removal is happening in background
      // Service will be removed from list when SignalR ServiceRemovalComplete event arrives
      console.log(`Service removal started for ${serviceName}: ${result.message}`);

      // Trigger a refetch after removal likely completes to refresh downloads
      setTimeout(() => {
        onDataRefresh?.();
      }, 30000); // Refresh after 30 seconds
    } catch (err: any) {
      const errorMsg = err.message || 'Failed to remove service from cache';

      // Update notification to failed (ID is "service_removal-{serviceName}")
      const notifId = `service_removal-${serviceName}`;
      updateNotification(notifId, {
        status: 'failed',
        error: errorMsg
      });

      console.error('Service removal error:', err);
    }
  };

  const toggleGameDetails = (gameId: number) => {
    // If already expanded, collapse immediately
    if (expandedGameId === gameId) {
      setExpandedGameId(null);
      setShowAllPaths((prev) => ({ ...prev, [gameId]: false })); // Reset show all when collapsing
      setShowAllUrls((prev) => ({ ...prev, [gameId]: false }));
      return;
    }

    // Show loading state for expansion
    setExpandingGameId(gameId);

    // Use setTimeout to allow the loading spinner to render before heavy DOM updates
    setTimeout(() => {
      setExpandedGameId(gameId);
      setExpandingGameId(null);
    }, 50); // Small delay to let spinner show
  };

  const toggleServiceDetails = (serviceName: string) => {
    // If already expanded, collapse immediately
    if (expandedServiceName === serviceName) {
      setExpandedServiceName(null);
      setShowAllPaths((prev) => ({ ...prev, [serviceName]: false })); // Reset show all when collapsing
      setShowAllUrls((prev) => ({ ...prev, [serviceName]: false }));
      return;
    }

    // Show loading state for expansion
    setExpandingServiceName(serviceName);

    // Use setTimeout to allow the loading spinner to render before heavy DOM updates
    setTimeout(() => {
      setExpandedServiceName(serviceName);
      setExpandingServiceName(null);
    }, 50); // Small delay to let spinner show
  };

  const toggleShowAllPaths = (id: number | string) => {
    setShowAllPaths((prev) => ({ ...prev, [id]: !prev[id] }));
  };

  const toggleShowAllUrls = (id: number | string) => {
    setShowAllUrls((prev) => ({ ...prev, [id]: !prev[id] }));
  };

  const formatBytes = (bytes: number): string => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(2))} ${sizes[i]}`;
  };

  return (
    <>
      <Card>
        <div className="space-y-4">
          {/* Header Section */}
          {cacheReadOnly ? (
            <div className="flex items-center gap-2">
              <h3 className="text-lg font-semibold text-themed-primary flex items-center gap-2">
                <HardDrive className="w-5 h-5" />
                Game Cache Detection
              </h3>
              <span
                className="px-2 py-0.5 text-xs rounded font-medium flex items-center gap-1.5 border"
                style={{
                  backgroundColor: 'var(--theme-warning-bg)',
                  color: 'var(--theme-warning)',
                  borderColor: 'var(--theme-warning)'
                }}
              >
                <Lock className="w-3 h-3" />
                Read-only
              </span>
            </div>
          ) : (
            <div className="flex items-center justify-between">
              <div>
                <h3 className="text-lg font-semibold text-themed-primary flex items-center gap-2">
                  <HardDrive className="w-5 h-5" />
                  Game Cache Detection
                </h3>
                <p className="text-sm text-themed-secondary mt-1">
                  Scan cache directory to find which games have stored files
                </p>
              </div>
              {(() => {
                const detectButton = (
                  <Button
                    onClick={handleDetect}
                    disabled={
                      loading ||
                      mockMode ||
                      cacheReadOnly ||
                      checkingPermissions ||
                      !hasProcessedLogs ||
                      checkingLogs
                    }
                    variant="filled"
                    color="blue"
                    leftSection={loading ? <Loader2 className="w-4 h-4 animate-spin" /> : undefined}
                    title={cacheReadOnly ? 'Cache directory is mounted read-only' : undefined}
                  >
                    {loading ? 'Detecting...' : 'Detect Games'}
                  </Button>
                );

                return !hasProcessedLogs && !checkingLogs ? (
                  <Tooltip content="Process access logs to populate the database first. LogEntries in the database are required for game detection.">
                    {detectButton}
                  </Tooltip>
                ) : (
                  detectButton
                );
              })()}
            </div>
          )}

          {!cacheReadOnly && (
            <>
              {/* Loading State */}
              {loading && (
                <div className="flex flex-col items-center justify-center py-8 gap-3">
                  <Loader2 className="w-6 h-6 animate-spin text-themed-accent" />
                  <p className="text-sm text-themed-secondary">
                    Scanning database and cache directory...
                  </p>
                  <p className="text-xs text-themed-muted">
                    This may take several minutes for large databases and cache directories
                  </p>
                </div>
              )}

              {/* Games List */}
              {!loading && totalGames > 0 && (
                <>
                  <div
                    className="mb-3 p-3 rounded-lg border"
                    style={{
                      backgroundColor: 'var(--theme-bg-elevated)',
                      borderColor: 'var(--theme-border-secondary)'
                    }}
                  >
                    <div className="flex items-center gap-2 text-themed-primary font-medium">
                      <Database className="w-5 h-5 text-themed-accent" />
                      Found {totalGames} game{totalGames !== 1 ? 's' : ''} with cache files
                      {searchQuery && filteredAndSortedGames.length !== totalGames && (
                        <span className="text-sm text-themed-muted font-normal">
                          ({filteredAndSortedGames.length} matching)
                        </span>
                      )}
                    </div>
                  </div>

                  {/* Search Bar */}
                  <div className="mb-3">
                    <div className="relative">
                      <Search className="absolute left-3 top-1/2 transform -translate-y-1/2 w-4 h-4 text-themed-muted" />
                      <input
                        type="text"
                        placeholder="Search by game name or AppID..."
                        value={searchQuery}
                        onChange={(e) => setSearchQuery(e.target.value)}
                        className="w-full pl-10 pr-4 py-2 rounded-lg border text-sm"
                        style={{
                          backgroundColor: 'var(--theme-bg-secondary)',
                          borderColor: 'var(--theme-border-secondary)',
                          color: 'var(--theme-text-primary)'
                        }}
                      />
                      {searchQuery && (
                        <button
                          onClick={() => setSearchQuery('')}
                          className="absolute right-3 top-1/2 transform -translate-y-1/2 text-themed-muted hover:text-themed-primary text-xs"
                        >
                          Clear
                        </button>
                      )}
                    </div>
                  </div>

                  {/* No Results Message */}
                  {filteredAndSortedGames.length === 0 && (
                    <div className="text-center py-8 text-themed-muted">
                      <Search className="w-12 h-12 mx-auto mb-3 opacity-50" />
                      <div className="mb-2">
                        No games found matching &ldquo;{searchQuery}&rdquo;
                      </div>
                      <Button variant="subtle" size="sm" onClick={() => setSearchQuery('')}>
                        Clear search
                      </Button>
                    </div>
                  )}

                  {filteredAndSortedGames.length > 0 && (
                    <>
                      <div className="space-y-3">
                        {paginatedGames.map((game) => (
                          <div
                            key={game.game_app_id}
                            className="rounded-lg border"
                            style={{
                              backgroundColor: 'var(--theme-bg-tertiary)',
                              borderColor: 'var(--theme-border-secondary)'
                            }}
                          >
                            <div className="flex items-center gap-2 p-3">
                              <Button
                                onClick={() => toggleGameDetails(game.game_app_id)}
                                variant="subtle"
                                size="sm"
                                className="flex-shrink-0"
                                disabled={expandingGameId === game.game_app_id}
                              >
                                {expandingGameId === game.game_app_id ? (
                                  <Loader2 className="w-4 h-4 animate-spin" />
                                ) : expandedGameId === game.game_app_id ? (
                                  <ChevronUp className="w-4 h-4" />
                                ) : (
                                  <ChevronDown className="w-4 h-4" />
                                )}
                              </Button>
                              <div className="flex-1 min-w-0">
                                <div className="flex items-center gap-2 mb-1">
                                  <h4 className="text-themed-primary font-semibold truncate">
                                    {game.game_name}
                                  </h4>
                                  <span className="text-xs text-themed-muted bg-themed-elevated px-2 py-0.5 rounded flex-shrink-0">
                                    AppID: {game.game_app_id}
                                  </span>
                                </div>
                                <div className="flex items-center gap-3 text-xs text-themed-muted flex-wrap">
                                  <span className="flex items-center gap-1">
                                    <FolderOpen className="w-3 h-3" />
                                    <strong className="text-themed-primary">
                                      {game.cache_files_found.toLocaleString()}
                                    </strong>{' '}
                                    files
                                  </span>
                                  <span className="flex items-center gap-1">
                                    <HardDrive className="w-3 h-3" />
                                    <strong className="text-themed-primary">
                                      {formatBytes(game.total_size_bytes)}
                                    </strong>
                                  </span>
                                  <span className="flex items-center gap-1">
                                    <Database className="w-3 h-3" />
                                    <strong className="text-themed-primary">
                                      {game.depot_ids.length}
                                    </strong>{' '}
                                    depot{game.depot_ids.length !== 1 ? 's' : ''}
                                  </span>
                                </div>
                              </div>
                              <Tooltip content="Remove all cache files for this game">
                                <Button
                                  onClick={() => handleRemoveClick(game)}
                                  disabled={
                                    mockMode ||
                                    notifications.some(
                                      (n) =>
                                        n.type === 'game_removal' &&
                                        n.details?.gameAppId === game.game_app_id &&
                                        n.status === 'running'
                                    ) ||
                                    !isAuthenticated ||
                                    cacheReadOnly ||
                                    checkingPermissions
                                  }
                                  variant="filled"
                                  color="red"
                                  size="sm"
                                  loading={notifications.some(
                                    (n) =>
                                      n.type === 'game_removal' &&
                                      n.details?.gameAppId === game.game_app_id &&
                                      n.status === 'running'
                                  )}
                                  title={
                                    cacheReadOnly
                                      ? 'Cache directory is mounted read-only'
                                      : undefined
                                  }
                                >
                                  {notifications.some(
                                    (n) =>
                                      n.type === 'game_removal' &&
                                      n.details?.gameAppId === game.game_app_id &&
                                      n.status === 'running'
                                  )
                                    ? 'Removing...'
                                    : 'Remove'}
                                </Button>
                              </Tooltip>
                            </div>

                            {/* Loading State for Expansion */}
                            {expandingGameId === game.game_app_id && (
                              <div
                                className="border-t px-3 py-4 flex items-center justify-center"
                                style={{ borderColor: 'var(--theme-border-secondary)' }}
                              >
                                <div className="flex items-center gap-2 text-themed-muted">
                                  <Loader2 className="w-4 h-4 animate-spin" />
                                  <span className="text-sm">Loading details...</span>
                                </div>
                              </div>
                            )}

                            {/* Expandable Details Section */}
                            {expandedGameId === game.game_app_id &&
                              expandingGameId !== game.game_app_id && (
                                <div
                                  className="border-t px-3 py-3 space-y-3"
                                  style={{ borderColor: 'var(--theme-border-secondary)' }}
                                >
                                  {/* Depot IDs */}
                                  {game.depot_ids.length > 0 && (
                                    <div>
                                      <p className="text-xs text-themed-muted mb-1.5 font-medium">
                                        Depot IDs:
                                      </p>
                                      <div className="flex flex-wrap gap-1">
                                        {game.depot_ids.map((depotId) => (
                                          <span
                                            key={depotId}
                                            className="text-xs px-2 py-0.5 rounded border"
                                            style={{
                                              backgroundColor: 'var(--theme-bg-elevated)',
                                              borderColor: 'var(--theme-border-primary)',
                                              color: 'var(--theme-text-secondary)'
                                            }}
                                          >
                                            {depotId}
                                          </span>
                                        ))}
                                      </div>
                                    </div>
                                  )}

                                  {/* Sample URLs */}
                                  {game.sample_urls.length > 0 && (
                                    <div>
                                      <div className="flex items-center justify-between mb-1.5">
                                        <p className="text-xs text-themed-muted font-medium">
                                          Sample URLs ({game.sample_urls.length}):
                                        </p>
                                        {game.sample_urls.length > MAX_INITIAL_URLS && (
                                          <Button
                                            variant="subtle"
                                            size="xs"
                                            onClick={() => toggleShowAllUrls(game.game_app_id)}
                                            className="text-xs"
                                          >
                                            {showAllUrls[game.game_app_id]
                                              ? `Show less`
                                              : `Show all ${game.sample_urls.length}`}
                                          </Button>
                                        )}
                                      </div>
                                      <div className="space-y-1 max-h-48 overflow-y-auto">
                                        {(showAllUrls[game.game_app_id]
                                          ? game.sample_urls
                                          : game.sample_urls.slice(0, MAX_INITIAL_URLS)
                                        ).map((url, idx) => (
                                          <div
                                            key={idx}
                                            className="p-2 rounded border"
                                            style={{
                                              backgroundColor: 'var(--theme-bg-secondary)',
                                              borderColor: 'var(--theme-border-primary)'
                                            }}
                                          >
                                            <Tooltip content={url}>
                                              <span className="text-xs font-mono text-themed-primary truncate block">
                                                {url}
                                              </span>
                                            </Tooltip>
                                          </div>
                                        ))}
                                      </div>
                                      {!showAllUrls[game.game_app_id] &&
                                        game.sample_urls.length > MAX_INITIAL_URLS && (
                                          <p className="text-xs text-themed-muted mt-2 italic">
                                            Showing {MAX_INITIAL_URLS} of {game.sample_urls.length}{' '}
                                            URLs
                                          </p>
                                        )}
                                    </div>
                                  )}

                                  {/* Cache File Paths */}
                                  {game.cache_file_paths && game.cache_file_paths.length > 0 && (
                                    <div>
                                      <div className="flex items-center justify-between mb-1.5">
                                        <p className="text-xs text-themed-muted font-medium">
                                          Cache File Locations (
                                          {game.cache_file_paths.length.toLocaleString()}):
                                        </p>
                                        {game.cache_file_paths.length > MAX_INITIAL_PATHS && (
                                          <Button
                                            variant="subtle"
                                            size="xs"
                                            onClick={() => toggleShowAllPaths(game.game_app_id)}
                                            className="text-xs"
                                          >
                                            {showAllPaths[game.game_app_id]
                                              ? `Show less`
                                              : `Show all ${game.cache_file_paths.length.toLocaleString()}`}
                                          </Button>
                                        )}
                                      </div>
                                      <div className="space-y-1 max-h-48 overflow-y-auto">
                                        {(showAllPaths[game.game_app_id]
                                          ? game.cache_file_paths
                                          : game.cache_file_paths.slice(0, MAX_INITIAL_PATHS)
                                        ).map((path, idx) => (
                                          <div
                                            key={idx}
                                            className="p-2 rounded border"
                                            style={{
                                              backgroundColor: 'var(--theme-bg-secondary)',
                                              borderColor: 'var(--theme-border-primary)'
                                            }}
                                          >
                                            <Tooltip content={path}>
                                              <span className="text-xs font-mono text-themed-primary truncate block">
                                                {path}
                                              </span>
                                            </Tooltip>
                                          </div>
                                        ))}
                                      </div>
                                      {!showAllPaths[game.game_app_id] &&
                                        game.cache_file_paths.length > MAX_INITIAL_PATHS && (
                                          <p className="text-xs text-themed-muted mt-2 italic">
                                            Showing {MAX_INITIAL_PATHS} of{' '}
                                            {game.cache_file_paths.length.toLocaleString()} paths
                                          </p>
                                        )}
                                    </div>
                                  )}
                                </div>
                              )}
                          </div>
                        ))}
                      </div>

                      {/* Pagination Controls */}
                      {totalPages > 1 && (
                        <div
                          className="flex items-center justify-between mt-4 p-3 rounded-lg border"
                          style={{
                            backgroundColor: 'var(--theme-bg-elevated)',
                            borderColor: 'var(--theme-border-secondary)'
                          }}
                        >
                          <div className="text-sm text-themed-muted">
                            Showing {(currentPage - 1) * itemsPerPage + 1}-
                            {Math.min(currentPage * itemsPerPage, filteredAndSortedGames.length)} of{' '}
                            {filteredAndSortedGames.length}
                          </div>
                          <div className="flex items-center gap-2">
                            <Button
                              variant="default"
                              size="sm"
                              onClick={() => setCurrentPage((prev) => Math.max(1, prev - 1))}
                              disabled={currentPage === 1}
                            >
                              Previous
                            </Button>
                            <div className="flex items-center gap-1">
                              {Array.from({ length: Math.min(5, totalPages) }, (_, i) => {
                                let pageNum: number;
                                if (totalPages <= 5) {
                                  pageNum = i + 1;
                                } else if (currentPage <= 3) {
                                  pageNum = i + 1;
                                } else if (currentPage >= totalPages - 2) {
                                  pageNum = totalPages - 4 + i;
                                } else {
                                  pageNum = currentPage - 2 + i;
                                }
                                return (
                                  <button
                                    key={pageNum}
                                    onClick={() => setCurrentPage(pageNum)}
                                    className={`px-3 py-1 rounded text-sm transition-colors ${
                                      currentPage === pageNum
                                        ? 'text-themed-bg font-semibold'
                                        : 'text-themed-secondary hover:text-themed-primary'
                                    }`}
                                    style={
                                      currentPage === pageNum
                                        ? {
                                            backgroundColor: 'var(--theme-accent)'
                                          }
                                        : {}
                                    }
                                  >
                                    {pageNum}
                                  </button>
                                );
                              })}
                            </div>
                            <Button
                              variant="default"
                              size="sm"
                              onClick={() =>
                                setCurrentPage((prev) => Math.min(totalPages, prev + 1))
                              }
                              disabled={currentPage === totalPages}
                            >
                              Next
                            </Button>
                          </div>
                        </div>
                      )}
                    </>
                  )}
                </>
              )}

              {/* Services List */}
              {!loading && totalServices > 0 && (
                <>
                  <div
                    className="mb-3 p-3 rounded-lg border"
                    style={{
                      backgroundColor: 'var(--theme-bg-elevated)',
                      borderColor: 'var(--theme-border-secondary)'
                    }}
                  >
                    <div className="flex items-center gap-2 text-themed-primary font-medium">
                      <Database className="w-5 h-5 text-themed-accent" />
                      Found {totalServices} service{totalServices !== 1 ? 's' : ''} with cache files
                    </div>
                  </div>

                  <div className="space-y-3">
                    {services.map((service) => (
                      <div
                        key={service.service_name}
                        className="rounded-lg border"
                        style={{
                          backgroundColor: 'var(--theme-bg-tertiary)',
                          borderColor: 'var(--theme-border-secondary)'
                        }}
                      >
                        <div className="flex items-center gap-2 p-3">
                          <Button
                            onClick={() => toggleServiceDetails(service.service_name)}
                            variant="subtle"
                            size="sm"
                            className="flex-shrink-0"
                            disabled={expandingServiceName === service.service_name}
                          >
                            {expandingServiceName === service.service_name ? (
                              <Loader2 className="w-4 h-4 animate-spin" />
                            ) : expandedServiceName === service.service_name ? (
                              <ChevronUp className="w-4 h-4" />
                            ) : (
                              <ChevronDown className="w-4 h-4" />
                            )}
                          </Button>
                          <div className="flex-1 min-w-0">
                            <div className="flex items-center gap-2 mb-1">
                              <h4 className="text-themed-primary font-semibold truncate capitalize">
                                {service.service_name}
                              </h4>
                            </div>
                            <div className="flex items-center gap-3 text-xs text-themed-muted flex-wrap">
                              <span className="flex items-center gap-1">
                                <FolderOpen className="w-3 h-3" />
                                <strong className="text-themed-primary">
                                  {service.cache_files_found.toLocaleString()}
                                </strong>{' '}
                                files
                              </span>
                              <span className="flex items-center gap-1">
                                <HardDrive className="w-3 h-3" />
                                <strong className="text-themed-primary">
                                  {formatBytes(service.total_size_bytes)}
                                </strong>
                              </span>
                            </div>
                          </div>
                          <Tooltip content="Remove all cache files for this service">
                            <Button
                              onClick={() => handleServiceRemoveClick(service)}
                              disabled={
                                mockMode ||
                                notifications.some(
                                  (n) =>
                                    n.type === 'service_removal' &&
                                    n.details?.service === service.service_name &&
                                    n.status === 'running'
                                ) ||
                                !isAuthenticated ||
                                cacheReadOnly ||
                                checkingPermissions
                              }
                              variant="filled"
                              color="red"
                              size="sm"
                              loading={notifications.some(
                                (n) =>
                                  n.type === 'service_removal' &&
                                  n.details?.service === service.service_name &&
                                  n.status === 'running'
                              )}
                              title={
                                cacheReadOnly
                                  ? 'Cache directory is mounted read-only'
                                  : undefined
                              }
                            >
                              {notifications.some(
                                (n) =>
                                  n.type === 'service_removal' &&
                                  n.details?.service === service.service_name &&
                                  n.status === 'running'
                              )
                                ? 'Removing...'
                                : 'Remove'}
                            </Button>
                          </Tooltip>
                        </div>

                        {/* Loading State for Expansion */}
                        {expandingServiceName === service.service_name && (
                          <div
                            className="border-t px-3 py-4 flex items-center justify-center"
                            style={{ borderColor: 'var(--theme-border-secondary)' }}
                          >
                            <div className="flex items-center gap-2 text-themed-muted">
                              <Loader2 className="w-4 h-4 animate-spin" />
                              <span className="text-sm">Loading details...</span>
                            </div>
                          </div>
                        )}

                        {/* Expandable Details Section */}
                        {expandedServiceName === service.service_name &&
                          expandingServiceName !== service.service_name && (
                            <div
                              className="border-t px-3 py-3 space-y-3"
                              style={{ borderColor: 'var(--theme-border-secondary)' }}
                            >
                              {/* Sample URLs */}
                              {service.sample_urls.length > 0 && (
                                <div>
                                  <div className="flex items-center justify-between mb-1.5">
                                    <p className="text-xs text-themed-muted font-medium">
                                      Sample URLs ({service.sample_urls.length}):
                                    </p>
                                    {service.sample_urls.length > MAX_INITIAL_URLS && (
                                      <Button
                                        variant="subtle"
                                        size="xs"
                                        onClick={() => toggleShowAllUrls(service.service_name)}
                                        className="text-xs"
                                      >
                                        {showAllUrls[service.service_name]
                                          ? `Show less`
                                          : `Show all ${service.sample_urls.length}`}
                                      </Button>
                                    )}
                                  </div>
                                  <div className="space-y-1 max-h-48 overflow-y-auto">
                                    {(showAllUrls[service.service_name]
                                      ? service.sample_urls
                                      : service.sample_urls.slice(0, MAX_INITIAL_URLS)
                                    ).map((url, idx) => (
                                      <div
                                        key={idx}
                                        className="p-2 rounded border"
                                        style={{
                                          backgroundColor: 'var(--theme-bg-secondary)',
                                          borderColor: 'var(--theme-border-primary)'
                                        }}
                                      >
                                        <Tooltip content={url}>
                                          <span className="text-xs font-mono text-themed-primary truncate block">
                                            {url}
                                          </span>
                                        </Tooltip>
                                      </div>
                                    ))}
                                  </div>
                                  {!showAllUrls[service.service_name] &&
                                    service.sample_urls.length > MAX_INITIAL_URLS && (
                                      <p className="text-xs text-themed-muted mt-2 italic">
                                        Showing {MAX_INITIAL_URLS} of {service.sample_urls.length}{' '}
                                        URLs
                                      </p>
                                    )}
                                </div>
                              )}

                              {/* Cache File Paths */}
                              {service.cache_file_paths && service.cache_file_paths.length > 0 && (
                                <div>
                                  <div className="flex items-center justify-between mb-1.5">
                                    <p className="text-xs text-themed-muted font-medium">
                                      Cache File Locations (
                                      {service.cache_file_paths.length.toLocaleString()}):
                                    </p>
                                    {service.cache_file_paths.length > MAX_INITIAL_PATHS && (
                                      <Button
                                        variant="subtle"
                                        size="xs"
                                        onClick={() => toggleShowAllPaths(service.service_name)}
                                        className="text-xs"
                                      >
                                        {showAllPaths[service.service_name]
                                          ? `Show less`
                                          : `Show all ${service.cache_file_paths.length.toLocaleString()}`}
                                      </Button>
                                    )}
                                  </div>
                                  <div className="space-y-1 max-h-48 overflow-y-auto">
                                    {(showAllPaths[service.service_name]
                                      ? service.cache_file_paths
                                      : service.cache_file_paths.slice(0, MAX_INITIAL_PATHS)
                                    ).map((path, idx) => (
                                      <div
                                        key={idx}
                                        className="p-2 rounded border"
                                        style={{
                                          backgroundColor: 'var(--theme-bg-secondary)',
                                          borderColor: 'var(--theme-border-primary)'
                                        }}
                                      >
                                        <Tooltip content={path}>
                                          <span className="text-xs font-mono text-themed-primary truncate block">
                                            {path}
                                          </span>
                                        </Tooltip>
                                      </div>
                                    ))}
                                  </div>
                                  {!showAllPaths[service.service_name] &&
                                    service.cache_file_paths.length > MAX_INITIAL_PATHS && (
                                      <p className="text-xs text-themed-muted mt-2 italic">
                                        Showing {MAX_INITIAL_PATHS} of{' '}
                                        {service.cache_file_paths.length.toLocaleString()} paths
                                      </p>
                                    )}
                                </div>
                              )}
                            </div>
                          )}
                      </div>
                    ))}
                  </div>
                </>
              )}

              {/* Empty State */}
              {!loading && totalGames === 0 && totalServices === 0 && games.length === 0 && services.length === 0 && !error && (
                <div className="text-center py-8 text-themed-muted">
                  <HardDrive className="w-12 h-12 mx-auto mb-3 opacity-50" />
                  <div className="mb-2">No games or services with cache files detected</div>
                  {!hasProcessedLogs && !checkingLogs ? (
                    <div className="text-xs space-y-1">
                      <div className="text-themed-warning font-medium">
                        Database has no LogEntries
                      </div>
                      <div>
                        Process access logs to populate the database. Detection requires
                        LogEntries to match cache files.
                      </div>
                    </div>
                  ) : (
                    <div className="text-xs">
                      Click &ldquo;Detect Games&rdquo; to scan your cache directory
                    </div>
                  )}
                </div>
              )}

              {/* Information Alert */}
              <Alert color="blue" className="about-section">
                <div>
                  <p className="text-xs font-medium mb-2">About Game & Service Cache Detection:</p>
                  <ul className="list-disc list-inside text-xs space-y-1 ml-2">
                    <li>
                      <strong>Requires processed logs:</strong> Access logs must be processed first
                      to populate the database
                    </li>
                    <li>Scans database for game and service records and checks if cache files exist</li>
                    <li>Shows total cache size and file count per game/service</li>
                    <li>Removal deletes ALL cache files, log entries, and database records for the selected item</li>
                    <li>
                      Service removal cleans up cache for non-game services (riot, blizzard, epic, wsus, etc.)
                    </li>
                  </ul>
                </div>
              </Alert>
            </>
          )}
        </div>
      </Card>

      {/* Game Removal Confirmation Modal */}
      <Modal
        opened={gameToRemove !== null}
        onClose={() => setGameToRemove(null)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Remove Game from Cache</span>
          </div>
        }
      >
        {gameToRemove && (
          <div className="space-y-4">
            <p className="text-themed-secondary">
              Are you sure you want to remove{' '}
              <span className="font-semibold text-themed-primary">{gameToRemove.game_name}</span>{' '}
              from cache?
            </p>

            <Alert color="yellow">
              <div>
                <p className="text-xs font-medium mb-2">This will:</p>
                <ul className="list-disc list-inside text-xs space-y-1 ml-2">
                  <li>
                    Delete approximately {gameToRemove.cache_files_found.toLocaleString()} cache
                    files
                  </li>
                  <li>Free up approximately {formatBytes(gameToRemove.total_size_bytes)}</li>
                  <li>
                    Remove cache for {gameToRemove.depot_ids.length} depot
                    {gameToRemove.depot_ids.length !== 1 ? 's' : ''}
                  </li>
                  <li>Progress will be shown in the notification bar at the top</li>
                  <li>This action cannot be undone</li>
                </ul>
              </div>
            </Alert>

            <div className="flex justify-end gap-3 pt-2">
              <Button variant="default" onClick={() => setGameToRemove(null)}>
                Cancel
              </Button>
              <Button
                variant="filled"
                color="red"
                leftSection={<Trash2 className="w-4 h-4" />}
                onClick={confirmRemoval}
              >
                Remove from Cache
              </Button>
            </div>
          </div>
        )}
      </Modal>

      {/* Service Removal Confirmation Modal */}
      <Modal
        opened={serviceToRemove !== null}
        onClose={() => setServiceToRemove(null)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Remove Service from Cache</span>
          </div>
        }
      >
        {serviceToRemove && (
          <div className="space-y-4">
            <p className="text-themed-secondary">
              Are you sure you want to remove all cache files for{' '}
              <span className="font-semibold text-themed-primary capitalize">{serviceToRemove.service_name}</span>{' '}
              service?
            </p>

            <Alert color="yellow">
              <div>
                <p className="text-xs font-medium mb-2">This will:</p>
                <ul className="list-disc list-inside text-xs space-y-1 ml-2">
                  <li>
                    Delete approximately {serviceToRemove.cache_files_found.toLocaleString()} cache
                    files
                  </li>
                  <li>Free up approximately {formatBytes(serviceToRemove.total_size_bytes)}</li>
                  <li>Remove ALL log entries for this service from the database</li>
                  <li>Remove ALL download records for this service from the database</li>
                  <li>Progress will be shown in the notification bar at the top</li>
                  <li>This action cannot be undone</li>
                </ul>
              </div>
            </Alert>

            <div className="flex justify-end gap-3 pt-2">
              <Button variant="default" onClick={() => setServiceToRemove(null)}>
                Cancel
              </Button>
              <Button
                variant="filled"
                color="red"
                leftSection={<Trash2 className="w-4 h-4" />}
                onClick={confirmServiceRemoval}
              >
                Remove from Cache
              </Button>
            </div>
          </div>
        )}
      </Modal>
    </>
  );
};

export default GameCacheDetector;
