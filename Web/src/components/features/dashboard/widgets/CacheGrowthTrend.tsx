import React, { memo } from 'react';
import { TrendingDown, TrendingUp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useCacheSnapshot, useStats } from '@contexts/DashboardDataContext/hooks';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';
import { getCacheGrowth } from './cacheGrowth';

interface CacheGrowthTrendProps {
  /** Current used cache size in bytes (from cacheInfo) */
  usedCacheSize: number;
  /** Total cache capacity in bytes (from cacheInfo) */
  totalCacheSize: number;
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** The dashboard's range chip, shown beside the title. */
  badge?: React.ReactNode;
}

/**
 * Widget showing current cache capacity and bounded occupancy change
 * Reads the cache snapshot slice of the batched dashboard response
 */
const CacheGrowthTrend: React.FC<CacheGrowthTrendProps> = memo(
  ({ usedCacheSize, totalCacheSize, glassmorphism = false, badge }) => {
    const { t } = useTranslation();
    const { timeRange } = useTimeFilter();
    const { cacheSnapshot, loading, error, failed, refetch } = useCacheSnapshot();
    const { failedSections } = useStats();

    const growth = getCacheGrowth(timeRange, loading, cacheSnapshot);
    const recordedChange = growth?.change ?? 0;
    const recordedPercent = growth?.percent ?? null;
    const hasCurrentCapacity = totalCacheSize > 0 || usedCacheSize > 0;
    const displayUsedSize = Math.max(usedCacheSize, 0);
    const usagePercent = totalCacheSize > 0 ? (displayUsedSize / totalCacheSize) * 100 : 0;
    const progressValue = totalCacheSize > 0 ? Math.min(displayUsedSize, totalCacheSize) : 0;
    const progressTone =
      usagePercent >= 90
        ? 'cache-growth-progress--error'
        : usagePercent >= 75
          ? 'cache-growth-progress--warning'
          : 'cache-growth-progress--primary';
    const recordedChangeText =
      recordedChange > 0
        ? `+${formatBytes(recordedChange)}`
        : recordedChange < 0
          ? `-${formatBytes(Math.abs(recordedChange))}`
          : formatBytes(0);
    const recordedPercentText =
      recordedPercent === null
        ? '—'
        : `${recordedPercent > 0 ? '+' : ''}${formatPercent(recordedPercent, 1)}`;
    const changeState =
      recordedChange > 0 ? 'increased' : recordedChange < 0 ? 'decreased' : 'unchanged';
    const changeClass =
      recordedChange > 0
        ? 'text-themed-warning'
        : recordedChange < 0
          ? 'text-themed-info'
          : 'text-themed-primary';

    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
        <div className="flex items-center gap-2 mb-3">
          <h3 className="dash-panel-title">{t('widgets.cacheGrowthTrend.title')}</h3>
        </div>

        {!hasCurrentCapacity && failedSections.cache && (
          <div className="text-sm text-themed-muted mb-3">{t('common.failedToLoad')}</div>
        )}

        {hasCurrentCapacity && (
          <>
            <div className="flex items-baseline gap-2 mb-3">
              <span className="text-xl font-bold text-themed-primary">
                {formatBytes(displayUsedSize)}
              </span>
              {totalCacheSize > 0 && (
                <span className="text-sm text-themed-muted">/ {formatBytes(totalCacheSize)}</span>
              )}
              <span className="text-xs text-themed-muted">{t('dashboard.cards.onDiskNow')}</span>
            </div>

            {totalCacheSize > 0 && (
              <progress
                aria-label={t('widgets.cacheGrowthTrend.capacity')}
                className={`cache-growth-progress mb-3 ${progressTone}`}
                max={totalCacheSize}
                value={progressValue}
              />
            )}
          </>
        )}

        <div className="well-surface dash-well p-3 flex-1 flex flex-col justify-center">
          {loading && !growth && !error && !failed ? (
            <div className="flex items-center justify-center gap-2 py-4 text-sm text-themed-muted">
              <LoadingSpinner size="sm" inline />
              <span>{t('common.loading')}</span>
            </div>
          ) : error || failed ? (
            <EmptyState
              action={
                <Button size="sm" onClick={refetch}>
                  {t('common.retry')}
                </Button>
              }
              icon={TrendingUp}
              subtitle={t('common.tryAgain')}
              title={t('common.failedToLoad')}
              variant="panel"
            />
          ) : growth ? (
            <div
              className={`flex items-center justify-center gap-2 text-xs font-medium ${changeClass}`}
            >
              {recordedChange > 0 && <TrendingUp className="w-3 h-3" />}
              {recordedChange < 0 && <TrendingDown className="w-3 h-3" />}
              <span>{t(`widgets.cacheGrowthTrend.${changeState}`)}</span>
            </div>
          ) : (
            <EmptyState
              icon={TrendingUp}
              subtitle={t('widgets.cacheGrowthTrend.noDataDesc')}
              title={t('widgets.cacheGrowthTrend.noDataTitle')}
              variant="panel"
            />
          )}
        </div>

        {/* A refetch whose snapshot sub-query comes back empty keeps the previous snapshot, so
            `growth` survives while the well already reports the failure. The footer follows the
            well rather than admitting failure above and printing last batch's numbers below. */}
        {growth && !error && !failed && (
          <div className="dash-readout dash-readout--footer">
            <div className="dash-readout-item">
              <div
                className={`dash-readout-value${
                  recordedChange > 0
                    ? ' is-warning'
                    : recordedChange < 0
                      ? ' is-info'
                      : ' is-primary'
                }`}
              >
                {recordedChangeText}
              </div>
              <div className="caps-label caps-label--wide dash-readout-label">
                {t('widgets.cacheGrowthTrend.recordedChange')}
              </div>
            </div>
            <div className="dash-readout-item">
              <div className="dash-readout-value">{recordedPercentText}</div>
              <div className="caps-label caps-label--wide dash-readout-label">
                {t('widgets.cacheGrowthTrend.recordedPercent')}
              </div>
            </div>
          </div>
        )}
        {badge ? <div className="dash-range-footer">{badge}</div> : null}
      </div>
    );
  }
);

CacheGrowthTrend.displayName = 'CacheGrowthTrend';

export default CacheGrowthTrend;
