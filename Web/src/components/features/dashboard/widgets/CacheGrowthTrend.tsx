import React, { useState, useEffect, useMemo, memo } from 'react';
import { TrendingUp, TrendingDown, Loader2 } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import { type CacheGrowthResponse } from '../../../../types';
import Sparkline from '../components/Sparkline';
import ApiService from '@services/api.service';
import { HelpPopover, HelpDefinition } from '@components/ui/HelpPopover';
import { useTimeFilter } from '@contexts/TimeFilterContext';
import { useMockMode } from '@contexts/MockModeContext';
import MockDataService from '../../../../test/mockData.service';

interface CacheGrowthTrendProps {
  /** Current used cache size in bytes (from cacheInfo) */
  usedCacheSize: number;
  /** Total cache capacity in bytes (from cacheInfo) */
  totalCacheSize: number;
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** Stagger index for entrance animation */
  staggerIndex?: number;
}

/**
 * Widget showing cache growth trend with sparkline and projection
 * Uses real API data from /api/stats/cache-growth
 */
const CacheGrowthTrend: React.FC<CacheGrowthTrendProps> = memo(({
  usedCacheSize,
  totalCacheSize,
  glassmorphism = true,
  staggerIndex,
}) => {
  const { timeRange, getTimeRangeParams, selectedEventIds } = useTimeFilter();

  // Determine if we're viewing historical/filtered data (not live)
  // Any non-live mode should disable real-time only stats
  const isHistoricalView = timeRange !== 'live';
  const { mockMode } = useMockMode();
  const [data, setData] = useState<CacheGrowthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Fetch cache growth data from API
  // In mock mode, use generated mock data instead
  useEffect(() => {
    if (mockMode) {
      setLoading(true);
      // Use mock data with provided cache sizes
      const mockData = MockDataService.generateMockCacheGrowth(usedCacheSize, totalCacheSize);
      setData(mockData);
      setError(null);
      setLoading(false);
      return;
    }

    const controller = new AbortController();

    // Clear old data immediately to prevent stale display during filter changes
    setData(null);

    const fetchData = async () => {
      try {
        setLoading(true);
        setError(null);
        const { startTime, endTime } = getTimeRangeParams();
        const eventId = selectedEventIds.length > 0 ? selectedEventIds[0] : undefined;
        // Pass actual cache size to detect deletions and calculate net growth
        const response = await ApiService.getCacheGrowth(
          controller.signal,
          startTime,
          endTime,
          'daily',
          usedCacheSize > 0 ? usedCacheSize : undefined,
          eventId
        );
        setData(response);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError('Failed to load growth data');
          console.error('CacheGrowthTrend fetch error:', err);
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    };

    fetchData();

    return () => controller.abort();
  }, [timeRange, getTimeRangeParams, usedCacheSize, totalCacheSize, mockMode, selectedEventIds]);

  // Extract sparkline data from API response
  const sparklineData = useMemo(() => {
    if (!data?.dataPoints?.length) return [];
    return data.dataPoints.map(dp => dp.cumulativeCacheMissBytes);
  }, [data]);

  // Calculate total growth during the selected period
  const periodGrowth = useMemo(() => {
    if (!data?.dataPoints?.length) return 0;
    return data.dataPoints.reduce((sum, dp) => sum + dp.growthFromPrevious, 0);
  }, [data]);

  // Get values from API or props
  const trend = data?.trend ?? 'stable';
  const percentChange = data?.percentChange ?? 0;
  // Use net growth rate (accounts for cache deletions) when available
  const growthRatePerDay = data?.netAverageDailyGrowth ?? data?.averageDailyGrowth ?? 0;
  const daysUntilFull = data?.estimatedDaysUntilFull ?? null;
  const hasEnoughData = sparklineData.length >= 2;
  const hasDataDeletion = data?.hasDataDeletion ?? false;
  const cacheWasCleared = data?.cacheWasCleared ?? false;

  // Usage percentage (from props - real cache info)
  const usagePercent = totalCacheSize > 0
    ? (usedCacheSize / totalCacheSize) * 100
    : 0;

  // Get trend color
  const getTrendColor = (): string => {
    // For cache, growth (up) is neutral/warning, stable is good, down is concerning
    if (trend === 'up') return 'var(--theme-warning)';
    if (trend === 'down') return 'var(--theme-info)';
    return 'var(--theme-success)';
  };

  // Build animation classes
  const animationClasses = staggerIndex !== undefined
    ? `animate-card-entrance stagger-${Math.min(staggerIndex + 1, 12)}`
    : '';

  // Loading state
  if (loading) {
    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
        <div className="flex items-center gap-2 mb-3">
          <TrendingUp className="w-5 h-5 text-themed-muted" />
          <h3 className="text-sm font-semibold text-themed-primary">
            Cache Growth
          </h3>
        </div>
        <div className="flex items-center justify-center h-24">
          <Loader2 className="w-6 h-6 animate-spin text-themed-accent" />
        </div>
      </div>
    );
  }

  // Error state
  if (error) {
    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
        <div className="flex items-center gap-2 mb-3">
          <TrendingUp className="w-5 h-5 text-themed-muted" />
          <h3 className="text-sm font-semibold text-themed-primary">
            Cache Growth
          </h3>
        </div>
        <p className="text-sm text-themed-muted">
          {error}
        </p>
      </div>
    );
  }

  return (
    <div
      className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}
    >
      {/* Header */}
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2">
          <TrendingUp className="w-5 h-5 text-themed-accent" />
          <h3 className="text-sm font-semibold text-themed-primary">
            Cache Growth
          </h3>
          <HelpPopover width={280}>
            <div className="space-y-1.5">
              <HelpDefinition term="↑ Up" termColor="green">Cache is growing faster recently</HelpDefinition>
              <HelpDefinition term="↓ Down" termColor="orange">Cache growth is slowing recently</HelpDefinition>
              {hasDataDeletion && (
                <HelpDefinition term="Net Growth" termColor="blue">
                  Adjusts for cache that was cleared or deleted
                </HelpDefinition>
              )}
              <HelpDefinition term="Data Points" termColor="purple">
                Time buckets with download activity. More points mean finer detail.
              </HelpDefinition>
              <div className="text-[10px] mt-2 pt-2 border-t border-themed-primary text-themed-muted">
                {hasDataDeletion
                  ? 'Cache was cleared in this period; the trend accounts for deletions.'
                  : 'Shows data added during the period (not total cache size).'}
              </div>
            </div>
          </HelpPopover>
        </div>
        {/* Only show percentage when meaningful (not 0), not extreme (<=500%), and cache wasn't cleared */}
        {hasEnoughData && percentChange !== 0 && Math.abs(percentChange) <= 500 && !cacheWasCleared && (
          <div
            className="flex items-center gap-1 text-xs font-medium"
            style={{ color: getTrendColor() }}
          >
            {trend === 'up' && <TrendingUp className="w-3 h-3" />}
            {trend === 'down' && <TrendingDown className="w-3 h-3" />}
            <span>{percentChange > 0 ? '+' : ''}{percentChange.toFixed(1)}%</span>
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
              added during period
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
              <span className="text-sm text-themed-muted">
                / {formatBytes(totalCacheSize)}
              </span>
            )}
          </div>

          {/* Usage bar */}
          {totalCacheSize > 0 && (
            <div className="widget-progress mb-3">
              <div
                className="widget-progress-fill"
                style={{
                  width: `${usagePercent}%`,
                  backgroundColor:
                    usagePercent >= 90
                      ? 'var(--theme-error)'
                      : usagePercent >= 75
                        ? 'var(--theme-warning)'
                        : 'var(--theme-primary)',
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
                ? 'Cache cleared • Showing new downloads'
                : 'Some cache data was deleted'}
            </div>
          )}
        </div>
      )}

      {/* Growth stats */}
      <div className="grid grid-cols-2 gap-4 text-xs">
        <div>
          <div className="text-themed-muted">
            {isHistoricalView ? 'Avg. Growth' : cacheWasCleared ? 'Download Rate' : hasDataDeletion ? 'Net Growth' : 'Growth Rate'}
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
                : 'Stable'}
          </div>
        </div>
        <div>
          <div className="text-themed-muted">
            {isHistoricalView ? 'Data Points' : daysUntilFull !== null && daysUntilFull > 0 ? 'Est. Full' : 'Status'}
          </div>
          <div className="font-medium text-themed-primary">
            {isHistoricalView
              ? `${sparklineData.length} samples`
              : daysUntilFull !== null && daysUntilFull > 0
                ? `~${daysUntilFull} days`
                : daysUntilFull === 0
                  ? 'Full'
                  : usagePercent > 0
                    ? `${usagePercent.toFixed(1)}% used`
                    : 'Empty'}
          </div>
        </div>
      </div>
    </div>
  );
});

CacheGrowthTrend.displayName = 'CacheGrowthTrend';

export default CacheGrowthTrend;
