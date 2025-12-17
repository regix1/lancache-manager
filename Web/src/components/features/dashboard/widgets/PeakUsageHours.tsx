import React, { useState, useEffect, useMemo, memo } from 'react';
import { Clock, Loader2 } from 'lucide-react';
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
 * Groups downloads by hour based on user's timezone preference
 */
const PeakUsageHours: React.FC<PeakUsageHoursProps> = memo(({
  period = '7d',
  glassmorphism = true,
  staggerIndex,
}) => {
  const [downloads, setDownloads] = useState<Download[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { useLocalTimezone } = useTimezone();
  const currentHour = new Date().getHours();

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
    // For 'all' or 'live', return no filter
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
    // Initialize all 24 hours with zeros
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

    // Get target timezone
    const targetTimezone = useLocalTimezone ? undefined : getServerTimezone();

    // Group each download by its hour in the target timezone
    downloads.forEach(download => {
      if (!download.startTimeUtc) return;

      const date = new Date(download.startTimeUtc);
      let hour: number;

      if (targetTimezone) {
        // Convert to server timezone and get hour
        const formatter = new Intl.DateTimeFormat('en-US', {
          timeZone: targetTimezone,
          hour: 'numeric',
          hour12: false,
        });
        hour = parseInt(formatter.format(date), 10);
      } else {
        // Use browser local time
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

  // Calculate peak hour and total
  const peakHour = useMemo(() => {
    const peak = hourlyData.reduce((max, h) => h.downloads > max.downloads ? h : max, hourlyData[0]);
    return peak?.hour ?? 0;
  }, [hourlyData]);

  const totalDownloads = useMemo(() => {
    return hourlyData.reduce((sum, h) => sum + h.downloads, 0);
  }, [hourlyData]);

  // Format hour for display
  const formatHour = (hour: number): string => {
    const ampm = hour >= 12 ? 'PM' : 'AM';
    const h = hour % 12 || 12;
    return `${h}${ampm}`;
  };

  // Get bar color based on whether it's current hour or peak
  const getBarColor = (hour: number): string => {
    if (hour === currentHour) {
      return 'var(--theme-primary)';
    }
    if (hour === peakHour) {
      return 'var(--theme-warning)';
    }
    return 'var(--theme-chart-1)';
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
        <div className="flex items-center justify-center h-24">
          <Loader2 className="w-6 h-6 animate-spin" style={{ color: 'var(--theme-primary)' }} />
        </div>
      </div>
    );
  }

  // Error or empty state
  if (error || totalDownloads === 0) {
    return (
      <div
        className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}
      >
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

  return (
    <div
      className={`widget-card ${glassmorphism ? 'glass' : ''} ${animationClasses}`}
    >
      {/* Header */}
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-2">
          <Clock className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
          <h3 className="text-sm font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
            Peak Usage Hours
          </h3>
        </div>
        <div className="flex items-center gap-2">
          <span
            className="text-xs px-1.5 py-0.5 rounded"
            style={{
              color: 'var(--theme-warning)',
              backgroundColor: 'color-mix(in srgb, var(--theme-warning) 15%, transparent)',
            }}
          >
            Peak: {formatHour(peakHour)}
          </span>
        </div>
      </div>

      {/* Bar chart */}
      <div className="flex items-end gap-0.5 h-16">
        {hourlyData.map((hourData, index) => {
          const heightPercent = (hourData.downloads / maxDownloads) * 100;
          const isCurrentHour = hourData.hour === currentHour;
          const isPeakHour = hourData.hour === peakHour;

          return (
            <Tooltip
              key={hourData.hour}
              content={
                <div className="text-xs">
                  <div className="font-medium">{formatHour(hourData.hour)}</div>
                  <div>{hourData.downloads} downloads</div>
                  <div>{formatBytes(hourData.bytesServed)}</div>
                </div>
              }
              position="top"
            >
              <div
                className={`hour-bar flex-1 cursor-pointer ${isCurrentHour ? 'current-hour' : ''} ${isPeakHour ? 'peak-hour' : ''}`}
                style={{
                  height: `${Math.max(heightPercent, 8)}%`,
                  backgroundColor: getBarColor(hourData.hour),
                  opacity: hourData.downloads > 0 ? 1 : 0.3,
                  animationDelay: `${index * 20}ms`,
                }}
              />
            </Tooltip>
          );
        })}
      </div>

      {/* Hour labels */}
      <div className="flex justify-between mt-2 text-[10px]" style={{ color: 'var(--theme-text-muted)' }}>
        <span>12AM</span>
        <span>6AM</span>
        <span>12PM</span>
        <span>6PM</span>
        <span>12AM</span>
      </div>

      {/* Legend */}
      <div className="flex items-center gap-4 mt-3 text-xs" style={{ color: 'var(--theme-text-muted)' }}>
        <div className="flex items-center gap-1">
          <div
            className="w-3 h-3 rounded"
            style={{ backgroundColor: 'var(--theme-primary)' }}
          />
          <span>Now</span>
        </div>
        <div className="flex items-center gap-1">
          <div
            className="w-3 h-3 rounded"
            style={{ backgroundColor: 'var(--theme-warning)' }}
          />
          <span>Peak</span>
        </div>
      </div>
    </div>
  );
});

PeakUsageHours.displayName = 'PeakUsageHours';

export default PeakUsageHours;
