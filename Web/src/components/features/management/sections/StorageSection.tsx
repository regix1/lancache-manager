import React, { Suspense, useState, useCallback, useEffect, useRef } from 'react';
import './StorageSection.css';
import { useTranslation } from 'react-i18next';
import { RefreshCw, Loader2, AlertTriangle, Archive } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Checkbox } from '@components/ui/Checkbox';
import { ManagerCardHeader, LoadingState } from '@components/ui/ManagerCard';
import { type AuthMode } from '@services/auth.service';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import {
  ImageCacheContext,
  ImageCacheInvalidateContext
} from '@components/common/ImageCacheContext';
import ApiService from '@services/api.service';
import { useNotifications } from '@contexts/notifications/useNotifications';
import DatasourcesManager from '../datasources/DatasourcesInfo';
import LogRemovalManager from '../log-processing/LogRemovalManager';
import CacheManager from '../cache/CacheManager';
import CorruptionManager from '../cache/CorruptionManager';
import GameCacheDetector from '../game-detection/GameCacheDetector';
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
  const { logsReadOnly, cacheReadOnly, reload: reloadPermissions } = useDirectoryPermissions();
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

  const { notifications } = useNotifications();
  const evictionScanNotification = notifications.find(
    (n: { type: string; status: string }) => n.type === 'eviction_scan' && n.status === 'running'
  );
  const isEvictionScanRunning = !!evictionScanNotification || isStartingEvictionScan;

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
                <Loader2 className="h-3.5 w-3.5 animate-spin mr-1.5" />
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
          <Suspense
            fallback={
              <Card>
                <div className="flex items-center justify-center py-8">
                  <div className="text-themed-muted">
                    {t('management.sections.storage.loadingCacheConfig')}
                  </div>
                </div>
              </Card>
            }
          >
            <CacheManager
              isAdmin={isAdmin}
              authMode={authMode}
              mockMode={mockMode}
              onError={onError}
              onSuccess={onSuccess}
            />
          </Suspense>

          {/* Corruption Detection */}
          <CorruptionManager authMode={authMode} mockMode={mockMode} onError={onError} />

          {/* Game Detection */}
          <ImageCacheContext.Provider value={imageCacheVersion}>
            <ImageCacheInvalidateContext.Provider value={invalidateImageCache}>
              <GameCacheDetector
                mockMode={mockMode}
                isAdmin={isAdmin}
                onDataRefresh={onDataRefresh}
                refreshKey={gameCacheRefreshKey}
              />
            </ImageCacheInvalidateContext.Provider>
          </ImageCacheContext.Provider>
        </div>
      </div>

      {/* ==================== EVICTION SETTINGS ==================== */}
      <div className="mt-6 sm:mt-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-orange)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('management.sections.data.evictedCacheData')}
          </h3>
        </div>

        <Card>
          <ManagerCardHeader
            icon={Archive}
            iconColor="orange"
            title={t('management.sections.data.evictedCacheData')}
            subtitle={t('management.sections.data.evictedCacheDescription')}
            actions={
              <div className="flex items-center gap-2">
                <Button
                  onClick={handleResetEvictions}
                  disabled={resettingEvictions || isEvictionScanRunning}
                  loading={resettingEvictions}
                  variant="subtle"
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
                    <Loader2 className="w-4 h-4 animate-spin" />
                  ) : (
                    t('management.sections.data.runEvictionScan')
                  )}
                </Button>
              </div>
            }
          />

          {evictionLoading ? (
            <LoadingState message={t('management.sections.data.evictionLoadingSettings')} />
          ) : (
            <>
              <div className="space-y-2 mb-4">
                {(['show', 'hide', 'remove'] as const).map((mode) => (
                  <label
                    key={mode}
                    className={`eviction-mode-option p-3 rounded-lg cursor-pointer flex items-start gap-3 transition-all duration-150 bg-themed-secondary${evictionMode === mode ? ' eviction-mode-option-selected' : ''}`}
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
        </Card>
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
    </div>
  );
};

export default StorageSection;
