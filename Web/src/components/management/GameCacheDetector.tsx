import React, { useState, useEffect } from 'react';
import { HardDrive, Loader2, Lock } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { useNotifications } from '@contexts/NotificationsContext';
import { useBackendOperation } from '@hooks/useBackendOperation';
import { formatDateTime } from '@utils/formatters';
import GamesList from './game-cache-detector/GamesList';
import ServicesList from './game-cache-detector/ServicesList';
import GameRemovalModal from './game-cache-detector/GameRemovalModal';
import ServiceRemovalModal from './game-cache-detector/ServiceRemovalModal';
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
  const { addNotification, updateNotification, notifications } = useNotifications();
  const gameDetectionOp = useBackendOperation('activeGameDetection', 'gameDetection', 120);
  const [loading, setLoading] = useState(false);
  const [games, setGames] = useState<GameCacheInfo[]>([]);
  const [services, setServices] = useState<ServiceCacheInfo[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [totalGames, setTotalGames] = useState<number>(0);
  const [totalServices, setTotalServices] = useState<number>(0);
  const [gameToRemove, setGameToRemove] = useState<GameCacheInfo | null>(null);
  const [serviceToRemove, setServiceToRemove] = useState<ServiceCacheInfo | null>(null);
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [hasProcessedLogs, setHasProcessedLogs] = useState(false);
  const [checkingLogs, setCheckingLogs] = useState(true);
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [scanType, setScanType] = useState<'full' | 'incremental' | 'load' | null>(null);


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

          // Store the last detection time
          if (result.lastDetectionTime) {
            setLastDetectionTime(result.lastDetectionTime);
          }

          // Only show "Loaded previous results" notification if we're NOT actively running a scan
          // Don't show it during full scan or quick scan - only when loading existing data
          const isActivelyScanning = loading || scanType === 'full' || scanType === 'incremental';

          if (!isActivelyScanning) {
            const parts = [];
            if (result.totalGamesDetected && result.totalGamesDetected > 0) {
              parts.push(`${result.totalGamesDetected} game${result.totalGamesDetected !== 1 ? 's' : ''}`);
            }
            if (result.totalServicesDetected && result.totalServicesDetected > 0) {
              parts.push(`${result.totalServicesDetected} service${result.totalServicesDetected !== 1 ? 's' : ''}`);
            }

            if (parts.length > 0) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: `Loaded previous results: ${parts.join(' and ')}`,
                details: { notificationType: 'info' }
              });
            }
          }
        } else {
          // No cached results - clear the display
          setGames([]);
          setTotalGames(0);
          setServices([]);
          setTotalServices(0);
          setLastDetectionTime(null);
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
        if (data.operationId && data.scanType) {
          console.log('[GameCacheDetector] Restoring interrupted game detection operation');
          setLoading(true);
          setScanType(data.scanType);
          // SignalR will handle the completion when it arrives
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
      setGames((prev) => {
        const newGames = prev.filter((g) => g.game_app_id !== gameAppId);
        setTotalGames(newGames.length);
        return newGames;
      });
    });

    // Handle completed service removals
    const serviceRemovalNotifs = notifications.filter(
      (n) => n.type === 'service_removal' && n.status === 'completed'
    );
    serviceRemovalNotifs.forEach((notif) => {
      const serviceName = notif.details?.service;
      if (!serviceName) return;

      // Remove from UI (backend already removed from database)
      setServices((prev) => {
        const newServices = prev.filter((s) => s.service_name !== serviceName);
        setTotalServices(newServices.length);
        return newServices;
      });
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

    // Handle game detection completion
    const gameDetectionNotifs = notifications.filter(
      (n) => n.type === 'game_detection' && n.status === 'completed'
    );
    if (gameDetectionNotifs.length > 0) {
      console.log('[GameCacheDetector] Game detection completed, loading results from database');
      setLoading(false);
      setScanType(null);

      // Clear operation state - detection is complete
      gameDetectionOp.clear().catch((err) => console.error('Failed to clear operation state:', err));

      // Load fresh results from the database (backend already saved them)
      const loadResults = async () => {
        try {
          const result = await ApiService.getCachedGameDetection();
          if (result.hasCachedResults) {
            if (result.games && result.totalGamesDetected) {
              setGames(result.games);
              setTotalGames(result.totalGamesDetected);
            }
            if (result.services && result.totalServicesDetected) {
              setServices(result.services);
              setTotalServices(result.totalServicesDetected);
            }
            if (result.lastDetectionTime) {
              setLastDetectionTime(result.lastDetectionTime);
            }
          }
        } catch (err) {
          console.error('[GameCacheDetector] Failed to load detection results:', err);
        }
      };
      loadResults();
    }

    // Handle game detection failure
    const gameDetectionFailedNotifs = notifications.filter(
      (n) => n.type === 'game_detection' && n.status === 'failed'
    );
    if (gameDetectionFailedNotifs.length > 0) {
      console.error('[GameCacheDetector] Game detection failed');
      setLoading(false);
      setScanType(null);

      // Clear operation state - detection failed
      gameDetectionOp.clear().catch((err) => console.error('Failed to clear operation state:', err));
    }
  }, [notifications]);

  const startDetection = async (forceRefresh: boolean, scanTypeLabel: 'full' | 'incremental') => {
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
    setScanType(scanTypeLabel);
    setGames([]);
    setTotalGames(0);
    setServices([]);
    setTotalServices(0);
    setLastDetectionTime(null); // Clear previous detection time when starting new scan

    try {
      // Start background detection - SignalR will send GameDetectionStarted event
      const result = await ApiService.startGameCacheDetection(forceRefresh);

      // Save operation state for restoration on page refresh
      await gameDetectionOp.save({ operationId: result.operationId, scanType: scanTypeLabel });
      // Success! SignalR notifications will handle the rest
    } catch (err: any) {
      const errorMsg = err.message || 'Failed to start detection';
      setError(errorMsg);
      addNotification({
        type: 'generic',
        status: 'failed',
        message: errorMsg,
        details: { notificationType: 'error' }
      });
      console.error('Detection error:', err);
      setLoading(false);
      setScanType(null);
    }
  };

  const handleFullScan = () => startDetection(true, 'full');
  const handleIncrementalScan = () => startDetection(false, 'incremental');

  const handleLoadData = async () => {
    if (mockMode) return;

    setScanType('load');
    setLoading(true);
    setError(null);

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

        // Store the last detection time
        if (result.lastDetectionTime) {
          setLastDetectionTime(result.lastDetectionTime);
        }

        // Show notification
        const parts = [];
        if (result.totalGamesDetected && result.totalGamesDetected > 0) {
          parts.push(`${result.totalGamesDetected} game${result.totalGamesDetected !== 1 ? 's' : ''}`);
        }
        if (result.totalServicesDetected && result.totalServicesDetected > 0) {
          parts.push(`${result.totalServicesDetected} service${result.totalServicesDetected !== 1 ? 's' : ''}`);
        }

        if (parts.length > 0) {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: `Loaded ${parts.join(' and ')} from previous scan`,
            details: { notificationType: 'success' }
          });
        } else {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: 'No previous detection results found',
            details: { notificationType: 'info' }
          });
        }
      } else {
        setGames([]);
        setTotalGames(0);
        setServices([]);
        setTotalServices(0);
        setLastDetectionTime(null);
        addNotification({
          type: 'generic',
          status: 'completed',
          message: 'No previous detection results found',
          details: { notificationType: 'info' }
        });
      }
    } catch (err) {
      console.error('[GameCacheDetector] Failed to load data:', err);
      setError('Failed to load previous results');
      addNotification({
        type: 'generic',
        status: 'failed',
        message: 'Failed to load previous results',
        details: { notificationType: 'error' }
      });
    } finally {
      setLoading(false);
      setScanType(null);
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
              <div className="flex items-center gap-2">
                {/* Load Data Button */}
                <Tooltip content="Load previous detection results from database">
                  <Button
                    onClick={handleLoadData}
                    disabled={loading || mockMode || cacheReadOnly || checkingPermissions}
                    variant="default"
                    size="sm"
                    leftSection={loading && scanType === 'load' ? <Loader2 className="w-4 h-4 animate-spin" /> : undefined}
                  >
                    {loading && scanType === 'load' ? 'Loading...' : 'Load Data'}
                  </Button>
                </Tooltip>

                {/* Incremental Scan Button */}
                {(() => {
                  const incrementalButton = (
                    <Button
                      onClick={handleIncrementalScan}
                      disabled={
                        loading ||
                        mockMode ||
                        cacheReadOnly ||
                        checkingPermissions ||
                        !hasProcessedLogs ||
                        checkingLogs
                      }
                      variant="default"
                      size="sm"
                      leftSection={loading && scanType === 'incremental' ? <Loader2 className="w-4 h-4 animate-spin" /> : undefined}
                    >
                      {loading && scanType === 'incremental' ? 'Scanning...' : 'Quick Scan'}
                    </Button>
                  );

                  return !hasProcessedLogs && !checkingLogs ? (
                    <Tooltip content="Process access logs first. LogEntries are required for detection.">
                      {incrementalButton}
                    </Tooltip>
                  ) : (
                    <Tooltip content="Scan for new games and services only (faster)">
                      {incrementalButton}
                    </Tooltip>
                  );
                })()}

                {/* Full Scan Button */}
                {(() => {
                  const fullButton = (
                    <Button
                      onClick={handleFullScan}
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
                      size="sm"
                      leftSection={loading && scanType === 'full' ? <Loader2 className="w-4 h-4 animate-spin" /> : undefined}
                    >
                      {loading && scanType === 'full' ? 'Scanning...' : 'Full Scan'}
                    </Button>
                  );

                  return !hasProcessedLogs && !checkingLogs ? (
                    <Tooltip content="Process access logs first. LogEntries are required for detection.">
                      {fullButton}
                    </Tooltip>
                  ) : (
                    <Tooltip content="Scan all games and services from scratch (slower)">
                      {fullButton}
                    </Tooltip>
                  );
                })()}
              </div>
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

              {/* Previous Results Badge */}
              {!loading && lastDetectionTime && (totalGames > 0 || totalServices > 0) && (
                <Alert color="blue">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium">
                      Showing results from previous scan
                    </span>
                    <span className="text-xs text-themed-muted">
                      Detected {formatDateTime(lastDetectionTime)}
                    </span>
                  </div>
                </Alert>
              )}

              {/* Services List - NOW APPEARS FIRST */}
              {!loading && (
                <ServicesList
                  services={services}
                  totalServices={totalServices}
                  notifications={notifications}
                  isAuthenticated={isAuthenticated}
                  cacheReadOnly={cacheReadOnly}
                  checkingPermissions={checkingPermissions}
                  onRemoveService={handleServiceRemoveClick}
                />
              )}

              {/* Games List - NOW APPEARS AFTER SERVICES */}
              {!loading && (
                <GamesList
                  games={games}
                  totalGames={totalGames}
                  notifications={notifications}
                  isAuthenticated={isAuthenticated}
                  cacheReadOnly={cacheReadOnly}
                  checkingPermissions={checkingPermissions}
                  onRemoveGame={handleRemoveClick}
                />
              )}

              {/* Empty State */}
              {!loading &&
                totalGames === 0 &&
                totalServices === 0 &&
                games.length === 0 &&
                services.length === 0 &&
                !error && (
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
                  <p className="text-xs font-medium mb-2">
                    About Game & Service Cache Detection:
                  </p>
                  <ul className="list-disc list-inside text-xs space-y-1 ml-2">
                    <li>
                      <strong>Requires processed logs:</strong> Access logs must be processed first
                      to populate the database
                    </li>
                    <li>
                      Scans database for game and service records and checks if cache files exist
                    </li>
                    <li>Shows total cache size and file count per game/service</li>
                    <li>
                      Removal deletes ALL cache files, log entries, and database records for the
                      selected item
                    </li>
                    <li>
                      Service removal cleans up cache for non-game services (riot, blizzard, epic,
                      wsus, etc.)
                    </li>
                  </ul>
                </div>
              </Alert>
            </>
          )}
        </div>
      </Card>

      {/* Game Removal Confirmation Modal */}
      <GameRemovalModal
        game={gameToRemove}
        onClose={() => setGameToRemove(null)}
        onConfirm={confirmRemoval}
      />

      {/* Service Removal Confirmation Modal */}
      <ServiceRemovalModal
        service={serviceToRemove}
        onClose={() => setServiceToRemove(null)}
        onConfirm={confirmServiceRemoval}
      />
    </>
  );
};

export default GameCacheDetector;
