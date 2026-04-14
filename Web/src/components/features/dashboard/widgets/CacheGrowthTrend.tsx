/* eslint-disable no-console */
import React, { useMemo, useEffect, memo } from 'react';
import { TrendingUp, TrendingDown } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import Sparkline from '../components/Sparkline';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useCacheGrowth } from '@contexts/DashboardDataContext/hooks';

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
  ({ usedCacheSize, totalCacheSize, glassmorphism = true }) => {
    const { t } = useTranslation();
    const { timeRange } = useTimeFilter();

    // Determine if we're viewing historical/filtered data (not live)
    // Any non-live mode should disable real-time only stats
    const isHistoricalView = timeRange !== 'live';

    // Consume cache growth data from batched context
    const { cacheGrowth: displayData, loading } = useCacheGrowth();
    const error: string | null = null;

    // [SPARKDBG] Fetch-trigger fires when timeRange changes (fetch happens in DashboardDataContext batch endpoint).
    useEffect(() => {
      console.log('[SPARKDBG] CacheGrowthTrend/fetch-trigger', { timeRange });
    }, [timeRange]);

    // [SPARKDBG] Fetch-response fires when new cacheGrowth data arrives from the batched endpoint.
    useEffect(() => {
      console.log('[SPARKDBG] CacheGrowthTrend/fetch-response', {
        timeRange,
        dataPoints: displayData?.dataPoints?.length,
        displayDataIdentity: displayData
      });
    }, [displayData, timeRange]);

    // Extract sparkline data from API response
    const sparklineData = useMemo(() => {
      if (!displayData?.dataPoints?.length) return [];
      return displayData.dataPoints.map((dp) => dp.cumulativeCacheMissBytes);
    }, [displayData]);

    console.log('[SPARKDBG] CacheGrowthTrend/render', {
      timeRange,
      sparklineDataLen: sparklineData.length,
      loading
    });

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

    // Get trend color
    const getTrendColor = (): string => {
      // For cache, growth (up) is neutral/warning, stable is good, down is concerning
      if (trend === 'up') return 'var(--theme-warning)';
      if (trend === 'down') return 'var(--theme-info)';
      return 'var(--theme-success)';
    };

    // Loading state - show skeleton on initial load and every refresh
    if (loading) {
      return (
        <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
          <div className="flex items-center gap-2 mb-3">
            <TrendingUp className="w-5 h-5 text-themed-muted" />
            <h3 className="text-sm font-semibold text-themed-primary">
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

    // Error state
    if (error) {
      return (
        <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
          <div className="flex items-center gap-2 mb-3">
            <TrendingUp className="w-5 h-5 text-themed-muted" />
            <h3 className="text-sm font-semibold text-themed-primary">
              {t('widgets.cacheGrowthTrend.title')}
            </h3>
          </div>
          <p className="text-sm text-themed-muted">{t('widgets.cacheGrowthTrend.failedToLoad')}</p>
        </div>
      );
    }

    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
        {/* Header */}
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <TrendingUp className="w-5 h-5 text-themed-accent" />
            <h3 className="text-sm font-semibold text-themed-primary">
              {t('widgets.cacheGrowthTrend.title')}
            </h3>
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
                      : []),
                    {
                      term: t('widgets.cacheGrowthTrend.dataPoints.term'),
                      description:
                        t('widgets.cacheGrowthTrend.dataPoints.description') +
                        (sparklineData.length > 0
                          ? ' ' +
                            t('widgets.cacheGrowthTrend.dataPoints.current', {
                              count: sparklineData.length
                            })
                          : '')
                    }
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
              <div
                className="flex items-center gap-1 text-xs font-medium"
                style={{ color: getTrendColor() }}
              >
                {trend === 'up' && <TrendingUp className="w-3 h-3" />}
                {trend === 'down' && <TrendingDown className="w-3 h-3" />}
                <span>
                  {percentChange > 0 ? '+' : ''}
                  {formatPercent(percentChange, 1)}
                </span>
              </div>
            )}
        </div>

        {/* Current usage or period growth */}
        {isHistoricalView ? (
          <>
            <div className="flex items-baseline gap-2 mb-2">
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
            <div className="flex items-baseline gap-2 mb-2">
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
        )}

        {/* Sparkline */}
        {sparklineData.length > 1 && (
          <div className="mb-3">
            <Sparkline
              key={timeRange}
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
        )}

        {/* Growth stats */}
        <div className="grid grid-cols-2 gap-4 text-xs">
          <div>
            <div className="text-themed-muted">
              {isHistoricalView
                ? t('widgets.cacheGrowthTrend.avgGrowth')
                : cacheWasCleared
                  ? t('widgets.cacheGrowthTrend.downloadRate')
                  : hasDataDeletion
                    ? t('widgets.cacheGrowthTrend.netGrowth.term')
                    : t('widgets.cacheGrowthTrend.growthRate')}
            </div>
            <div
              className={`font-medium ${
                growthRatePerDay < 0
                  ? 'text-themed-info'
                  : growthRatePerDay > 0
                    ? 'text-themed-primary'
                    : 'text-themed-muted'
              }`}
            >
              {growthRatePerDay > 0
                ? `+${formatBytes(growthRatePerDay)}/day`
                : growthRatePerDay < 0
                  ? `-${formatBytes(Math.abs(growthRatePerDay))}/day`
                  : t('widgets.cacheGrowthTrend.stable')}
            </div>
          </div>
          <div>
            <div className="text-themed-muted">
              {isHistoricalView
                ? t('widgets.cacheGrowthTrend.dataPoints.term')
                : daysUntilFull !== null && daysUntilFull > 0
                  ? t('widgets.cacheGrowthTrend.estFull')
                  : t('widgets.cacheGrowthTrend.status')}
            </div>
            <div className="font-medium text-themed-primary">
              {isHistoricalView
                ? t('widgets.cacheGrowthTrend.dataPointsCount', { count: sparklineData.length })
                : daysUntilFull !== null && daysUntilFull > 0
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
          </div>
        </div>
      </div>
    );
  }
);

CacheGrowthTrend.displayName = 'CacheGrowthTrend';

export default CacheGrowthTrend;
