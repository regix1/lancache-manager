import React, { useMemo, memo, useState } from 'react';
import { Clock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatCount } from '@utils/formatters';
import { type HourlyActivityItem, type HourlyActivityResponse } from '../../../../types';
import { Tooltip } from '@components/ui/Tooltip';
import { HelpPopover, HelpSection, HelpNote, HelpDefinition } from '@components/ui/HelpPopover';
import { useTimezone } from '@contexts/useTimezone';
import { useHourlyActivity } from '@contexts/DashboardDataContext/hooks';
import {
  getCurrentHour,
  getDayBoundsInTimezone,
  getEffectiveTimezone,
  getTimeInTimezone
} from '@utils/timezone';
import { Button } from '@components/ui/Button';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { EmptyState } from '@components/ui/ManagerCard';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { WidgetPanel } from '../WidgetPanel';
import { hourlyMetricValue, type PeakUsageMetric } from './peakUsageMetric';
import {
  PEAK_USAGE_ROW_HOURS,
  isPeakUsageAxisColumn,
  peakUsageClockLabel,
  peakUsageColumn,
  peakUsageRow
} from './peakUsageAxis';

interface PeakUsageHoursProps {
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** The dashboard's range chip, shown beside the title. */
  badge?: React.ReactNode;
}

/**
 * Whether the reader's today falls inside the range the buckets were counted over. Both ends are
 * read on the reader's clock, because a calendar day begins at a different moment in another zone.
 */
function todayOverlapsPeriod(
  hourlyActivity: HourlyActivityResponse | null,
  viewerZone: string
): boolean {
  if (!hourlyActivity) return true; // Assume yes while loading

  if (!hourlyActivity.periodStart && !hourlyActivity.periodEnd) return true;

  const now = Math.floor(Date.now() / 1000);
  const today = getDayBoundsInTimezone(new Date(), viewerZone);
  const todayStart = Math.floor(today.start.getTime() / 1000);
  const todayEnd = Math.floor(today.end.getTime() / 1000);

  const periodStart = hourlyActivity.periodStart ?? 0;
  const periodEnd = hourlyActivity.periodEnd ?? now;

  return todayStart <= periodEnd && todayEnd >= periodStart;
}

function PeakUsageHourAxis({
  use24HourFormat,
  rowStartHour = 0,
  position
}: {
  use24HourFormat: boolean;
  rowStartHour?: number;
  position?: 'first' | 'second';
}) {
  return (
    <div
      className={`peak-usage-hour-axis text-themed-muted${
        position ? ` peak-usage-hour-axis--${position}` : ''
      }`}
    >
      {Array.from({ length: PEAK_USAGE_ROW_HOURS }, (_, column) => (
        <span key={column} className="peak-usage-hour-label">
          {isPeakUsageAxisColumn(column)
            ? peakUsageClockLabel(column, use24HourFormat, rowStartHour)
            : null}
        </span>
      ))}
    </div>
  );
}

function PeakUsageAxisSkeleton({ position }: { position?: 'first' | 'second' }) {
  return (
    <div className={`peak-usage-hour-axis${position ? ` peak-usage-hour-axis--${position}` : ''}`}>
      {Array.from({ length: PEAK_USAGE_ROW_HOURS }, (_, column) =>
        isPeakUsageAxisColumn(column) ? (
          <div key={column} className="peak-usage-skeleton-axis-tick skeleton-shimmer" />
        ) : (
          <span key={column} />
        )
      )}
    </div>
  );
}

/**
 * Widget showing download activity by hour of day
 * Displays a heatmap-style visualization with clear Peak and Now indicators
 * Uses backend aggregation for efficiency
 * Intelligently handles multi-day ranges by showing averages
 */
const PeakUsageHours: React.FC<PeakUsageHoursProps> = memo(({ glassmorphism = false, badge }) => {
  const { t } = useTranslation();
  const { use24HourFormat } = useTimezone();

  // Bytes is the default because it is what the widget has always shown, and because one large
  // game outweighing many small ones is the case the heatmap is usually being read for.
  const [metric, setMetric] = useState<PeakUsageMetric>('bytes');

  // Consume hourly activity data from batched context
  const { hourlyActivity: displayData, loading, error, failed, refetch } = useHourlyActivity();

  const viewerZone = getEffectiveTimezone();

  // Both are recomputed every render rather than memoized: in a memo keyed on the response they
  // freeze on the day the page loaded, and a page left open past midnight marks the wrong hour.
  const currentHour = getCurrentHour();
  const isTodayInRange = todayOverlapsPeriod(displayData, viewerZone);

  // Determine if we should show averages (multi-day period)
  const daysInPeriod = displayData?.daysInPeriod ?? 1;
  const isMultiDayPeriod = daysInPeriod > 1;

  // Never zero-filled to 24 hours: a failed section sends no buckets, and zeros would draw a full
  // day of silence nobody measured.
  const hourlyData = useMemo((): HourlyActivityItem[] => displayData?.hours ?? [], [displayData]);

  const hourlyByHour = useMemo(() => {
    const hours = new Map<number, HourlyActivityItem>();
    for (const item of hourlyData) {
      hours.set(item.hour, item);
    }
    return hours;
  }, [hourlyData]);

  // What a full-brightness cell means. Rescaled per metric, so switching re-shades the grid
  // against the busiest hour for the figure now being read rather than against bytes.
  const maxMetricValue = useMemo(() => {
    const max = Math.max(0, ...hourlyData.map((h) => hourlyMetricValue(h, metric)));
    return max || 1;
  }, [hourlyData, metric]);

  // Which hours of the day the range reached, or null when every cell on the grid was measured.
  const measuredWindow = useMemo((): { seconds: number; hours: Set<number> } | null => {
    if (!displayData || displayData.period !== 'filtered') return null;
    const { periodStart, periodEnd } = displayData;
    if (periodStart == null || periodEnd == null) return null;
    // 86_400 seconds is a full turn of the clock, so every hour of the day is inside the range.
    const seconds = periodEnd - periodStart;
    if (seconds >= 86_400) return null;

    const hours = new Set<number>();
    for (let at = periodStart; at < periodEnd; at += 3_600) {
      hours.add(getTimeInTimezone(new Date(at * 1000), viewerZone).hour);
    }
    // The step above lands on the range's last hour only when the range ends on an hour boundary.
    hours.add(getTimeInTimezone(new Date((periodEnd - 1) * 1000), viewerZone).hour);
    return { seconds, hours };
  }, [displayData, viewerZone]);

  // Buckets carry an hour number even when nothing was recorded, so a length check never fires.
  // Bytes count too: a range can hold only the tail of a download that began before it, which
  // arrives as served bytes with no download count.
  const hasHourlyActivity = hourlyData.some((h) => h.downloads > 0 || h.bytesServed > 0);
  const totalDownloads = displayData?.totalDownloads ?? 0;

  // Calculate total bytes served across all hours
  const totalBytesServed = useMemo(() => {
    return hourlyData.reduce((sum, h) => sum + h.bytesServed, 0);
  }, [hourlyData]);

  const metricTotal = metric === 'bytes' ? totalBytesServed : totalDownloads;

  // A range of 3_600 seconds measured a single hour however it straddles the clock, so crowning it
  // would rank that hour against 23 cells nothing was measured in.
  const hasBusiestHour =
    metricTotal > 0 && (measuredWindow === null || measuredWindow.seconds > 3_600);

  const marksCurrentHour =
    isTodayInRange && (measuredWindow === null || measuredWindow.hours.has(currentHour));

  // Determine time-of-day category for the peak hour
  const getTimeOfDayLabel = (hour: number): string => {
    if (hour >= 5 && hour < 12) return t('widgets.peakUsageHours.morning');
    if (hour >= 12 && hour < 17) return t('widgets.peakUsageHours.afternoon');
    if (hour >= 17 && hour < 21) return t('widgets.peakUsageHours.evening');
    return t('widgets.peakUsageHours.night');
  };

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
  // The scale classes are declared after the ring classes in dashboard.css, so a cell carrying both
  // keeps the fill chosen here and takes only the ring from the marker class.
  const getIntensityColor = (value: number, isPeakHour: boolean): string => {
    if (value === 0) {
      // Cells sit on the tertiary well surface, so idle needs its own step
      return 'peak-scale-swatch--0';
    }

    const intensity = value / maxMetricValue;

    // Peak hour gets special color
    if (isPeakHour && value > 0) {
      return 'peak-legend-swatch--peak';
    }

    // Use intensity-based coloring
    if (intensity > 0.75) {
      return 'peak-scale-swatch--4';
    } else if (intensity > 0.5) {
      return 'peak-scale-swatch--3';
    } else if (intensity > 0.25) {
      return 'peak-scale-swatch--2';
    } else {
      return 'peak-scale-swatch--1';
    }
  };

  // Loading state — skeleton only on initial load (no prior data); SWR refetch keeps existing chart
  if (loading && !displayData) {
    return (
      <WidgetPanel glass={glassmorphism}>
        <div className="flex items-center gap-2 mb-3">
          <h3 className="dash-panel-title">{t('widgets.peakUsageHours.title')}</h3>
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
          <div
            className={`peak-usage-heatmap-block${use24HourFormat ? ' peak-usage-heatmap-block--24hour' : ''}`}
          >
            {use24HourFormat ? (
              <PeakUsageAxisSkeleton position="first" />
            ) : (
              <div className="peak-usage-skeleton-row-label peak-usage-skeleton-row-label--am skeleton-shimmer" />
            )}
            <div className="peak-usage-heatmap peak-usage-heatmap--am">
              {Array.from({ length: PEAK_USAGE_ROW_HOURS }).map((_, i) => (
                <div key={`am-${i}`} className="peak-usage-skeleton-cell skeleton-shimmer" />
              ))}
            </div>
            {use24HourFormat ? null : (
              <div className="peak-usage-skeleton-row-label peak-usage-skeleton-row-label--pm skeleton-shimmer" />
            )}
            <div className="peak-usage-heatmap peak-usage-heatmap--pm">
              {Array.from({ length: PEAK_USAGE_ROW_HOURS }).map((_, i) => (
                <div key={`pm-${i}`} className="peak-usage-skeleton-cell skeleton-shimmer" />
              ))}
            </div>
            <PeakUsageAxisSkeleton position={use24HourFormat ? 'second' : undefined} />
          </div>

          {/* Legend */}
          <div className="peak-usage-skeleton-legend">
            <div className="peak-usage-skeleton-legend-item skeleton-shimmer" />
            <div className="peak-usage-skeleton-legend-item skeleton-shimmer" />
            <div className="peak-usage-skeleton-legend-item skeleton-shimmer" />
          </div>
        </div>
        {badge ? <div className="dash-range-footer">{badge}</div> : null}
      </WidgetPanel>
    );
  }

  // Error state — the whole fetch failed, or only this section's query did
  if (failed || (error && !displayData)) {
    return (
      <WidgetPanel glass={glassmorphism}>
        <div className="flex items-center gap-2 mb-3">
          <h3 className="dash-panel-title">{t('widgets.peakUsageHours.title')}</h3>
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
        {badge ? <div className="dash-range-footer">{badge}</div> : null}
      </WidgetPanel>
    );
  }

  // Empty state — nothing recorded, and equally totals that arrived without their hourly buckets
  if (!displayData || !hasHourlyActivity) {
    return (
      <WidgetPanel glass={glassmorphism}>
        <div className="flex items-center gap-2 mb-3">
          <h3 className="dash-panel-title">{t('widgets.peakUsageHours.title')}</h3>
        </div>
        <div className="well-surface dash-well p-3 flex-1 flex flex-col">
          <EmptyState
            variant="panel"
            icon={Clock}
            title={t('widgets.peakUsageHours.noDataTitle')}
            subtitle={t('widgets.peakUsageHours.noDataAvailable')}
          />
        </div>
        {badge ? <div className="dash-range-footer">{badge}</div> : null}
      </WidgetPanel>
    );
  }

  // The response carries a peak hour, but it is always the one that served the most bytes, so it
  // cannot answer for the downloads view. Both are read off the same buckets the grid draws, and
  // keeping the first of any tie matches what the server does for the bytes case.
  const peakHour = hourlyData.reduce(
    (busiest, hour) =>
      hourlyMetricValue(hour, metric) > hourlyMetricValue(busiest, metric) ? hour : busiest,
    hourlyData[0]
  ).hour;
  const peakTimeOfDay = getTimeOfDayLabel(peakHour);

  const renderHourCell = (hour: number) => {
    const hourData = hourlyByHour.get(hour);
    if (!hourData || (measuredWindow !== null && !measuredWindow.hours.has(hour))) {
      // An idle fill would read as an hour that was watched and stayed quiet.
      return (
        <div
          key={hour}
          className="w-full h-6 rounded border border-dashed border-themed-primary opacity-40"
        />
      );
    }

    const isCurrentHour = marksCurrentHour && hour === currentHour;
    const isPeakHour = hasBusiestHour && hour === peakHour;
    const cellValue = hourlyMetricValue(hourData, metric);
    // The current hour keeps whatever fill its activity earned and is marked by the ring
    // alone. Filling it would paint the same blue the busiest intensity step uses, so the
    // legend would teach one colour for "now" and the grid would spend it on "most active".
    const markerClass = isCurrentHour ? 'peak-legend-swatch--now' : '';

    return (
      <Tooltip
        key={hour}
        content={
          <div className="text-xs space-y-1">
            <div className="font-semibold text-themed-primary">{formatHour(hour)}</div>
            {isMultiDayPeriod ? (
              <>
                <div className="text-themed-secondary">
                  {formatCount(hourData.avgDownloads)}{' '}
                  {t('widgets.peakUsageHours.tooltip.avgDownloadsPerDay')}
                </div>
                <div className="text-themed-secondary">
                  {formatBytes(hourData.avgBytesServed)}{' '}
                  {t('widgets.peakUsageHours.tooltip.avgServedPerDay')}
                </div>
                <div className="pt-1 border-t border-themed-primary text-themed-muted">
                  {t('widgets.peakUsageHours.tooltip.totalDownloads', {
                    count: hourData.downloads
                  })}
                </div>
              </>
            ) : (
              <>
                <div className="text-themed-secondary">
                  {formatCount(hourData.downloads)} {t('widgets.peakUsageHours.tooltip.downloads')}
                </div>
                <div className="text-themed-secondary">
                  {formatBytes(hourData.bytesServed)} {t('widgets.peakUsageHours.tooltip.served')}
                </div>
                {hourData.cacheHitBytes > 0 && (
                  <div className="text-themed-success">
                    {formatBytes(hourData.cacheHitBytes)}{' '}
                    {t('widgets.peakUsageHours.tooltip.fromCache')}
                  </div>
                )}
              </>
            )}
          </div>
        }
        position="top"
      >
        <div
          className={`w-full h-6 rounded cursor-pointer transition-colors duration-200 hover:brightness-110 ${getIntensityColor(
            cellValue,
            isPeakHour
          )} ${markerClass}`}
        />
      </Tooltip>
    );
  };

  return (
    <WidgetPanel glass={glassmorphism}>
      {/* Header */}
      {/* The heading gets a row to itself; the period line and the metric picker share the row
          under it, so the title is never squeezed against a control. */}
      <div className="mb-3">
        <div className="flex items-center gap-2 min-w-0">
          <h3 className="dash-panel-title">{t('widgets.peakUsageHours.title')}</h3>
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
                  ...(hasBusiestHour
                    ? [
                        {
                          term: t('widgets.peakUsageHours.peakHour.term'),
                          description: isMultiDayPeriod
                            ? t('widgets.peakUsageHours.peakHour.multiDay')
                            : t('widgets.peakUsageHours.peakHour.singleDay')
                        }
                      ]
                    : []),
                  ...(marksCurrentHour
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
            <HelpNote type="info">
              {metric === 'bytes'
                ? t('widgets.peakUsageHours.heatmapNoteBytes')
                : t('widgets.peakUsageHours.heatmapNoteDownloads')}
            </HelpNote>
          </HelpPopover>
        </div>
        {/* The period line and the picker share this row. It renders even with nothing to say
            about the period, because the picker still has to land somewhere. */}
        <div className="flex items-center justify-between gap-2 mt-2">
          <div className="flex items-center gap-2 text-xs text-themed-muted min-w-0">
            {isMultiDayPeriod && (
              <span>{t('widgets.peakUsageHours.days', { count: daysInPeriod })}</span>
            )}
            {isMultiDayPeriod && hasBusiestHour && <span>·</span>}
            {hasBusiestHour && (
              <>
                <span>{t('widgets.peakUsageHours.mostActive')}</span>
                <span className="font-medium text-themed-warning">{peakTimeOfDay}</span>
              </>
            )}
          </div>
          <EnhancedDropdown
            options={[
              { value: 'bytes', label: t('widgets.peakUsageHours.metric.bytes') },
              { value: 'downloads', label: t('widgets.peakUsageHours.metric.downloads') }
            ]}
            value={metric}
            onChange={(next: string) => setMetric(next as PeakUsageMetric)}
            size="sm"
            variant="button"
            alignRight
            triggerAriaLabel={t('widgets.peakUsageHours.metric.label')}
            className="peak-usage-metric-select"
          />
        </div>
      </div>

      {/* Heatmap well - 24 hour blocks. flex-1 so the small row-stretch
          remainder lands inside the well instead of as dead card space */}
      <div className="well-surface dash-well p-3 flex-1 flex flex-col justify-center">
        <div
          className={`peak-usage-heatmap-block${use24HourFormat ? ' peak-usage-heatmap-block--24hour' : ''}`}
        >
          {use24HourFormat ? (
            <PeakUsageHourAxis use24HourFormat rowStartHour={0} position="first" />
          ) : (
            <span className="caps-label caps-label--sm peak-usage-heatmap-row-label peak-usage-heatmap-row-label--am">
              {t('common.dateTimePicker.am')}
            </span>
          )}
          <div className="peak-usage-heatmap peak-usage-heatmap--am">
            {Array.from({ length: 24 }, (_, hour) => hour)
              .filter((hour) => peakUsageRow(hour) === 0)
              .sort((a, b) => peakUsageColumn(a) - peakUsageColumn(b))
              .map(renderHourCell)}
          </div>
          {use24HourFormat ? null : (
            <span className="caps-label caps-label--sm peak-usage-heatmap-row-label peak-usage-heatmap-row-label--pm">
              {t('common.dateTimePicker.pm')}
            </span>
          )}
          <div className="peak-usage-heatmap peak-usage-heatmap--pm">
            {Array.from({ length: 24 }, (_, hour) => hour)
              .filter((hour) => peakUsageRow(hour) === 1)
              .sort((a, b) => peakUsageColumn(a) - peakUsageColumn(b))
              .map(renderHourCell)}
          </div>
          <PeakUsageHourAxis
            use24HourFormat={use24HourFormat}
            rowStartHour={use24HourFormat ? PEAK_USAGE_ROW_HOURS : 0}
            position={use24HourFormat ? 'second' : undefined}
          />
        </div>
      </div>

      {/* Legend */}
      <div className="flex items-center justify-between flex-wrap gap-2 mb-4">
        <div className="flex items-center gap-4 text-xs text-themed-muted">
          {marksCurrentHour && (
            <div className="flex items-center gap-1.5">
              <div className="peak-legend-swatch peak-legend-swatch--now" />
              <span>{t('widgets.peakUsageHours.currentHourLabel')}</span>
            </div>
          )}
          {hasBusiestHour && (
            <div className="flex items-center gap-1.5">
              <div className="peak-legend-swatch peak-legend-swatch--peak" />
              <span>{t('widgets.peakUsageHours.busiestHourLabel')}</span>
            </div>
          )}
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
        {hasBusiestHour && (
          <div className="dash-readout-item">
            <div className="dash-readout-value is-warning">{formatHour(peakHour)}</div>
            <div className="caps-label caps-label--wide dash-readout-label">
              {t('widgets.peakUsageHours.busiestHour')}
            </div>
          </div>
        )}
        {marksCurrentHour && (
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
      {badge ? <div className="dash-range-footer dash-range-footer--seamless">{badge}</div> : null}
    </WidgetPanel>
  );
});

PeakUsageHours.displayName = 'PeakUsageHours';

export default PeakUsageHours;
