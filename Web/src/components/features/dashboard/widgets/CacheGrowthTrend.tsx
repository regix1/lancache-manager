import React, { useMemo, memo } from 'react';
import { TrendingUp, TrendingDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import Sparkline from '../components/Sparkline';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useCacheGrowth } from '@contexts/DashboardDataContext/hooks';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';

interface CacheGrowthTrendProps {
  /** Current used cache size in bytes (from cacheInfo) */
  usedCacheSize: number;
  /** Total cache capacity in bytes (from cacheInfo) */
  totalCacheSize: number;
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
}

/**
 * Widget showing cache growth trend with sparkline and projection
 * Uses real API data from /api/stats/cache-growth
 */
const CacheGrowthTrend: React.FC<CacheGrowthTrendProps> = memo(
  ({ usedCacheSize, totalCacheSize, glassmorphism = false }) => {
    const { t } = useTranslation();
    const { timeRange } = useTimeFilter();

    // Determine if we're viewing historical/filtered data (not live)
    // Any non-live mode should disable real-time only stats
    const isHistoricalView = timeRange !== 'live';

    // Consume cache growth data from batched context
    const { cacheGrowth: displayData, loading, error, refetch } = useCacheGrowth();

    // Extract sparkline data from API response
    const sparklineData = useMemo(() => {
      if (!displayData?.dataPoints?.length) return [];
      return displayData.dataPoints.map((dp) => dp.cumulativeCacheMissBytes);
    }, [displayData]);

    // Calculate total growth during the selected period
    const periodGrowth = useMemo(() => {
      if (!displayData?.dataPoints?.length) return 0;
      return displayData.dataPoints.reduce((sum, dp) => sum + dp.growthFromPrevious, 0);
    }, [displayData]);

    // Get values from API or props
    const trend = displayData?.trend ?? 'stable';
    const percentChange = displayData?.percentChange ?? 0;
    // Use net growth rate (accounts for cache deletions) when available
    const growthRatePerDay =
      displayData?.netAverageDailyGrowth ?? displayData?.averageDailyGrowth ?? 0;
    const daysUntilFull = displayData?.estimatedDaysUntilFull ?? null;
    const hasEnoughData = sparklineData.length >= 2;
    const hasDataDeletion = displayData?.hasDataDeletion ?? false;
    const cacheWasCleared = displayData?.cacheWasCleared ?? false;

    // Usage percentage (from props - real cache info)
    // Note: Can exceed 100% temporarily during nginx cache eviction
    const usagePercent = totalCacheSize > 0 ? (usedCacheSize / totalCacheSize) * 100 : 0;

    // Cap at 100% for progress bar display, but track if we're over
    const isOverLimit = usagePercent > 100;
    const displayPercent = Math.min(usagePercent, 100);

    // For cache, growth (up) is neutral/warning, stable is good, down is concerning
    const trendClass =
      trend === 'up'
        ? 'text-themed-warning'
        : trend === 'down'
          ? 'text-themed-info'
          : 'text-themed-success';

    // Loading state — skeleton only on initial load (no prior data); SWR refetch keeps existing chart
    if (loading && !displayData) {
      return (
        <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
          <div className="flex items-center gap-2 mb-3">
            <h3 className="text-lg font-semibold text-themed-primary">
              {t('widgets.cacheGrowthTrend.title')}
            </h3>
          </div>
          <div className="cache-growth-skeleton">
            {/* Large stat */}
            <div className="cache-growth-skeleton-stat" />

            {/* Progress bar */}
            <div className="cache-growth-skeleton-progress" />

            {/* Sparkline area */}
            <div className="cache-growth-skeleton-chart" />

            {/* Stats grid */}
            <div className="cache-growth-skeleton-grid">
              <div className="cache-growth-skeleton-grid-cell">
                <div className="cache-growth-skeleton-label" />
                <div className="cache-growth-skeleton-value" />
              </div>
              <div className="cache-growth-skeleton-grid-cell">
                <div className="cache-growth-skeleton-label" />
                <div className="cache-growth-skeleton-value" />
              </div>
            </div>
          </div>
        </div>
      );
    }

    // Error state — shown when no data is available and an error occurred
    if (error && !displayData) {
      return (
        <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
          <div className="flex items-center gap-2 mb-3">
            <h3 className="text-lg font-semibold text-themed-primary">
              {t('widgets.cacheGrowthTrend.title')}
            </h3>
          </div>
          <EmptyState
            icon={TrendingUp}
            title={t('common.failedToLoad')}
            subtitle={t('common.tryAgain')}
            action={
              <Button size="sm" onClick={refetch}>
                {t('common.retry')}
              </Button>
            }
          />
        </div>
      );
    }

    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
        {/* Header */}
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <h3 className="text-lg font-semibold text-themed-primary">
              {t('widgets.cacheGrowthTrend.title')}
            </h3>
            {loading && displayData && <LoadingSpinner size="xs" inline />}
            <HelpPopover width={320}>
              <HelpSection title={t('widgets.cacheGrowthTrend.help.termsTitle')} variant="subtle">
                <HelpDefinition
                  items={[
                    {
                      term: t('widgets.cacheGrowthTrend.trendUp.term'),
                      description: t('widgets.cacheGrowthTrend.trendUp.description')
                    },
                    {
                      term: t('widgets.cacheGrowthTrend.trendDown.term'),
                      description: t('widgets.cacheGrowthTrend.trendDown.description')
                    },
                    ...(hasDataDeletion
                      ? [
                          {
                            term: t('widgets.cacheGrowthTrend.netGrowth.term'),
                            description: t('widgets.cacheGrowthTrend.netGrowth.description')
                          }
                        ]
                      : [])
                  ]}
                />
              </HelpSection>
              <HelpNote type="info">
                {hasDataDeletion
                  ? t('widgets.cacheGrowthTrend.cacheCleared')
                  : t('widgets.cacheGrowthTrend.dataAddedNote')}
              </HelpNote>
            </HelpPopover>
          </div>
          {/* Only show percentage when meaningful (not 0), not extreme (<=500%), and cache wasn't cleared */}
          {hasEnoughData &&
            percentChange !== 0 &&
            Math.abs(percentChange) <= 500 &&
            !cacheWasCleared && (
              <div className={`flex items-center gap-1 text-xs font-medium ${trendClass}`}>
                {trend === 'up' && <TrendingUp className="w-3 h-3" />}
                {trend === 'down' && <TrendingDown className="w-3 h-3" />}
                <span>
                  {percentChange > 0 ? '+' : ''}
                  {formatPercent(percentChange, 1)}
                </span>
              </div>
            )}
        </div>

        {/* Current usage or period growth — hidden while the growth chart is
            empty (the usage number already lives in the Used Space stat card)
            so the placeholder card stays as compact as its row partner */}
        {hasEnoughData &&
          (isHistoricalView ? (
            <>
              <div className="flex items-baseline gap-2 mb-3">
                <span className="text-xl font-bold text-themed-primary">
                  {formatBytes(periodGrowth)}
                </span>
                <span className="text-sm text-themed-muted">
                  {t('widgets.cacheGrowthTrend.addedDuringPeriod')}
                </span>
              </div>
              {/* No usage bar for historical view - we don't have snapshot data */}
              <div className="h-1 mb-3" />
            </>
          ) : (
            <>
              <div className="flex items-baseline gap-2 mb-3">
                <span className="text-xl font-bold text-themed-primary">
                  {formatBytes(usedCacheSize)}
                </span>
                {totalCacheSize > 0 && (
                  <span className="text-sm text-themed-muted">/ {formatBytes(totalCacheSize)}</span>
                )}
              </div>

              {/* Usage bar */}
              {totalCacheSize > 0 && (
                <div className="widget-progress mb-3">
                  <div
                    className="widget-progress-fill"
                    style={{
                      width: `${displayPercent}%`,
                      backgroundColor: isOverLimit
                        ? 'var(--theme-error)' // Over configured limit (during eviction)
                        : usagePercent >= 90
                          ? 'var(--theme-error)'
                          : usagePercent >= 75
                            ? 'var(--theme-warning)'
                            : 'var(--theme-primary)'
                    }}
                  />
                </div>
              )}
            </>
          ))}

        {/* Sparkline. flex-1 so the small row-stretch remainder lands inside
            the well instead of as dead card space */}
        {sparklineData.length > 1 ? (
          <div className="dash-well p-3 flex-1 flex flex-col justify-center">
            <Sparkline
              data={sparklineData}
              color={hasDataDeletion ? 'var(--theme-info)' : 'var(--theme-primary)'}
              height={40}
              showArea={true}
              animated={true}
              ariaLabel={`Cache growth trend over ${timeRange}`}
            />
            {hasDataDeletion && (
              <div className="text-[10px] text-center mt-1 text-themed-muted">
                {cacheWasCleared
                  ? t('widgets.cacheGrowthTrend.cacheCleared')
                  : t('widgets.cacheGrowthTrend.someDeleted')}
              </div>
            )}
          </div>
        ) : (
          <div className="dash-well p-3 flex-1 flex flex-col">
            <EmptyState
              variant="panel"
              icon={TrendingUp}
              title={t('widgets.cacheGrowthTrend.noDataTitle')}
              subtitle={t('widgets.cacheGrowthTrend.noDataDesc')}
            />
          </div>
        )}

        {/* Growth readout */}
        <div className="dash-readout dash-readout--footer">
          <div className="dash-readout-item">
            <div className={`dash-readout-value${growthRatePerDay < 0 ? ' is-info' : ''}`}>
              {growthRatePerDay > 0
                ? `+${formatBytes(growthRatePerDay)}/day`
                : growthRatePerDay < 0
                  ? `-${formatBytes(Math.abs(growthRatePerDay))}/day`
                  : t('widgets.cacheGrowthTrend.stable')}
            </div>
            <div className="dash-readout-label">
              {isHistoricalView
                ? t('widgets.cacheGrowthTrend.avgGrowth')
                : cacheWasCleared
                  ? t('widgets.cacheGrowthTrend.downloadRate')
                  : hasDataDeletion
                    ? t('widgets.cacheGrowthTrend.netGrowth.term')
                    : t('widgets.cacheGrowthTrend.growthRate')}
            </div>
          </div>
          <div className="dash-readout-item">
            <div className="dash-readout-value">
              {daysUntilFull !== null && daysUntilFull > 0
                ? t('widgets.cacheGrowthTrend.days', { count: daysUntilFull })
                : daysUntilFull === 0
                  ? t('widgets.cacheGrowthTrend.full')
                  : isOverLimit
                    ? t('widgets.cacheGrowthTrend.overLimit', {
                        percent: usagePercent.toFixed(1)
                      })
                    : usagePercent > 0
                      ? t('widgets.cacheGrowthTrend.used', { percent: usagePercent.toFixed(1) })
                      : t('widgets.cacheGrowthTrend.empty')}
            </div>
            <div className="dash-readout-label">
              {daysUntilFull !== null && daysUntilFull > 0
                ? t('widgets.cacheGrowthTrend.estFull')
                : t('widgets.cacheGrowthTrend.status')}
            </div>
          </div>
        </div>
      </div>
    );
  }
);

CacheGrowthTrend.displayName = 'CacheGrowthTrend';

export default CacheGrowthTrend;
