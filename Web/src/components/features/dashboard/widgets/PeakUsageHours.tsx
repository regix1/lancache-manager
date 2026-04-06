import React, { useMemo, memo } from 'react';
import { Clock, TrendingUp, Zap, Calendar } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatCount } from '@utils/formatters';
import { type HourlyActivityItem } from '../../../../types';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { useTimezone } from '@contexts/useTimezone';
import { useHourlyActivity } from '@contexts/DashboardDataContext/hooks';
import { getCurrentHour } from '@utils/timezone';

interface PeakUsageHoursProps {
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** Stagger index for entrance animation */
  staggerIndex?: number;
}

/**
 * Widget showing download activity by hour of day
 * Displays a heatmap-style visualization with clear Peak and Now indicators
 * Uses backend aggregation for efficiency
 * Intelligently handles multi-day ranges by showing averages
 */
const PeakUsageHours: React.FC<PeakUsageHoursProps> = memo(
  ({ glassmorphism = true, staggerIndex }) => {
    const { t } = useTranslation();
    const { use24HourFormat, useLocalTimezone } = useTimezone();

    // Consume hourly activity data from batched context
    const { hourlyActivity: displayData, loading } = useHourlyActivity();
    const error: string | null = null;

    // Get current hour based on timezone preference
    const currentHour = useMemo(() => {
      return getCurrentHour(useLocalTimezone);
    }, [useLocalTimezone]);

    // Check if today is within the period range
    const isTodayInRange = useMemo(() => {
      if (!displayData) return true; // Assume yes while loading

      // For 'live' mode (no period bounds), today is always in range
      if (!displayData.periodStart && !displayData.periodEnd) return true;

      const now = Math.floor(Date.now() / 1000);
      const todayStart = Math.floor(new Date().setHours(0, 0, 0, 0) / 1000);
      const todayEnd = Math.floor(new Date().setHours(23, 59, 59, 999) / 1000);

      // Check if today overlaps with the period
      const periodStart = displayData.periodStart ?? 0;
      const periodEnd = displayData.periodEnd ?? now;

      return todayStart <= periodEnd && todayEnd >= periodStart;
    }, [displayData]);

    // Determine if we should show averages (multi-day period)
    const daysInPeriod = displayData?.daysInPeriod ?? 1;
    const isMultiDayPeriod = daysInPeriod > 1;

    // Extract hourly data from API response (already includes all 24 hours)
    const hourlyData = useMemo((): HourlyActivityItem[] => {
      if (!displayData?.hours?.length) {
        // Return empty buckets if no data
        return Array.from({ length: 24 }, (_, i) => ({
          hour: i,
          downloads: 0,
          avgDownloads: 0,
          bytesServed: 0,
          avgBytesServed: 0,
          cacheHitBytes: 0,
          cacheMissBytes: 0
        }));
      }
      return displayData.hours;
    }, [displayData]);

    // Find max for scaling
    const maxDownloads = useMemo(() => {
      const max = Math.max(...hourlyData.map((h) => h.downloads));
      return max || 1;
    }, [hourlyData]);

    // Peak hour from API response
    const peakHour = displayData?.peakHour ?? 0;
    const totalDownloads = displayData?.totalDownloads ?? 0;

    // Calculate total bytes served across all hours
    const totalBytesServed = useMemo(() => {
      return hourlyData.reduce((sum, h) => sum + h.bytesServed, 0);
    }, [hourlyData]);

    // Determine time-of-day category for the peak hour
    const getTimeOfDayLabel = (hour: number): string => {
      if (hour >= 5 && hour < 12) return t('widgets.peakUsageHours.morning');
      if (hour >= 12 && hour < 17) return t('widgets.peakUsageHours.afternoon');
      if (hour >= 17 && hour < 21) return t('widgets.peakUsageHours.evening');
      return t('widgets.peakUsageHours.night');
    };

    const peakTimeOfDay = getTimeOfDayLabel(peakHour);

    // Format hour for display based on 12h/24h preference
    const formatHour = (hour: number, short = false): string => {
      if (use24HourFormat) {
        return short
          ? `${hour.toString().padStart(2, '0')}`
          : `${hour.toString().padStart(2, '0')}:00`;
      }
      const ampm = hour >= 12 ? 'PM' : 'AM';
      const h = hour % 12 || 12;
      return short ? `${h}${ampm}` : `${h}:00 ${ampm}`;
    };

    // Get intensity color based on activity level (heatmap style)
    // For multi-day periods, use averages for scaling to avoid inflated totals
    const getIntensityColor = (
      downloads: number,
      isCurrentHour: boolean,
      isPeakHour: boolean
    ): string => {
      if (downloads === 0) {
        return 'var(--theme-bg-tertiary)';
      }

      const intensity = downloads / maxDownloads;

      // Peak hour gets special color
      if (isPeakHour && downloads > 0) {
        return 'var(--theme-warning)';
      }

      // Current hour gets primary color (only if today is in range)
      if (isCurrentHour && isTodayInRange) {
        return 'var(--theme-primary)';
      }

      // Use intensity-based coloring
      if (intensity > 0.75) {
        return 'var(--theme-chart-1)';
      } else if (intensity > 0.5) {
        return 'var(--theme-chart-1-emphasis)';
      } else if (intensity > 0.25) {
        return 'var(--theme-chart-1-strong)';
      } else {
        return 'var(--theme-chart-1-muted)';
      }
    };

    // Build animation classes
    const animationClasses =
      staggerIndex !== undefined
        ? `animate-card-entrance stagger-${Math.min(staggerIndex + 1, 12)}`
        : '';

    // Loading state - only show loading skeleton if we have no data at all
    if (loading && !displayData) {
      return (
        <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
          <div className="flex items-center gap-2 mb-3">
            <Clock className="w-5 h-5 text-themed-muted" />
            <h3 className="text-sm font-semibold text-themed-primary">
              {t('widgets.peakUsageHours.title')}
            </h3>
          </div>
          <div className="peak-usage-skeleton">
            {/* Period totals bar */}
            <div className="peak-usage-skeleton-bar" />

            {/* Two stat cards */}
            <div className="peak-usage-skeleton-cards">
              <div className="peak-usage-skeleton-card">
                <div className="peak-usage-skeleton-card-label" />
                <div className="peak-usage-skeleton-card-value" />
              </div>
              <div className="peak-usage-skeleton-card">
                <div className="peak-usage-skeleton-card-label" />
                <div className="peak-usage-skeleton-card-value" />
              </div>
            </div>

            {/* Heatmap grid */}
            <div className="peak-usage-skeleton-heatmap">
              {Array.from({ length: 24 }).map((_, i) => (
                <div key={i} className="peak-usage-skeleton-cell" />
              ))}
            </div>

            {/* Legend */}
            <div className="peak-usage-skeleton-legend">
              <div className="peak-usage-skeleton-legend-item" />
              <div className="peak-usage-skeleton-legend-item" />
              <div className="peak-usage-skeleton-legend-item" />
            </div>
          </div>
        </div>
      );
    }

    // Error or empty state
    if (error || totalDownloads === 0) {
      return (
        <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
          <div className="flex items-center gap-2 mb-3">
            <Clock className="w-5 h-5 text-themed-muted" />
            <h3 className="text-sm font-semibold text-themed-primary">
              {t('widgets.peakUsageHours.title')}
            </h3>
          </div>
          <p className="text-sm text-themed-muted">
            {error || t('widgets.peakUsageHours.noDataAvailable')}
          </p>
        </div>
      );
    }

    // Get peak hour data for stats
    const peakHourData = hourlyData.find((h) => h.hour === peakHour);
    const currentHourData = hourlyData.find((h) => h.hour === currentHour);

    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
        {/* Header */}
        <div className="flex items-center justify-between mb-2">
          <div className="flex items-center gap-2">
            <Clock className="w-5 h-5 flex-shrink-0 text-themed-accent" />
            <h3 className="text-sm font-semibold text-themed-primary">
              {t('widgets.peakUsageHours.title')}
            </h3>
            <HelpPopover width={320}>
              <HelpSection title={t('widgets.peakUsageHours.help.aboutTitle')}>
                {t('widgets.peakUsageHours.description')}
              </HelpSection>
              <HelpSection title={t('widgets.peakUsageHours.help.termsTitle')} variant="subtle">
                <HelpDefinition
                  items={[
                    {
                      term: t('widgets.peakUsageHours.dataPeriod.term'),
                      description: isMultiDayPeriod
                        ? t('widgets.peakUsageHours.dataPeriod.multiDay', { days: daysInPeriod })
                        : t('widgets.peakUsageHours.dataPeriod.singleDay')
                    },
                    {
                      term: t('widgets.peakUsageHours.peakHour.term'),
                      description: isMultiDayPeriod
                        ? t('widgets.peakUsageHours.peakHour.multiDay')
                        : t('widgets.peakUsageHours.peakHour.singleDay')
                    },
                    ...(isTodayInRange
                      ? [
                          {
                            term: t('widgets.peakUsageHours.currentHour.term'),
                            description: isMultiDayPeriod
                              ? t('widgets.peakUsageHours.currentHour.multiDay')
                              : t('widgets.peakUsageHours.currentHour.singleDay')
                          }
                        ]
                      : [])
                  ]}
                />
              </HelpSection>
              <HelpNote type="info">{t('widgets.peakUsageHours.heatmapNote')}</HelpNote>
            </HelpPopover>
          </div>
          <div className="flex items-center gap-2 text-xs text-themed-muted">
            <span className="hidden sm:inline">{t('widgets.peakUsageHours.mostActive')}</span>
            <span className="font-medium text-themed-warning">{peakTimeOfDay}</span>
          </div>
        </div>

        {/* Period Totals - Aggregate stats for entire time range */}
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between px-3 py-2 mb-3 themed-border-radius text-xs gap-1 sm:gap-0 bg-themed-secondary">
          <div className="flex items-center gap-2">
            {isMultiDayPeriod && (
              <Calendar className="w-3.5 h-3.5 hidden sm:block text-themed-muted" />
            )}
            <span className="text-themed-muted">
              {isMultiDayPeriod
                ? t('widgets.peakUsageHours.days', { count: daysInPeriod })
                : t('widgets.peakUsageHours.selectedPeriod')}
            </span>
            <span className="sm:hidden text-themed-secondary">•</span>
            <span className="sm:hidden text-themed-secondary tabular-nums">
              {formatCount(totalDownloads)} {t('widgets.peakUsageHours.downloads')}
            </span>
            <span className="sm:hidden text-themed-secondary">•</span>
            <span className="sm:hidden text-themed-secondary tabular-nums">
              {formatBytes(totalBytesServed)}
            </span>
          </div>
          <div className="hidden sm:flex items-center gap-4">
            <span className="text-themed-secondary tabular-nums">
              {t('widgets.peakUsageHours.totalDownloads', {
                count: totalDownloads,
                formattedCount: formatCount(totalDownloads)
              })}
            </span>
            <span className="text-themed-secondary tabular-nums">
              {t('widgets.peakUsageHours.totalData', { size: formatBytes(totalBytesServed) })}
            </span>
          </div>
        </div>

        {/* Stats Summary - Single column on mobile, two columns on desktop */}
        <div className={`grid grid-cols-1 ${isTodayInRange ? 'sm:grid-cols-2' : ''} gap-3 mb-4`}>
          {/* Peak Hour */}
          <div
            className="p-3 flex flex-col justify-between min-h-[72px] themed-border-radius"
            style={{
              backgroundColor: 'var(--theme-warning-faint)'
            }}
          >
            <div className="flex items-center justify-between mb-1">
              <div className="flex items-center gap-2">
                <TrendingUp className="w-4 h-4 flex-shrink-0 text-themed-warning" />
                <span className="text-xs font-medium text-themed-warning">
                  {t('widgets.peakUsageHours.busiestHour')}
                </span>
              </div>
              {isMultiDayPeriod && (
                <span
                  className="text-[10px] px-1.5 py-0.5 rounded text-themed-warning"
                  style={{
                    backgroundColor: 'var(--theme-warning-subtle)'
                  }}
                >
                  {t('widgets.peakUsageHours.dailyAvg')}
                </span>
              )}
            </div>
            <div className="flex items-baseline gap-3">
              <div className="text-lg font-bold leading-tight text-themed-primary tabular-nums">
                {formatHour(peakHour)}
              </div>
              <div className="text-[11px] leading-tight text-themed-muted tabular-nums">
                {isMultiDayPeriod
                  ? `${(peakHourData?.avgDownloads ?? 0).toFixed(1)}${t('widgets.peakUsageHours.perDay')}`
                  : `${formatCount(peakHourData?.downloads ?? 0)} ${t('widgets.peakUsageHours.downloads')}`}
              </div>
              <div className="text-[11px] leading-tight text-themed-muted tabular-nums">
                {isMultiDayPeriod
                  ? `${formatBytes(peakHourData?.avgBytesServed ?? 0)}${t('widgets.peakUsageHours.perDay')}`
                  : formatBytes(peakHourData?.bytesServed ?? 0)}
              </div>
            </div>
          </div>

          {/* Current Hour - Hidden on mobile, only show on sm+ if today is in range */}
          {isTodayInRange && (
            <div
              className="hidden sm:flex p-3 flex-col justify-between min-h-[72px] themed-border-radius"
              style={{
                backgroundColor: 'var(--theme-primary-faint)'
              }}
            >
              <div className="flex items-center justify-between mb-1">
                <div className="flex items-center gap-2">
                  <Zap className="w-4 h-4 flex-shrink-0 text-themed-accent" />
                  <span className="text-xs font-medium text-themed-accent">
                    {currentHour === peakHour
                      ? t('widgets.peakUsageHours.nowPeakHour')
                      : t('widgets.peakUsageHours.typicalFor', {
                          hour: formatHour(currentHour, true)
                        })}
                  </span>
                </div>
                {/* Show % of peak for context - only when not the peak hour */}
                {currentHour !== peakHour &&
                  peakHourData &&
                  (isMultiDayPeriod ? peakHourData.avgDownloads : peakHourData.downloads) > 0 && (
                    <span
                      className="text-[10px] px-1.5 py-0.5 rounded text-themed-accent"
                      style={{
                        backgroundColor: 'var(--theme-primary-subtle)'
                      }}
                    >
                      {t('widgets.peakUsageHours.ofPeak', {
                        percent: isMultiDayPeriod
                          ? Math.round(
                              ((currentHourData?.avgDownloads ?? 0) / peakHourData.avgDownloads) *
                                100
                            )
                          : Math.round(
                              ((currentHourData?.downloads ?? 0) / peakHourData.downloads) * 100
                            )
                      })}
                    </span>
                  )}
                {isMultiDayPeriod && currentHour === peakHour && (
                  <span
                    className="text-[10px] px-1.5 py-0.5 rounded text-themed-accent"
                    style={{
                      backgroundColor: 'var(--theme-primary-subtle)'
                    }}
                  >
                    {t('widgets.peakUsageHours.dailyAvg')}
                  </span>
                )}
              </div>
              <div className="flex items-baseline gap-3">
                <div className="text-lg font-bold leading-tight text-themed-primary tabular-nums">
                  {formatHour(currentHour)}
                </div>
                <div className="text-[11px] leading-tight text-themed-muted tabular-nums">
                  {isMultiDayPeriod
                    ? `${(currentHourData?.avgDownloads ?? 0).toFixed(1)}${t('widgets.peakUsageHours.perDay')}`
                    : `${formatCount(currentHourData?.downloads ?? 0)} ${t('widgets.peakUsageHours.downloads')}`}
                </div>
                <div className="text-[11px] leading-tight text-themed-muted tabular-nums">
                  {isMultiDayPeriod
                    ? `${formatBytes(currentHourData?.avgBytesServed ?? 0)}${t('widgets.peakUsageHours.perDay')}`
                    : formatBytes(currentHourData?.bytesServed ?? 0)}
                </div>
              </div>
            </div>
          )}
        </div>

        {/* Heatmap Grid - 24 hour blocks */}
        <div className="mb-3">
          <div className="grid grid-cols-12 gap-1.5 p-1">
            {hourlyData.map((hourData) => {
              const isCurrentHour = isTodayInRange && hourData.hour === currentHour;
              const isPeakHour = hourData.hour === peakHour;

              return (
                <Tooltip
                  key={hourData.hour}
                  content={
                    <div className="text-xs space-y-1">
                      <div className="font-semibold text-themed-primary">
                        {formatHour(hourData.hour)}
                      </div>
                      {isMultiDayPeriod ? (
                        <>
                          <div className="text-themed-secondary">
                            {formatCount(hourData.avgDownloads)} avg downloads/day
                          </div>
                          <div className="text-themed-secondary">
                            {formatBytes(hourData.avgBytesServed)} avg served/day
                          </div>
                          <div className="pt-1 border-t border-themed-primary text-themed-muted">
                            Total: {formatCount(hourData.downloads)} downloads
                          </div>
                        </>
                      ) : (
                        <>
                          <div className="text-themed-secondary">
                            {formatCount(hourData.downloads)} downloads
                          </div>
                          <div className="text-themed-secondary">
                            {formatBytes(hourData.bytesServed)} served
                          </div>
                          {hourData.cacheHitBytes > 0 && (
                            <div className="text-themed-success">
                              {formatBytes(hourData.cacheHitBytes)} from cache
                            </div>
                          )}
                        </>
                      )}
                    </div>
                  }
                  position="top"
                >
                  <div
                    className="w-full h-5 rounded cursor-pointer transition-colors duration-200 hover:brightness-110"
                    style={{
                      backgroundColor: getIntensityColor(
                        hourData.downloads,
                        isCurrentHour,
                        isPeakHour
                      ),
                      boxShadow: isCurrentHour
                        ? '0 0 0 2px var(--theme-card-bg), 0 0 0 3px var(--theme-primary)'
                        : isPeakHour
                          ? '0 0 0 2px var(--theme-card-bg), 0 0 0 3px var(--theme-warning)'
                          : undefined
                    }}
                  />
                </Tooltip>
              );
            })}
          </div>

          {/* Hour labels - show key hours */}
          <div className="flex justify-between mt-2 text-[10px] text-themed-muted">
            <span>{formatHour(0, true)}</span>
            <span>{formatHour(6, true)}</span>
            <span>{formatHour(12, true)}</span>
            <span>{formatHour(18, true)}</span>
            <span>{formatHour(23, true)}</span>
          </div>
        </div>

        {/* Legend */}
        <div className="flex items-center justify-between pt-3 border-t border-themed-primary">
          <div className="flex items-center gap-4 text-xs text-themed-muted">
            {isTodayInRange && (
              <div className="flex items-center gap-1.5">
                <div
                  className="w-3 h-3 rounded"
                  style={{
                    backgroundColor: 'var(--theme-primary)',
                    boxShadow: '0 0 0 1px var(--theme-card-bg), 0 0 0 2px var(--theme-primary)'
                  }}
                />
                <span>{t('widgets.peakUsageHours.currentHourLabel')}</span>
              </div>
            )}
            <div className="flex items-center gap-1.5">
              <div
                className="w-3 h-3 rounded"
                style={{
                  backgroundColor: 'var(--theme-warning)',
                  boxShadow: '0 0 0 1px var(--theme-card-bg), 0 0 0 2px var(--theme-warning)'
                }}
              />
              <span>{t('widgets.peakUsageHours.busiestHourLabel')}</span>
            </div>
          </div>

          {/* Intensity scale */}
          <div className="flex items-center gap-1 text-xs text-themed-muted">
            <span>{t('widgets.peakUsageHours.less')}</span>
            <div className="flex gap-0.5">
              <div className="w-2.5 h-2.5 rounded-sm bg-themed-tertiary" />
              <div
                className="w-2.5 h-2.5 rounded-sm"
                style={{
                  backgroundColor: 'var(--theme-chart-1-muted)'
                }}
              />
              <div
                className="w-2.5 h-2.5 rounded-sm"
                style={{
                  backgroundColor: 'var(--theme-chart-1-strong)'
                }}
              />
              <div
                className="w-2.5 h-2.5 rounded-sm"
                style={{
                  backgroundColor: 'var(--theme-chart-1-emphasis)'
                }}
              />
              <div
                className="w-2.5 h-2.5 rounded-sm"
                style={{ backgroundColor: 'var(--theme-chart-1)' }}
              />
            </div>
            <span>{t('widgets.peakUsageHours.more')}</span>
          </div>
        </div>
      </div>
    );
  }
);

PeakUsageHours.displayName = 'PeakUsageHours';

export default PeakUsageHours;
