import React, { useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { HardDrive, Loader2, Database, Server } from 'lucide-react';
import ApiService from '@services/api.service';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { AccordionSection } from '@components/ui/AccordionSection';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { useNotifications } from '@contexts/notifications';
import { useDockerSocket } from '@contexts/DockerSocketContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import {
  ManagerCardHeader,
  LoadingState,
  EmptyState,
  ReadOnlyBadge
} from '@components/ui/ManagerCard';
import GamesList from './GamesList';
import ServicesList from './ServicesList';
import CacheRemovalModal from '@components/modals/cache/CacheRemovalModal';
import type { GameCacheInfo, ServiceCacheInfo, DatasourceInfo } from '../../../../types';

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
  const { t } = useTranslation();
  const { addNotification, updateNotification, notifications, isAnyRemovalRunning } = useNotifications();
  const { isDockerAvailable } = useDockerSocket();

  // Derive game detection state from notifications (standardized pattern)
  const activeGameDetectionNotification = notifications.find(
    n => n.type === 'game_detection' && n.status === 'running'
  );
  const isDetectionFromNotification = !!activeGameDetectionNotification;

  // Track local starting state for immediate UI feedback before SignalR events arrive
  const [isStartingDetection, setIsStartingDetection] = useState(false);
  // Track local loading state for loading cached data (quick synchronous operation)
  const [isLoadingData, setIsLoadingData] = useState(false);
  // Ref to prevent duplicate API calls (handles rapid button clicks before state updates)
  const detectionInFlightRef = useRef(false);
  // Combined loading state: either notification says running OR we're in starting phase OR loading cached data
  const loading = isDetectionFromNotification || isStartingDetection || isLoadingData;
  const [games, setGames] = useState<GameCacheInfo[]>([]);
  const [services, setServices] = useState<ServiceCacheInfo[]>([]);
  const [gameToRemove, setGameToRemove] = useState<GameCacheInfo | null>(null);
  const [serviceToRemove, setServiceToRemove] = useState<ServiceCacheInfo | null>(null);
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [checkingPermissions, setCheckingPermissions] = useState(false);
  const [hasProcessedLogs, setHasProcessedLogs] = useState(false);
  const [checkingLogs, setCheckingLogs] = useState(false);
  const [lastDetectionTime, setLastDetectionTime] = useState<string | null>(null);
  const [scanType, setScanType] = useState<'full' | 'incremental' | 'load' | null>(null);
  const [datasources, setDatasources] = useState<DatasourceInfo[]>([]);
  const [selectedDatasource, setSelectedDatasource] = useState<string | null>(null);

  // Accordion state for Services and Games sections
  const [servicesExpanded, setServicesExpanded] = useState(true);
  const [gamesExpanded, setGamesExpanded] = useState(true);

  // Format last detection time with timezone awareness
  const formattedLastDetectionTime = useFormattedDateTime(lastDetectionTime);

  // Filter games and services by selected datasource
  // Note: Items with empty/missing datasources (legacy data) are shown regardless of filter
  const filteredGames = selectedDatasource
    ? games.filter((g) => !g.datasources?.length || g.datasources.includes(selectedDatasource))
    : games;
  const filteredServices = selectedDatasource
    ? services.filter((s) => !s.datasources?.length || s.datasources.includes(selectedDatasource))
    : services;

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
              parts.push(`${result.totalGamesDetected} game${result.totalGamesDetected !== 1 ? 's' : ''}`);
            }
            if (result.totalServicesDetected && result.totalServicesDetected > 0) {
              parts.push(`${result.totalServicesDetected} service${result.totalServicesDetected !== 1 ? 's' : ''}`);
            }

            if (parts.length > 0) {
              addNotification({
                type: 'generic',
                status: 'completed',
                message: t('management.gameDetection.loadedPreviousResults', { results: parts.join(' and ') }),
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
        // Silent fail - not critical
      }
    };

    loadCachedGames();
    if (refreshKey === 0) {
      // Only check permissions, logs, and datasources on initial mount
      loadDirectoryPermissions();
      loadDatasources();
      checkIfLogsProcessed(); // Check database for LogEntries
      // Note: Recovery is now handled by NotificationsContext's recoverGameDetection
      // which queries the backend and creates the notification on page load
    }
  }, [mockMode, refreshKey]); // Re-run when mockMode or refreshKey changes

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

  const loadDatasources = async () => {
    try {
      const config = await ApiService.getConfig();
      if (config.dataSources && config.dataSources.length > 0) {
        setDatasources(config.dataSources);
      } else {
        // Fallback to default datasource from config
        setDatasources([{
          name: 'default',
          cachePath: config.cachePath || '/cache',
          logsPath: config.logsPath || '/logs',
          cacheWritable: config.cacheWritable ?? false,
          logsWritable: config.logsWritable ?? false,
          enabled: true
        }]);
      }
    } catch (err) {
      console.error('Failed to load datasources:', err);
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
      setGames((prev) => prev.filter((g) => g.game_app_id !== gameAppId));
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
    });

    // Handle database reset completion
    const databaseResetNotifs = notifications.filter(
      (n) => n.type === 'database_reset' && n.status === 'completed'
    );
    if (databaseResetNotifs.length > 0) {
      setGames([]);
      setServices([]);
      checkIfLogsProcessed();
    }

    // Handle log processing completion
    const logProcessingNotifs = notifications.filter(
      (n) => n.type === 'log_processing' && n.status === 'completed'
    );
    if (logProcessingNotifs.length > 0) {
      checkIfLogsProcessed();
    }

    // Handle game detection completion - ONLY if we were starting detection
    if (isStartingDetection) {
      const gameDetectionNotifs = notifications.filter(
        (n) => n.type === 'game_detection' && n.status === 'completed'
      );
      if (gameDetectionNotifs.length > 0) {
        setIsStartingDetection(false);
        setScanType(null);
        // Reset ref to allow future detection calls
        detectionInFlightRef.current = false;

        // Note: Operation state now handled by NotificationsContext

        // Load fresh results from the database (backend already saved them)
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
          } catch (err) {
            console.error('[GameCacheDetector] Failed to load detection results:', err);
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
  }, [notifications, isStartingDetection]);

  const startDetection = useCallback(async (forceRefresh: boolean, scanTypeLabel: 'full' | 'incremental') => {
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
      console.log('[GameCacheDetector] Detection already in progress, ignoring duplicate call');
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
      const errorMsg = (err instanceof Error ? err.message : String(err)) || t('management.gameDetection.failedToStartDetection');
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
  }, [mockMode, loading, t, addNotification]);

  const handleFullScan = useCallback(() => startDetection(true, 'full'), [startDetection]);
  const handleIncrementalScan = useCallback(() => startDetection(false, 'incremental'), [startDetection]);

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
          parts.push(`${result.totalGamesDetected} game${result.totalGamesDetected !== 1 ? 's' : ''}`);
        }
        if (result.totalServicesDetected && result.totalServicesDetected > 0) {
          parts.push(`${result.totalServicesDetected} service${result.totalServicesDetected !== 1 ? 's' : ''}`);
        }

        if (parts.length > 0) {
          addNotification({
            type: 'generic',
            status: 'completed',
            message: t('management.gameDetection.loadedFromPreviousScan', { results: parts.join(' and ') }),
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
    if (!isAuthenticated) {
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

    // Add notification for tracking (shows in notification bar and on Remove button)
    // Note: ID will be "game_removal-{gameAppId}" for SignalR handler to find it
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
      await ApiService.removeGameFromCache(gameAppId);

      // Fire-and-forget: API returned 202 Accepted, removal is happening in background
      // Game will be removed from list when SignalR GameRemovalComplete event arrives

      // Trigger a refetch after removal likely completes to refresh downloads
      setTimeout(() => {
        onDataRefresh?.();
      }, 30000); // Refresh after 30 seconds
    } catch (err: unknown) {
      const errorMsg = (err instanceof Error ? err.message : String(err)) || t('management.gameDetection.failedToRemoveGame');

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
      const errorMsg = (err instanceof Error ? err.message : String(err)) || t('management.gameDetection.failedToRemoveService');

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
    const allExpanded = servicesExpanded && gamesExpanded;
    setServicesExpanded(!allExpanded);
    setGamesExpanded(!allExpanded);
  };

  const hasResults = filteredGames.length > 0 || filteredServices.length > 0;
  const allExpanded = servicesExpanded && gamesExpanded;

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={340}>
      <HelpSection title={t('management.gameDetection.help.removal.title')}>
        <div className="space-y-1.5">
          <HelpDefinition term={t('management.gameDetection.help.removal.game.term')} termColor="green">
            {t('management.gameDetection.help.removal.game.description')}
          </HelpDefinition>
          <HelpDefinition term={t('management.gameDetection.help.removal.service.term')} termColor="purple">
            {t('management.gameDetection.help.removal.service.description')}
          </HelpDefinition>
        </div>
      </HelpSection>

      <HelpSection title={t('management.gameDetection.help.howItWorks.title')} variant="subtle">
        {t('management.gameDetection.help.howItWorks.description')}
      </HelpSection>

      <HelpNote type="info">
        {t('management.gameDetection.help.note')}
      </HelpNote>
    </HelpPopover>
  );

  // Header actions - scan buttons
  const headerActions = (
    <div className="flex items-center gap-2">
      <Tooltip content={t('management.gameDetection.loadPreviousResults')}>
        <Button
          onClick={handleLoadData}
          disabled={loading || mockMode || checkingPermissions}
          variant="subtle"
          size="sm"
        >
          {loading && scanType === 'load' ? <Loader2 className="w-4 h-4 animate-spin" /> : t('common.load')}
        </Button>
      </Tooltip>

      <Tooltip content={!hasProcessedLogs && !checkingLogs ? t('management.gameDetection.processLogsFirst') : t('management.gameDetection.quickScan')}>
        <Button
          onClick={handleIncrementalScan}
          disabled={loading || mockMode || checkingPermissions || !hasProcessedLogs || checkingLogs}
          variant="subtle"
          size="sm"
        >
          {loading && scanType === 'incremental' ? <Loader2 className="w-4 h-4 animate-spin" /> : t('management.gameDetection.quick')}
        </Button>
      </Tooltip>

      <Tooltip content={!hasProcessedLogs && !checkingLogs ? t('management.gameDetection.processLogsFirst') : t('management.gameDetection.fullScan')}>
        <Button
          onClick={handleFullScan}
          disabled={loading || mockMode || checkingPermissions || !hasProcessedLogs || checkingLogs}
          variant="filled"
          color="blue"
          size="sm"
        >
          {loading && scanType === 'full' ? <Loader2 className="w-4 h-4 animate-spin" /> : t('management.gameDetection.fullScanButton')}
        </Button>
      </Tooltip>
    </div>
  );

  return (
    <>
      <Card>
        <div className="space-y-4">
          <ManagerCardHeader
            icon={HardDrive}
            iconColor="purple"
            title={t('management.gameDetection.title')}
            subtitle={t('management.gameDetection.subtitle')}
            helpContent={helpContent}
            permissions={{
              logsReadOnly: datasources.some(ds => !ds.logsWritable),
              cacheReadOnly,
              checkingPermissions
            }}
            actions={headerActions}
          />

          {/* Read-Only Warning */}
          {cacheReadOnly && (
            <>
              <Alert color="orange" className="mb-2">
                <div>
                  <p className="font-medium">{t('management.gameDetection.alerts.cacheReadOnly.title')}</p>
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
                  { value: '', label: t('management.gameDetection.placeholders.allDatasources') },
                  ...datasources.map((ds): DropdownOption => ({
                    value: ds.name,
                    label: ds.name
                  }))
                ]}
                value={selectedDatasource || ''}
                onChange={(value) => setSelectedDatasource(value || null)}
                placeholder={t('management.gameDetection.placeholders.allDatasources')}
                compactMode
                cleanStyle
                prefix={t('management.gameDetection.filterPrefix')}
              />
            </div>
          )}

          {/* Loading State */}
          {loading && (
            <LoadingState
              message={datasources.length > 1
                ? t('management.gameDetection.scanningMultipleDatasources', { count: datasources.length })
                : t('management.gameDetection.scanningSingle')}
              submessage={t('management.gameDetection.scanningNote')}
            />
          )}

          {!cacheReadOnly && !loading && (
            <>
              {/* Previous Results Badge */}
              {lastDetectionTime && hasResults && (
                <Alert color="blue">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <span className="text-sm font-medium">
                        {t('common.resultsFromPreviousScan')}
                      </span>
                      <span className="text-xs text-themed-muted">
                        {formattedLastDetectionTime}
                      </span>
                    </div>
                    {/* Expand/Collapse All button */}
                    {hasResults && (
                      <Button
                        variant="subtle"
                        size="xs"
                        onClick={handleExpandCollapseAll}
                      >
                        {allExpanded ? t('management.gameDetection.collapseAll') : t('management.gameDetection.expandAll')}
                      </Button>
                    )}
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
                  iconColor="var(--theme-accent)"
                  isExpanded={servicesExpanded}
                  onToggle={() => setServicesExpanded(!servicesExpanded)}
                >
                  <ServicesList
                    services={filteredServices}
                    totalServices={filteredServices.length}
                    notifications={notifications}
                    isAnyRemovalRunning={isAnyRemovalRunning}
                    isAuthenticated={isAuthenticated}
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
                  iconColor="var(--theme-success-text)"
                  isExpanded={gamesExpanded}
                  onToggle={() => setGamesExpanded(!gamesExpanded)}
                >
                  <GamesList
                    games={filteredGames}
                    totalGames={filteredGames.length}
                    notifications={notifications}
                    isAnyRemovalRunning={isAnyRemovalRunning}
                    isAuthenticated={isAuthenticated}
                    cacheReadOnly={cacheReadOnly}
                    dockerSocketAvailable={isDockerAvailable}
                    checkingPermissions={checkingPermissions}
                    onRemoveGame={handleRemoveClick}
                  />
                </AccordionSection>
              )}

              {/* Empty State */}
              {!hasResults && !loading && (
                <EmptyState
                  icon={HardDrive}
                  title={selectedDatasource
                    ? t('management.gameDetection.emptyState.noGamesServicesDatasource', { datasource: selectedDatasource })
                    : t('management.gameDetection.emptyState.noGamesServices')}
                  subtitle={!hasProcessedLogs && !checkingLogs
                    ? t('management.gameDetection.emptyState.processLogsFirst')
                    : t('management.gameDetection.emptyState.clickFullScan')}
                  action={selectedDatasource ? (
                    <Button
                      variant="subtle"
                      size="sm"
                      onClick={() => setSelectedDatasource(null)}
                    >
                      {t('management.gameDetection.clearFilter')}
                    </Button>
                  ) : undefined}
                />
              )}
            </>
          )}
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
    </>
  );
};

export default GameCacheDetector;
