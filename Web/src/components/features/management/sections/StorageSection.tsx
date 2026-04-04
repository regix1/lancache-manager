import React, { useState, useCallback, useEffect, useRef, useMemo } from 'react';
import './StorageSection.css';
import { useTranslation } from 'react-i18next';
import { RefreshCw, AlertTriangle, Archive, Database } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Checkbox } from '@components/ui/Checkbox';
import { LoadingState } from '@components/ui/ManagerCard';
import { AccordionSection } from '@components/ui/AccordionSection';
import { type AuthMode } from '@services/auth.service';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { ImageCacheContext, ImageInvalidateContext } from '@components/common/ImageCacheContext';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { useGameDetection } from '@contexts/DashboardDataContext/hooks';
import { useNotifications } from '@contexts/notifications/useNotifications';
import { useDockerSocket } from '@contexts/useDockerSocket';
import DatasourcesManager from '../datasources/DatasourcesInfo';
import LogRemovalManager from '../log-processing/LogRemovalManager';
import CacheManager from '../cache/CacheManager';
import CorruptionManager from '../cache/CorruptionManager';
import GameCacheDetector from '../game-detection/GameCacheDetector';
import GamesList from '../game-detection/GamesList';
import CacheRemovalModal from '@components/modals/cache/CacheRemovalModal';
import type { GameCacheInfo } from '../../../../types';
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

  const { notifications, isAnyRemovalRunning, addNotification } = useNotifications();
  const { isDockerAvailable } = useDockerSocket();
  const evictionScanNotification = notifications.find(
    (n: { type: string; status: string }) => n.type === 'eviction_scan' && n.status === 'running'
  );
  const isEvictionScanRunning = !!evictionScanNotification || isStartingEvictionScan;

  // Evicted Games — derived from cached game detection data (is_evicted === true)
  const { gameDetectionData, isLoading: evictedGamesLoading } = useGameDetection();
  const [evictedGamesExpanded, setEvictedGamesExpanded] = useState(() => {
    const saved = localStorage.getItem('management-evicted-games-expanded');
    return saved !== null ? saved === 'true' : true;
  });
  const [evictedDataExpanded, setEvictedDataExpanded] = useState(() => {
    const saved = localStorage.getItem('management-evicted-data-expanded');
    return saved !== null ? saved === 'true' : true;
  });
  const evictedGames = useMemo(
    () => gameDetectionData?.games?.filter((game) => game.is_evicted === true) ?? [],
    [gameDetectionData]
  );

  useEffect(() => {
    localStorage.setItem('management-evicted-games-expanded', String(evictedGamesExpanded));
  }, [evictedGamesExpanded]);

  useEffect(() => {
    localStorage.setItem('management-evicted-data-expanded', String(evictedDataExpanded));
  }, [evictedDataExpanded]);

  const [evictedGameToRemove, setEvictedGameToRemove] = useState<GameCacheInfo | null>(null);

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
    setEvictedGameToRemove(game);
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

  const handleStartEvictionScan = async () => {
    if (evictionScanInFlightRef.current) return;
    evictionScanInFlightRef.current = true;
    setIsStartingEvictionScan(true);
    try {
      await ApiService.startEvictionScan();
    } catch (err: unknown) {
      onError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsStartingEvictionScan(false);
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

          {/* Evicted Game Data (combined: eviction settings + evicted games list) */}
          <Card>
            <AccordionSection
              title={t('management.sections.data.evictedCacheData')}
              icon={Archive}
              iconColor="var(--theme-icon-orange)"
              isExpanded={evictedDataExpanded}
              onToggle={() => setEvictedDataExpanded((prev) => !prev)}
            >
              <div className="space-y-4">
                {/* Action toolbar */}
                <div className="flex flex-wrap items-center justify-end gap-2">
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

                {/* Evicted Games list */}
                <hr className="evicted-games-divider" />
                <AccordionSection
                  title={t('management.gameDetection.evictedGamesSection')}
                  count={evictedGames.length}
                  icon={Database}
                  iconColor="var(--theme-warning-text)"
                  isExpanded={evictedGamesExpanded}
                  onToggle={() => setEvictedGamesExpanded((prev) => !prev)}
                  badge={
                    evictedGames.length > 0 ? (
                      <span className="themed-badge status-badge-warning">
                        {evictedGames.length}
                      </span>
                    ) : undefined
                  }
                >
                  {evictedGamesLoading ? (
                    <LoadingState message={t('management.gameDetection.loadingEvictedGames')} />
                  ) : evictedGames.length === 0 ? (
                    <div className="text-center py-8 text-themed-muted">
                      <Database className="w-12 h-12 mx-auto mb-3 opacity-50" />
                      <p>{t('management.gameDetection.noEvictedGames')}</p>
                    </div>
                  ) : (
                    <GamesList
                      games={evictedGames}
                      totalGames={evictedGames.length}
                      notifications={notifications}
                      isAnyRemovalRunning={isAnyRemovalRunning}
                      isAdmin={isAdmin}
                      cacheReadOnly={cacheReadOnly}
                      dockerSocketAvailable={isDockerAvailable}
                      checkingPermissions={checkingPermissions}
                      onRemoveGame={handleEvictedGameRemoveClick}
                    />
                  )}
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
    </div>
  );
};

export default StorageSection;
