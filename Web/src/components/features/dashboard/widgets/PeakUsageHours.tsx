import React, { useMemo, memo } from 'react';
import { Clock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatCount } from '@utils/formatters';
import { type HourlyActivityItem } from '../../../../types';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { useTimezone } from '@contexts/useTimezone';
import { useHourlyActivity } from '@contexts/DashboardDataContext/hooks';
import { getCurrentHour } from '@utils/timezone';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';

interface PeakUsageHoursProps {
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
}

/**
 * Widget showing download activity by hour of day
 * Displays a heatmap-style visualization with clear Peak and Now indicators
 * Uses backend aggregation for efficiency
 * Intelligently handles multi-day ranges by showing averages
 */
const PeakUsageHours: React.FC<PeakUsageHoursProps> = memo(({ glassmorphism = false }) => {
  const { t } = useTranslation();
  const { use24HourFormat, useLocalTimezone } = useTimezone();

  // Consume hourly activity data from batched context
  const { hourlyActivity: displayData, loading, error, refetch } = useHourlyActivity();

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
      // Cells sit on the tertiary well surface, so idle needs its own step
      return 'var(--theme-bg-secondary-strong)';
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

  // Loading state — skeleton only on initial load (no prior data); SWR refetch keeps existing chart
  if (loading && !displayData) {
    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
        <div className="flex items-center gap-2 mb-3">
          <h3 className="text-lg font-semibold text-themed-primary">
            {t('widgets.peakUsageHours.title')}
          </h3>
        </div>
        <div className="peak-usage-skeleton">
          {/* Period totals bar */}
          <div className="peak-usage-skeleton-bar skeleton-shimmer" />

          {/* Two stat cards */}
          <div className="peak-usage-skeleton-cards">
            <div className="peak-usage-skeleton-card">
              <div className="peak-usage-skeleton-card-label skeleton-shimmer" />
              <div className="peak-usage-skeleton-card-value skeleton-shimmer" />
            </div>
            <div className="peak-usage-skeleton-card">
              <div className="peak-usage-skeleton-card-label skeleton-shimmer" />
              <div className="peak-usage-skeleton-card-value skeleton-shimmer" />
            </div>
          </div>

          {/* Heatmap grid */}
          <div className="peak-usage-skeleton-heatmap">
            {Array.from({ length: 24 }).map((_, i) => (
              <div key={i} className="peak-usage-skeleton-cell skeleton-shimmer" />
            ))}
          </div>

          {/* Legend */}
          <div className="peak-usage-skeleton-legend">
            <div className="peak-usage-skeleton-legend-item skeleton-shimmer" />
            <div className="peak-usage-skeleton-legend-item skeleton-shimmer" />
            <div className="peak-usage-skeleton-legend-item skeleton-shimmer" />
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
            {t('widgets.peakUsageHours.title')}
          </h3>
        </div>
        <EmptyState
          icon={Clock}
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

  // Empty state — data loaded but no downloads yet
  if (totalDownloads === 0) {
    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
        <div className="flex items-center gap-2 mb-3">
          <h3 className="text-lg font-semibold text-themed-primary">
            {t('widgets.peakUsageHours.title')}
          </h3>
        </div>
        <div className="well-surface dash-well p-3 flex-1 flex flex-col">
          <EmptyState
            variant="panel"
            icon={Clock}
            title={t('widgets.peakUsageHours.noDataTitle')}
            subtitle={t('widgets.peakUsageHours.noDataAvailable')}
          />
        </div>
      </div>
    );
  }

  return (
    <div className={`widget-card ${glassmorphism ? 'glass' : ''}`}>
      {/* Header */}
      <div className="flex items-center justify-between gap-2 mb-3 flex-wrap">
        <div className="flex items-center gap-2 min-w-0">
          <h3 className="text-lg font-semibold text-themed-primary">
            {t('widgets.peakUsageHours.title')}
          </h3>
          {loading && displayData && <LoadingSpinner size="xs" inline />}
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
          {isMultiDayPeriod && (
            <>
              <span>{t('widgets.peakUsageHours.days', { count: daysInPeriod })}</span>
              <span>·</span>
            </>
          )}
          <span className="hidden sm:inline">{t('widgets.peakUsageHours.mostActive')}</span>
          <span className="font-medium text-themed-warning">{peakTimeOfDay}</span>
        </div>
      </div>

      {/* Heatmap well - 24 hour blocks. flex-1 so the small row-stretch
          remainder lands inside the well instead of as dead card space */}
      <div className="well-surface dash-well p-3 flex-1 flex flex-col justify-center">
        <div className="grid grid-cols-12 gap-1.5">
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
                  className="w-full h-6 rounded cursor-pointer transition-colors duration-200 hover:brightness-110"
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
      <div className="flex items-center justify-between flex-wrap gap-2 mb-4">
        <div className="flex items-center gap-4 text-xs text-themed-muted">
          {isTodayInRange && (
            <div className="flex items-center gap-1.5">
              <div className="peak-legend-swatch peak-legend-swatch--now" />
              <span>{t('widgets.peakUsageHours.currentHourLabel')}</span>
            </div>
          )}
          <div className="flex items-center gap-1.5">
            <div className="peak-legend-swatch peak-legend-swatch--peak" />
            <span>{t('widgets.peakUsageHours.busiestHourLabel')}</span>
          </div>
        </div>

        {/* Intensity scale */}
        <div className="flex items-center gap-1 text-xs text-themed-muted">
          <span>{t('widgets.peakUsageHours.less')}</span>
          <div className="flex gap-0.5">
            <div className="peak-scale-swatch peak-scale-swatch--0" />
            <div className="peak-scale-swatch peak-scale-swatch--1" />
            <div className="peak-scale-swatch peak-scale-swatch--2" />
            <div className="peak-scale-swatch peak-scale-swatch--3" />
            <div className="peak-scale-swatch peak-scale-swatch--4" />
          </div>
          <span>{t('widgets.peakUsageHours.more')}</span>
        </div>
      </div>

      {/* Labeled readout strip — pinned to the card bottom to match the other panels */}
      <div className="dash-readout dash-readout--footer">
        <div className="dash-readout-item">
          <div className="dash-readout-value is-warning">{formatHour(peakHour)}</div>
          <div className="caps-label caps-label--wide dash-readout-label">
            {t('widgets.peakUsageHours.busiestHour')}
          </div>
        </div>
        {isTodayInRange && (
          <div className="dash-readout-item">
            <div className="dash-readout-value is-primary">{formatHour(currentHour)}</div>
            <div className="caps-label caps-label--wide dash-readout-label">
              {t('widgets.peakUsageHours.currentHourLabel')}
            </div>
          </div>
        )}
        <div className="dash-readout-item">
          <div className="dash-readout-value">{formatCount(totalDownloads)}</div>
          <div className="caps-label caps-label--wide dash-readout-label">
            {t('widgets.peakUsageHours.downloads')}
          </div>
        </div>
        <div className="dash-readout-item">
          <div className="dash-readout-value">{formatBytes(totalBytesServed)}</div>
          <div className="caps-label caps-label--wide dash-readout-label">
            {t('widgets.peakUsageHours.dataServed')}
          </div>
        </div>
      </div>
    </div>
  );
});

PeakUsageHours.displayName = 'PeakUsageHours';

export default PeakUsageHours;
