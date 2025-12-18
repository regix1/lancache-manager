import React, { Suspense } from 'react';
import { Card } from '@components/ui/Card';
import { type AuthMode } from '@services/auth.service';
import DatasourcesManager from '../datasources/DatasourcesInfo';
import LogRemovalManager from '../log-processing/LogRemovalManager';
import CacheManager from '../cache/CacheManager';
import CorruptionManager from '../cache/CorruptionManager';
import GameCacheDetector from '../game-detection/GameCacheDetector';

interface StorageSectionProps {
  isAuthenticated: boolean;
  authMode: AuthMode;
  mockMode: boolean;
  gameCacheRefreshKey: number;
  onError: (message: string) => void;
  onSuccess: (message: string) => void;
  onDataRefresh: () => void;
  logRemovalReloadRef: React.MutableRefObject<(() => Promise<void>) | null>;
  corruptionReloadRef: React.MutableRefObject<(() => Promise<void>) | null>;
}

const StorageSection: React.FC<StorageSectionProps> = ({
  isAuthenticated,
  authMode,
  mockMode,
  gameCacheRefreshKey,
  onError,
  onSuccess,
  onDataRefresh,
  logRemovalReloadRef,
  corruptionReloadRef
}) => {
  return (
    <div
      className="management-section animate-fade-in"
      role="tabpanel"
      id="panel-storage"
      aria-labelledby="tab-storage"
    >
      {/* Section Header */}
      <div className="mb-4 sm:mb-6">
        <h2 className="text-xl font-semibold text-themed-primary mb-1">
          Storage Management
        </h2>
        <p className="text-themed-secondary text-sm">
          Manage cache directories, process logs, detect corrupted files, and identify cached games
        </p>
      </div>

      {/* ==================== LOG OPERATIONS ==================== */}
      <div className="mb-6 sm:mb-8">
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-icon-blue)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Log Operations
          </h3>
        </div>

        <div className="space-y-4">
          {/* Log Processing */}
          <DatasourcesManager
            isAuthenticated={isAuthenticated}
            mockMode={mockMode}
            onError={onError}
            onSuccess={onSuccess}
            onDataRefresh={onDataRefresh}
          />

          {/* Log Removal */}
          <LogRemovalManager
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onReloadRef={logRemovalReloadRef}
          />
        </div>
      </div>

      {/* ==================== CACHE OPERATIONS ==================== */}
      <div>
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div
            className="w-1 h-5 rounded-full"
            style={{ backgroundColor: 'var(--theme-icon-green)' }}
          />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            Cache Operations
          </h3>
        </div>

        <div className="space-y-4">
          {/* Cache Clearing */}
          <Suspense
            fallback={
              <Card>
                <div className="flex items-center justify-center py-8">
                  <div className="text-themed-muted">Loading cache configuration...</div>
                </div>
              </Card>
            }
          >
            <CacheManager
              isAuthenticated={isAuthenticated}
              authMode={authMode}
              mockMode={mockMode}
              onError={onError}
              onSuccess={onSuccess}
            />
          </Suspense>

          {/* Corruption Detection */}
          <CorruptionManager
            authMode={authMode}
            mockMode={mockMode}
            onError={onError}
            onReloadRef={corruptionReloadRef}
          />

          {/* Game Detection */}
          <GameCacheDetector
            mockMode={mockMode}
            isAuthenticated={authMode === 'authenticated'}
            onDataRefresh={onDataRefresh}
            refreshKey={gameCacheRefreshKey}
          />
        </div>
      </div>
    </div>
  );
};

export default StorageSection;
