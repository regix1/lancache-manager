import React, { useState, useEffect, useMemo, memo } from 'react';
import { Clock, Loader2, TrendingUp, Zap } from 'lucide-react';
import { formatBytes } from '@utils/formatters';
import { type Download, type HourlyActivityItem } from '../../../../types';
import { Tooltip } from '@components/ui/Tooltip';
import { useTimezone } from '@contexts/TimezoneContext';
import { getServerTimezone } from '@utils/timezone';
import ApiService from '@services/api.service';

interface PeakUsageHoursProps {
  /** Time period for data (default: 7d) */
  period?: string;
  /** Whether to use glassmorphism style */
  glassmorphism?: boolean;
  /** Stagger index for entrance animation */
  staggerIndex?: number;
}

/**
 * Widget showing download activity by hour of day
 * Displays a heatmap-style visualization with clear Peak and Now indicators
 */
const PeakUsageHours: React.FC<PeakUsageHoursProps> = memo(({
  period = '7d',
  glassmorphism = true,
  staggerIndex,
}) => {
  const [downloads, setDownloads] = useState<Download[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { useLocalTimezone, use24HourFormat } = useTimezone();

  // Get current hour in the appropriate timezone
  const currentHour = useMemo(() => {
    const now = new Date();
    if (useLocalTimezone) {
      return now.getHours();
    }
    // Use server timezone
    const targetTimezone = getServerTimezone();
    if (targetTimezone) {
      const formatter = new Intl.DateTimeFormat('en-US', {
        timeZone: targetTimezone,
        hour: 'numeric',
        hour12: false,
      });
      return parseInt(formatter.format(now), 10);
    }
    return now.getHours();
  }, [useLocalTimezone]);

  // Calculate time range based on period
  const getTimeRange = (period: string): { startTime?: number; endTime?: number } => {
    const now = Math.floor(Date.now() / 1000);
    const periodMap: Record<string, number> = {
      '1h': 3600,
      '6h': 6 * 3600,
      '12h': 12 * 3600,
      '24h': 24 * 3600,
      '7d': 7 * 24 * 3600,
      '30d': 30 * 24 * 3600,
    };
    const seconds = periodMap[period];
    if (seconds) {
      return { startTime: now - seconds, endTime: now };
    }
    return {};
  };

  // Fetch downloads from API
  useEffect(() => {
    const controller = new AbortController();

    const fetchData = async () => {
      try {
        setLoading(true);
        setError(null);
        const { startTime, endTime } = getTimeRange(period);
        const response = await ApiService.getLatestDownloads(controller.signal, 'unlimited', startTime, endTime);
        setDownloads(response);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError('Failed to load hourly data');
          console.error('PeakUsageHours fetch error:', err);
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    };

    fetchData();

    return () => controller.abort();
  }, [period]);

  // Group downloads by hour based on timezone preference
  const hourlyData = useMemo((): HourlyActivityItem[] => {
    const hourBuckets: Map<number, HourlyActivityItem> = new Map();
    for (let i = 0; i < 24; i++) {
      hourBuckets.set(i, {
        hour: i,
        downloads: 0,
        bytesServed: 0,
        cacheHitBytes: 0,
        cacheMissBytes: 0,
      });
    }

    if (downloads.length === 0) {
      return Array.from(hourBuckets.values());
    }

    const targetTimezone = useLocalTimezone ? undefined : getServerTimezone();

    downloads.forEach(download => {
      if (!download.startTimeUtc) return;

      const date = new Date(download.startTimeUtc);
      let hour: number;

      if (targetTimezone) {
        const formatter = new Intl.DateTimeFormat('en-US', {
          timeZone: targetTimezone,
          hour: 'numeric',
          hour12: false,
        });
        hour = parseInt(formatter.format(date), 10);
      } else {
        hour = date.getHours();
      }

      const bucket = hourBuckets.get(hour)!;
      bucket.downloads += 1;
      bucket.bytesServed += download.totalBytes || 0;
      bucket.cacheHitBytes += download.cacheHitBytes || 0;
      bucket.cacheMissBytes += download.cacheMissBytes || 0;
    });

    return Array.from(hourBuckets.values()).sort((a, b) => a.hour - b.hour);
  }, [downloads, useLocalTimezone]);

  // Find max for scaling
  const maxDownloads = useMemo(() => {
    const max = Math.max(...hourlyData.map(h => h.downloads));
    return max || 1;
  }, [hourlyData]);

  // Calculate peak hour
  const peakHour = useMemo(() => {
    const peak = hourlyData.reduce((max, h) => h.downloads > max.downloads ? h : max, hourlyData[0]);
    return peak?.hour ?? 0;
  }, [hourlyData]);

  const totalDownloads = useMemo(() => {
    return hourlyData.reduce((sum, h) => sum + h.downloads, 0);
  }, [hourlyData]);

  // Format hour for display based on 12h/24h preference
  const formatHour = (hour: number, short: boolean = false): string => {
    if (use24HourFormat) {
      return short ? `${hour.toString().padStart(2, '0')}` : `${hour.toString().padStart(2, '0')}:00`;
    }
    const ampm = hour >= 12 ? 'PM' : 'AM';
    const h = hour % 12 || 12;
    return short ? `${h}${ampm}` : `${h}:00 ${ampm}`;
  };

  // Get intensity color based on activity level (heatmap style)
  const getIntensityColor = (downloads: number, isCurrentHour: boolean, isPeakHour: boolean): string => {
    if (downloads === 0) {
      return 'var(--theme-bg-tertiary)';
    }

    const intensity = downloads / maxDownloads;

    // Peak hour gets special color
    if (isPeakHour && downloads > 0) {
      return 'var(--theme-warning)';
    }

    // Current hour gets primary color
    if (isCurrentHour) {
      return 'var(--theme-primary)';
    }

    // Use intensity-based coloring
    if (intensity > 0.75) {
      return 'var(--theme-chart-1)';
    } else if (intensity > 0.5) {
      return 'color-mix(in srgb, var(--theme-chart-1) 75%, var(--theme-bg-tertiary))';
    } else if (intensity > 0.25) {
      return 'color-mix(in srgb, var(--theme-chart-1) 50%, var(--theme-bg-tertiary))';
    } else {
      return 'color-mix(in srgb, var(--theme-chart-1) 30%, var(--theme-bg-tertiary))';
    }
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
          <Clock className="w-5 h-5" style={{ color: 'var(--theme-text-muted)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Peak Usage Hours
          </h3>
        </div>
        <div className="flex items-center justify-center h-32">
          <Loader2 className="w-6 h-6 animate-spin" style={{ color: 'var(--theme-primary)' }} />
        </div>
      </div>
    );
  }

  // Error or empty state
  if (error || totalDownloads === 0) {
    return (
      <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
        <div className="flex items-center gap-2 mb-3">
          <Clock className="w-5 h-5" style={{ color: 'var(--theme-text-muted)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Peak Usage Hours
          </h3>
        </div>
        <p className="text-sm" style={{ color: 'var(--theme-text-muted)' }}>
          {error || 'No download data available yet'}
        </p>
      </div>
    );
  }

  // Get peak hour data for stats
  const peakHourData = hourlyData.find(h => h.hour === peakHour);
  const currentHourData = hourlyData.find(h => h.hour === currentHour);

  return (
    <div className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}>
      {/* Header */}
      <div className="flex items-center justify-between mb-4 h-5">
        <div className="flex items-center gap-2">
          <Clock className="w-5 h-5 flex-shrink-0" style={{ color: 'var(--theme-primary)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Peak Usage Hours
          </h3>
        </div>
        <div className="text-xs min-w-[80px] text-right" style={{ color: 'var(--theme-text-muted)', fontVariantNumeric: 'tabular-nums' }}>
          {totalDownloads.toLocaleString()} downloads
        </div>
      </div>

      {/* Stats Summary - Fixed height containers to prevent layout shift */}
      <div className="grid grid-cols-2 gap-3 mb-4" style={{ minHeight: '76px' }}>
        {/* Peak Hour */}
        <div
          className="rounded-lg p-3 flex flex-col justify-between"
          style={{
            backgroundColor: 'color-mix(in srgb, var(--theme-warning) 10%, transparent)',
            height: '76px'
          }}
        >
          <div className="flex items-center gap-2">
            <TrendingUp className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-warning)' }} />
            <span className="text-xs font-medium" style={{ color: 'var(--theme-warning)' }}>
              Peak
            </span>
          </div>
          <div>
            <div className="text-lg font-bold leading-tight" style={{ color: 'var(--theme-text-primary)', fontVariantNumeric: 'tabular-nums' }}>
              {formatHour(peakHour)}
            </div>
            <div className="text-xs leading-tight" style={{ color: 'var(--theme-text-muted)', fontVariantNumeric: 'tabular-nums' }}>
              {(peakHourData?.downloads ?? 0).toLocaleString()} downloads
            </div>
          </div>
        </div>

        {/* Current Hour */}
        <div
          className="rounded-lg p-3 flex flex-col justify-between"
          style={{
            backgroundColor: 'color-mix(in srgb, var(--theme-primary) 10%, transparent)',
            height: '76px'
          }}
        >
          <div className="flex items-center gap-2">
            <Zap className="w-4 h-4 flex-shrink-0" style={{ color: 'var(--theme-primary)' }} />
            <span className="text-xs font-medium" style={{ color: 'var(--theme-primary)' }}>
              Now
            </span>
          </div>
          <div>
            <div className="text-lg font-bold leading-tight" style={{ color: 'var(--theme-text-primary)', fontVariantNumeric: 'tabular-nums' }}>
              {formatHour(currentHour)}
            </div>
            <div className="text-xs leading-tight" style={{ color: 'var(--theme-text-muted)', fontVariantNumeric: 'tabular-nums' }}>
              {(currentHourData?.downloads ?? 0).toLocaleString()} downloads
            </div>
          </div>
        </div>
      </div>

      {/* Heatmap Grid - 24 hour blocks */}
      <div className="mb-3">
        <div className="grid grid-cols-12 gap-1.5 p-1">
          {hourlyData.map((hourData) => {
            const isCurrentHour = hourData.hour === currentHour;
            const isPeakHour = hourData.hour === peakHour;

            return (
              <Tooltip
                key={hourData.hour}
                content={
                  <div className="text-xs space-y-1">
                    <div className="font-semibold">{formatHour(hourData.hour)}</div>
                    <div>{hourData.downloads.toLocaleString()} downloads</div>
                    <div>{formatBytes(hourData.bytesServed)} served</div>
                    {hourData.cacheHitBytes > 0 && (
                      <div className="text-green-400">
                        {formatBytes(hourData.cacheHitBytes)} from cache
                      </div>
                    )}
                  </div>
                }
                position="top"
              >
                <div
                  className="w-full h-5 rounded cursor-pointer transition-colors duration-200 hover:brightness-110"
                  style={{
                    backgroundColor: getIntensityColor(hourData.downloads, isCurrentHour, isPeakHour),
                    boxShadow: isCurrentHour
                      ? '0 0 0 2px var(--theme-card-bg), 0 0 0 3px var(--theme-primary)'
                      : isPeakHour
                      ? '0 0 0 2px var(--theme-card-bg), 0 0 0 3px var(--theme-warning)'
                      : undefined,
                  }}
                />
              </Tooltip>
            );
          })}
        </div>

        {/* Hour labels - show key hours */}
        <div className="flex justify-between mt-2 text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
          <span>{formatHour(0, true)}</span>
          <span>{formatHour(6, true)}</span>
          <span>{formatHour(12, true)}</span>
          <span>{formatHour(18, true)}</span>
          <span>{formatHour(23, true)}</span>
        </div>
      </div>

      {/* Legend */}
      <div className="flex items-center justify-between pt-3 border-t" style={{ borderColor: 'var(--theme-border-primary)' }}>
        <div className="flex items-center gap-4 text-xs" style={{ color: 'var(--theme-text-muted)' }}>
          <div className="flex items-center gap-1.5">
            <div
              className="w-3 h-3 rounded"
              style={{
                backgroundColor: 'var(--theme-primary)',
                boxShadow: '0 0 0 1px var(--theme-card-bg), 0 0 0 2px var(--theme-primary)'
              }}
            />
            <span>Now</span>
          </div>
          <div className="flex items-center gap-1.5">
            <div
              className="w-3 h-3 rounded"
              style={{
                backgroundColor: 'var(--theme-warning)',
                boxShadow: '0 0 0 1px var(--theme-card-bg), 0 0 0 2px var(--theme-warning)'
              }}
            />
            <span>Peak</span>
          </div>
        </div>

        {/* Intensity scale */}
        <div className="flex items-center gap-1 text-xs" style={{ color: 'var(--theme-text-muted)' }}>
          <span>Less</span>
          <div className="flex gap-0.5">
            <div className="w-2.5 h-2.5 rounded-sm" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }} />
            <div className="w-2.5 h-2.5 rounded-sm" style={{ backgroundColor: 'color-mix(in srgb, var(--theme-chart-1) 30%, var(--theme-bg-tertiary))' }} />
            <div className="w-2.5 h-2.5 rounded-sm" style={{ backgroundColor: 'color-mix(in srgb, var(--theme-chart-1) 50%, var(--theme-bg-tertiary))' }} />
            <div className="w-2.5 h-2.5 rounded-sm" style={{ backgroundColor: 'color-mix(in srgb, var(--theme-chart-1) 75%, var(--theme-bg-tertiary))' }} />
            <div className="w-2.5 h-2.5 rounded-sm" style={{ backgroundColor: 'var(--theme-chart-1)' }} />
          </div>
          <span>More</span>
        </div>
      </div>
    </div>
  );
});

PeakUsageHours.displayName = 'PeakUsageHours';

export default PeakUsageHours;
