import { useCallback, useState } from 'react';
import type { ChartOptions } from 'chart.js';
import { formatTimestamp, type ReaderClock } from '@utils/dateTimeFormat';
import { formatBytes } from '@utils/formatters';
import { getThemeColor } from '../ServiceAnalyticsChart/chartTheme';

interface HiddenSeries {
  hiddenSeries: ReadonlySet<number>;
  toggleSeries: (index: number) => void;
}

/**
 * The only record of which series are hidden: the dataset objects read `hidden` from it, so a
 * canvas that unmounts and comes back redraws with the series the legend says are hidden. [6]
 */
export function useHiddenSeries(): HiddenSeries {
  const [hiddenSeries, setHiddenSeries] = useState<ReadonlySet<number>>(() => new Set());

  const toggleSeries = useCallback((index: number) => {
    setHiddenSeries((current) => {
      const next = new Set(current);
      if (next.has(index)) {
        next.delete(index);
      } else {
        next.add(index);
      }
      return next;
    });
  }, []);

  return { hiddenSeries, toggleSeries };
}

export function lineChartScales(): ChartOptions<'line'>['scales'] {
  return {
    x: {
      ticks: {
        color: getThemeColor('--theme-text-muted'),
        maxRotation: 0,
        autoSkip: true,
        maxTicksLimit: 8
      },
      grid: { display: false }
    },
    y: {
      beginAtZero: true,
      grace: '10%',
      ticks: {
        color: getThemeColor('--theme-text-muted'),
        callback: (value) => formatBytes(typeof value === 'number' ? value : Number(value))
      },
      grid: { color: getThemeColor('--theme-border-secondary') }
    }
  };
}

export function bandwidthTickLabel(
  startUnix: number,
  bucketMinutes: number,
  clock: ReaderClock
): string {
  const style = bucketMinutes >= 1440 ? 'dateShort' : bucketMinutes >= 180 ? 'stamp' : 'timeOnly';
  return formatTimestamp(new Date(startUnix * 1000), {
    ...clock,
    forceYear: false,
    style
  });
}

export function hasBandwidthPoints(saved: number[], served: number[]): boolean {
  return saved.some((value) => value > 0) || served.some((value) => value > 0);
}
