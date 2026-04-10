import React, { useState, useCallback, useEffect, useRef } from 'react';
import './StorageSection.css';
import { useTranslation } from 'react-i18next';
import { RefreshCw, AlertTriangle, Archive, Sliders, Database } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Checkbox } from '@components/ui/Checkbox';
import { LoadingState } from '@components/ui/ManagerCard';
import { AccordionSection } from '@components/ui/AccordionSection';
import { type AuthMode } from '@services/auth.service';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { useDockerSocket } from '@contexts/useDockerSocket';
import { ImageCacheContext, ImageInvalidateContext } from '@components/common/ImageCacheContext';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useNotifications, NOTIFICATION_IDS } from '@contexts/notifications';
import { useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import CacheRemovalModal from '@components/modals/cache/CacheRemovalModal';
import EvictedItemsList from '../game-detection/EvictedItemsList';
import DatasourcesManager from '../datasources/DatasourcesInfo';
import LogRemovalManager from '../log-processing/LogRemovalManager';
import CacheManager from '../cache/CacheManager';
import CorruptionManager from '../cache/CorruptionManager';
import GameCacheDetector from '../game-detection/GameCacheDetector';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';
interface StorageSectionProps {
  isAdmin: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  gameCacheRefreshKey: number;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  onDataRefresh: () => void;
}

const StorageSection: React.FC<StorageSectionProps> = ({
  isAdmin,
  authMode,
  mockMode,
  gameCacheRefreshKey,
  onError,
  onSuccess,
  onDataRefresh
}) => {
  const { t } = useTranslation();
  const {
    logsReadOnly,
    cacheReadOnly,
    checkingPermissions,
    reload: reloadPermissions
  } = useDirectoryPermissions();
  const { isDockerAvailable } = useDockerSocket();
  const [isRechecking, setIsRechecking] = useState(false);

  // Image cache busting for GameCacheDetector's GameImage components
  const [imageCacheVersion, setImageCacheVersion] = useState(() => Date.now());
  const invalidateImageCache = useCallback(() => setImageCacheVersion(Date.now()), []);

  // Eviction Settings State
  const [evictionMode, setEvictionMode] = useState<string>('show');
  const [savedEvictionMode, setSavedEvictionMode] = useState<string>('show');
  const [evictionScanNotifications, setEvictionScanNotifications] = useState(false);
  const [savedEvictionScanNotifications, setSavedEvictionScanNotifications] = useState(false);
  const [evictionLoading, setEvictionLoading] = useState(false);
  const [evictionSaving, setEvictionSaving] = useState(false);
  const [showRemoveConfirm, setShowRemoveConfirm] = useState(false);
  const [isStartingEvictionScan, setIsStartingEvictionScan] = useState(false);
  const evictionScanInFlightRef = useRef(false);
  const [resettingEvictions, setResettingEvictions] = useState(false);
  const isEvictionDirty =
    evictionMode !== savedEvictionMode ||
    evictionScanNotifications !== savedEvictionScanNotifications;

  const { notifications, addNotification, updateNotification } = useNotifications();
  const { gameDetectionData } = useGameDetection();
  const { on, off } = useSignalR();

  // Local state for evicted items — same pattern as GameCacheDetector's games/services.
  // Items only disappear when explicitly filtered by notification completion.
  const [evictedGames, setEvictedGames] = useState<GameCacheInfo[]>([]);
  const [evictedServices, setEvictedServices] = useState<ServiceCacheInfo[]>([]);

  const isAnyEvictedRemovalRunning = notifications.some(
    (n) =>
      (n.type === 'game_removal' ||
        n.type === 'service_removal' ||
        n.type === 'eviction_removal') &&
      n.status === 'running'
  );

  // Sync from context when detection data changes — but NOT during active removal
  // (prevents context refresh from hiding items before notification confirms completion)
  useEffect(() => {
    if (isAnyEvictedRemovalRunning) return;
    const games =
      gameDetectionData?.games?.filter(
        (g) => (g.evicted_downloads_count ?? 0) > 0 || g.is_evicted === true
      ) ?? [];
    const services =
      gameDetectionData?.services?.filter(
        (s) => (s.evicted_downloads_count ?? 0) > 0 || s.is_evicted === true
      ) ?? [];
    setEvictedGames(games);
    setEvictedServices(services);
  }, [gameDetectionData, isAnyEvictedRemovalRunning]);

  // Remove evicted items from local state when notification confirms removal is done
  // (identical pattern to GameCacheDetector lines 216-241 — idempotent, no ref needed)
  useEffect(() => {
    notifications
      .filter((n) => n.type === 'game_removal' && n.status === 'completed')
      .forEach((notif) => {
        const gameAppId = notif.details?.gameAppId;
        const gameName = notif.details?.gameName;
        if (gameAppId) {
          setEvictedGames((prev) => prev.filter((g) => g.game_app_id !== gameAppId));
        } else if (gameName) {
          setEvictedGames((prev) => prev.filter((g) => g.game_name !== gameName));
        }
      });

    notifications
      .filter((n) => n.type === 'service_removal' && n.status === 'completed')
      .forEach((notif) => {
        const serviceName = notif.details?.service;
        if (serviceName) {
          setEvictedServices((prev) => prev.filter((s) => s.service_name !== serviceName));
        }
      });
  }, [notifications]);

  // Listen for EvictionRemovalComplete (partial eviction) — same pattern as
  // GameCacheDetector's handleGameRemovalComplete SignalR listener (line 350)
  useEffect(() => {
    const handleEvictionRemovalComplete = () => {
      // Re-derive evicted items from fresh context after backend finishes
      setTimeout(() => {
        const games =
          gameDetectionData?.games?.filter(
            (g) => (g.evicted_downloads_count ?? 0) > 0 || g.is_evicted === true
          ) ?? [];
        const services =
          gameDetectionData?.services?.filter(
            (s) => (s.evicted_downloads_count ?? 0) > 0 || s.is_evicted === true
          ) ?? [];
        setEvictedGames(games);
        setEvictedServices(services);
      }, 500);
    };

    on('EvictionRemovalComplete', handleEvictionRemovalComplete);
    return () => {
      off('EvictionRemovalComplete', handleEvictionRemovalComplete);
    };
  }, [on, off, gameDetectionData]);

  // Evicted removal state (migrated from GameCacheDetector)
  const [evictedGameToRemove, setEvictedGameToRemove] = useState<GameCacheInfo | null>(null);
  const [partialEvictedTarget, setPartialEvictedTarget] = useState<
    GameCacheInfo | ServiceCacheInfo | null
  >(null);
  const [evictedServiceToRemove, setEvictedServiceToRemove] = useState<ServiceCacheInfo | null>(
    null
  );

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
    if (service.is_evicted !== true && (service.evicted_downloads_count ?? 0) > 0) {
      setPartialEvictedTarget(service);
    } else {
      setEvictedServiceToRemove(service);
    }
  };

  const confirmPartialEvictedRemoval = async () => {
    if (!partialEvictedTarget) return;

    const isService = 'service_name' in partialEvictedTarget;
    // Close modal immediately — progress shown via EvictionRemoval* SignalR notifications (registry-managed)
    setPartialEvictedTarget(null);

    if (isService) {
      const service = partialEvictedTarget as ServiceCacheInfo;
      try {
        await ApiService.removeEvictedForService(service.service_name);
        setTimeout(() => {
          onDataRefresh?.();
        }, 30000);
      } catch (err: unknown) {
        const errorMsg =
          (err instanceof Error ? err.message : String(err)) ||
          t('management.gameDetection.failedToRemoveService');
        console.error('Partial evicted service removal error:', errorMsg);
      }
    } else {
      const game = partialEvictedTarget as GameCacheInfo;
      const isEpic = game.service === 'epicgames';
      try {
        if (isEpic && game.epic_app_id) {
          await ApiService.removeEvictedForEpicGame(game.epic_app_id);
        } else {
          await ApiService.removeEvictedForGame(game.game_app_id);
        }
        setTimeout(() => {
          onDataRefresh?.();
        }, 30000);
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
      // Item disappears when notification becomes 'completed' via useEffect
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
      console.error('Evicted game removal error:', errorMsg);
    }
  };

  const confirmEvictedServiceRemoval = async () => {
    if (!evictedServiceToRemove) return;

    const serviceName = evictedServiceToRemove.service_name;

    addNotification({
      type: 'service_removal',
      status: 'running',
      message: t('management.gameDetection.removingService', { name: serviceName }),
      details: { service: serviceName }
    });

    setEvictedServiceToRemove(null);

    try {
      await ApiService.removeServiceFromCache(serviceName);
      // Item disappears when notification becomes 'completed' via useEffect
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
      console.error('Evicted service removal error:', errorMsg);
    }
  };

  const evictionScanNotification = notifications.find(
    (n: { type: string; status: string }) => n.type === 'eviction_scan' && n.status === 'running'
  );
  const isEvictionScanRunning = !!evictionScanNotification || isStartingEvictionScan;

  const [evictedDataExpanded, setEvictedDataExpanded] = useState(() => {
    const saved = localStorage.getItem('management-evicted-data-expanded-v2');
    return saved !== null ? saved === 'true' : false;
  });

  useEffect(() => {
    localStorage.setItem('management-evicted-data-expanded-v2', String(evictedDataExpanded));
  }, [evictedDataExpanded]);

  const [evictionSettingsExpanded, setEvictionSettingsExpanded] = useState(() => {
    const saved = localStorage.getItem('management-eviction-settings-expanded');
    return saved !== null ? saved === 'true' : true;
  });

  useEffect(() => {
    localStorage.setItem('management-eviction-settings-expanded', String(evictionSettingsExpanded));
  }, [evictionSettingsExpanded]);

  const [evictedItemsExpanded, setEvictedItemsExpanded] = useState(() => {
    const saved = localStorage.getItem('management-evicted-items-expanded');
    return saved !== null ? saved === 'true' : true;
  });

  useEffect(() => {
    localStorage.setItem('management-evicted-items-expanded', String(evictedItemsExpanded));
  }, [evictedItemsExpanded]);

  const evictionAllExpanded = evictionSettingsExpanded && evictedItemsExpanded;

  const handleEvictionExpandCollapseAll = () => {
    const next = !evictionAllExpanded;
    setEvictionSettingsExpanded(next);
    setEvictedItemsExpanded(next);
  };

  const loadEvictionSettings = useCallback(
    async (signal?: AbortSignal) => {
      setEvictionLoading(true);
      try {
        const response = await ApiService.getEvictionSettings(signal);
        setEvictionMode(response.evictedDataMode);
        setSavedEvictionMode(response.evictedDataMode);
        setEvictionScanNotifications(response.evictionScanNotifications);
        setSavedEvictionScanNotifications(response.evictionScanNotifications);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        onError(t('management.sections.data.evictionLoadError'));
      } finally {
        setEvictionLoading(false);
      }
    },
    [onError, t]
  );

  useEffect(() => {
    const controller = new AbortController();
    loadEvictionSettings(controller.signal);
    return () => controller.abort();
  }, [loadEvictionSettings]);

  const performEvictionSave = async () => {
    setEvictionSaving(true);
    try {
      const response = await ApiService.updateEvictionSettings(
        evictionMode,
        evictionScanNotifications
      );
      setEvictionMode(response.evictedDataMode);
      setSavedEvictionMode(response.evictedDataMode);
      setEvictionScanNotifications(response.evictionScanNotifications);
      setSavedEvictionScanNotifications(response.evictionScanNotifications);
      onSuccess(t('management.sections.data.evictionSaveSuccess'));
      onDataRefresh();
    } catch (err: unknown) {
      onError(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.sections.data.evictionSaveError')
      );
    } finally {
      setEvictionSaving(false);
    }
  };

  const handleSaveEviction = async () => {
    if (evictionMode === 'remove' && savedEvictionMode !== 'remove') {
      setShowRemoveConfirm(true);
      return;
    }
    await performEvictionSave();
  };

  const handleConfirmRemove = async () => {
    await performEvictionSave();
    setShowRemoveConfirm(false);
  };

  const isMountedRef = useRef(true);
  useEffect(() => {
    isMountedRef.current = true;
    return () => {
      isMountedRef.current = false;
    };
  }, []);

  const EVICTION_ALREADY_RUNNING_MSG =
    'Eviction scan is already running. Please wait a moment and try again.';

  const handleStartEvictionScan = async () => {
    if (evictionScanInFlightRef.current) return;
    evictionScanInFlightRef.current = true;
    setIsStartingEvictionScan(true);

    const attemptScan = async (): Promise<boolean> => {
      try {
        await ApiService.startEvictionScan();
        return true;
      } catch (err: unknown) {
        const msg = err instanceof Error ? err.message : String(err);
        if (msg === EVICTION_ALREADY_RUNNING_MSG) {
          return false;
        }
        throw err;
      }
    };

    try {
      const succeeded = await attemptScan();
      if (!succeeded) {
        onError(t('management.sections.data.evictionScanWaiting'));
      }
    } catch (err: unknown) {
      onError(err instanceof Error ? err.message : String(err));
    } finally {
      if (isMountedRef.current) {
        setIsStartingEvictionScan(false);
      }
      evictionScanInFlightRef.current = false;
    }
  };

  const handleResetEvictions = async () => {
    setResettingEvictions(true);
    try {
      const result = await ApiService.resetEvictions();
      onSuccess(
        t('management.sections.data.resetEvictionsSuccess', {
          count: result.reset
        })
      );
      onDataRefresh();
    } catch (err: unknown) {
      onError(
        (err instanceof Error ? err.message : String(err)) ||
          t('management.sections.data.resetEvictionsError')
      );
    } finally {
      setResettingEvictions(false);
    }
  };

  const handleRecheckPermissions = async () => {
    setIsRechecking(true);
    try {
      await reloadPermissions();
    } finally {
      setIsRechecking(false);
    }
  };

  // Only show the recheck button when at least one directory is read-only
  const hasPermissionIssues = logsReadOnly || cacheReadOnly;

  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-storage"
      aria-labelledby="tab-storage"
    >
      {/* Section Header */}
      <div className="mb-4 sm:mb-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-xl font-semibold text-themed-primary mb-1">
              {t('management.sections.storage.title')}
            </h2>
            <p className="text-themed-secondary text-sm">
              {t('management.sections.storage.subtitle')}
            </p>
          </div>
          {hasPermissionIssues && (
            <Button
              variant="outline"
              size="sm"
              onClick={handleRecheckPermissions}
              disabled={isRechecking}
            >
              {isRechecking ? (
                <LoadingSpinner inline size="sm" className="mr-1.5" />
              ) : (
                <RefreshCw className="h-3.5 w-3.5 mr-1.5" />
              )}
              {isRechecking
                ? t('management.sections.storage.recheckingPermissions')
                : t('management.sections.storage.recheckPermissions')}
            </Button>
          )}
        </div>
      </div>

      {/* ==================== LOG OPERATIONS ==================== */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.storage.logOperations')}
          </h3>
        </div>

        <div className="space-y-4">
          {/* Log Processing */}
          <DatasourcesManager
            isAdmin={isAdmin}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
            onDataRefresh={onDataRefresh}
          />

          {/* Log Removal */}
          <LogRemovalManager authMode={authMode} mockMode={mockMode} onError={onError} />
        </div>
      </div>

      {/* ==================== CACHE OPERATIONS ==================== */}
      <div>
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-green)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.storage.cacheOperations')}
          </h3>
        </div>

        <div className="space-y-4">
          {/* Cache Clearing */}
          <CacheManager
            isAdmin={isAdmin}
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
          />

          {/* Corruption Detection */}
          <CorruptionManager authMode={authMode} mockMode={mockMode} onError={onError} />

          {/* Game Detection */}
          <ImageCacheContext.Provider value={imageCacheVersion}>
            <ImageInvalidateContext.Provider value={invalidateImageCache}>
              <GameCacheDetector
                mockMode={mockMode}
                isAdmin={isAdmin}
                onDataRefresh={onDataRefresh}
                refreshKey={gameCacheRefreshKey}
              />
            </ImageInvalidateContext.Provider>
          </ImageCacheContext.Provider>

          {/* Eviction Detection and Removal (outer card with two inner sub-accordions: settings + items) */}
          <Card>
            <AccordionSection
              title={t('management.sections.data.evictedCacheData')}
              icon={Archive}
              iconColor="var(--theme-icon-orange)"
              isExpanded={evictedDataExpanded}
              onToggle={() => setEvictedDataExpanded((prev) => !prev)}
              badge={undefined}
            >
              <div className="space-y-4">
                {/* Header actions toolbar — visible even when inner accordions are collapsed */}
                <div className="flex flex-wrap items-center justify-end gap-2">
                  <Button variant="default" size="sm" onClick={handleEvictionExpandCollapseAll}>
                    {evictionAllExpanded
                      ? t('management.gameDetection.collapseAll')
                      : t('management.gameDetection.expandAll')}
                  </Button>
                  <Button
                    onClick={handleResetEvictions}
                    disabled={resettingEvictions || isEvictionScanRunning}
                    loading={resettingEvictions}
                    variant="default"
                    size="sm"
                  >
                    {t('management.sections.data.resetEvictions')}
                  </Button>
                  <Button
                    onClick={handleStartEvictionScan}
                    disabled={isEvictionScanRunning || resettingEvictions}
                    variant="filled"
                    color="blue"
                    size="sm"
                  >
                    {isEvictionScanRunning ? (
                      <LoadingSpinner inline size="sm" />
                    ) : (
                      t('management.sections.data.runEvictionScan')
                    )}
                  </Button>
                </div>

                {/* Sub-accordion 1: Eviction Scan & Settings */}
                <AccordionSection
                  title={t('management.sections.data.evictionSettingsHeading')}
                  icon={Sliders}
                  iconColor="var(--theme-icon-blue)"
                  isExpanded={evictionSettingsExpanded}
                  onToggle={() => setEvictionSettingsExpanded((prev) => !prev)}
                >
                  {evictionLoading ? (
                    <LoadingState message={t('management.sections.data.evictionLoadingSettings')} />
                  ) : (
                    <>
                      <div className="space-y-2 mb-4">
                        {(['show', 'showClean', 'hide', 'remove'] as const).map((mode) => (
                          <label
                            key={mode}
                            className={`eviction-mode-option p-3 rounded-lg cursor-pointer flex items-start gap-3 transition-all duration-150${evictionMode === mode ? ' eviction-mode-option-selected' : ''}`}
                          >
                            <input
                              type="radio"
                              name="evictionMode"
                              value={mode}
                              checked={evictionMode === mode}
                              onChange={() => setEvictionMode(mode)}
                              className="eviction-radio mt-1"
                            />
                            <div className="flex-1 min-w-0">
                              <div className="font-medium text-themed-primary">
                                {t(`management.sections.data.evictionModes.${mode}`)}
                              </div>
                              <div className="text-sm text-themed-secondary mt-1">
                                {t(`management.sections.data.evictionModes.${mode}Description`)}
                              </div>
                            </div>
                          </label>
                        ))}
                      </div>

                      <div className="pt-4 border-t border-themed-primary">
                        <div className="flex items-center justify-between">
                          <Checkbox
                            label={t('management.sections.data.evictionScanNotifications')}
                            checked={evictionScanNotifications}
                            onChange={(e) => setEvictionScanNotifications(e.target.checked)}
                          />
                          <Button
                            onClick={handleSaveEviction}
                            disabled={!isEvictionDirty || evictionSaving}
                            loading={evictionSaving}
                            className="sm:w-40"
                          >
                            {t('management.sections.clients.saveChanges')}
                          </Button>
                        </div>
                        <p className="text-xs text-themed-muted mt-1 ml-6">
                          {t('management.sections.data.evictionScanNotificationsDescription')}
                        </p>
                      </div>
                    </>
                  )}
                </AccordionSection>

                {/* Sub-accordion 2: Evicted Items */}
                <AccordionSection
                  title={t('management.sections.data.evictedItemsHeading')}
                  count={
                    evictedGames.length + evictedServices.length > 0
                      ? evictedGames.length + evictedServices.length
                      : undefined
                  }
                  icon={Database}
                  iconColor="var(--theme-icon-emerald)"
                  isExpanded={evictedItemsExpanded}
                  onToggle={() => setEvictedItemsExpanded((prev) => !prev)}
                >
                  <EvictedItemsList
                    games={evictedGames}
                    services={evictedServices}
                    notifications={notifications}
                    isAdmin={isAdmin}
                    cacheReadOnly={cacheReadOnly}
                    dockerSocketAvailable={isDockerAvailable}
                    checkingPermissions={checkingPermissions}
                    isAnyRemovalRunning={isAnyEvictedRemovalRunning}
                    onRemoveGame={handleEvictedGameRemoveClick}
                    onRemoveService={handleEvictedServiceRemoveClick}
                  />
                </AccordionSection>
              </div>
            </AccordionSection>
          </Card>
        </div>
      </div>

      {/* Eviction Remove Confirmation Modal */}
      <Modal
        opened={showRemoveConfirm}
        onClose={evictionSaving ? () => undefined : () => setShowRemoveConfirm(false)}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.sections.data.evictionRemoveConfirmTitle')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.sections.data.evictionRemoveConfirmMessage')}
          </p>
          <Alert color="yellow">
            <p className="text-sm">{t('management.sections.data.evictionRemoveConfirmWarning')}</p>
          </Alert>
          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowRemoveConfirm(false)}
              disabled={evictionSaving}
            >
              {t('management.sections.data.evictionRemoveConfirmCancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleConfirmRemove}
              loading={evictionSaving}
            >
              {t('management.sections.data.evictionRemoveConfirmButton')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Evicted Game Removal Confirmation Modal */}
      <CacheRemovalModal
        target={evictedGameToRemove ? { type: 'game', data: evictedGameToRemove } : null}
        onClose={() => setEvictedGameToRemove(null)}
        onConfirm={confirmEvictedGameRemoval}
      />

      {/* Evicted Service Removal Confirmation Modal (fully evicted) */}
      <CacheRemovalModal
        target={evictedServiceToRemove ? { type: 'service', data: evictedServiceToRemove } : null}
        onClose={() => setEvictedServiceToRemove(null)}
        onConfirm={confirmEvictedServiceRemoval}
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
    </div>
  );
};

export default StorageSection;
