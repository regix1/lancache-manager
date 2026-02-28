import React, { Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { RefreshCw, Loader2 } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { type AuthMode } from '@services/auth.service';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
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
        <div className="flex items-center justify-between">
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
          <GameCacheDetector
            mockMode={mockMode}
            isAdmin={isAdmin}
            onDataRefresh={onDataRefresh}
            refreshKey={gameCacheRefreshKey}
          />
        </div>
      </div>
    </div>
  );
};

export default StorageSection;
