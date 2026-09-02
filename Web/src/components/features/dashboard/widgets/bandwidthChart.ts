import { useCallback, useState } from 'react';
import type { ChartOptions, Scale } from 'chart.js';
import { formatTimestamp, type ReaderClock } from '@utils/dateTimeFormat';
import { formatBytes } from '@utils/formatters';
import { getThemeColor } from '../ServiceAnalyticsChart/chartTheme';

interface HiddenSeries {
  hiddenSeries: ReadonlySet<number>;
  toggleSeries: (index: number) => void;
  seriesKey: string;
}

/**
 * The only record of which series are hidden: the dataset objects read `hidden` from it, so a
 * canvas that unmounts and comes back redraws with the series the legend says are hidden.
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

  // React key for the canvas. A hidden dataset keeps tracking the axis while it is out of view, so
  // reusing the canvas animates a restored series down from off the top of the plot as the axis
  // grows back. A fresh canvas starts every series at the zero line and grows up instead.
  const seriesKey = Array.from(hiddenSeries).sort().join(',');

  return { hiddenSeries, toggleSeries, seriesKey };
}

// Below this plot width, Chart.js's forced first/last tick ("includeBounds") can space the two
// boundary labels closer together than either label is wide, so they overlap. Above it, the same
// two labels have room and turning includeBounds off would only change which ticks autoSkip picks.
const NARROW_PLOT_WIDTH = 400;

interface NarrowScaleTicks {
  includeBounds?: boolean;
  autoSkipPadding?: number;
  callback?: (value: number | string, index: number) => string;
}

// A narrow plot can't fit the full label ("Aug 27, 2026, 10:00 AM") twice without the boundary
// ticks colliding, but it has room for several short ones ("Aug 27"). `narrowLabels` is the short
// form of the same points, indexed the same as the chart's own category ticks.
export function lineChartScales(narrowLabels: string[]): ChartOptions<'line'>['scales'] {
  // Chart chrome reads the chart token family, the one the theme editor exposes for
  // charts, so an author retuning it moves every canvas. `border` is off on both
  // scales because chart.js otherwise draws an axis rule in its own library default,
  // which no theme can reach.
  const textColor = getThemeColor('--theme-chart-text');
  return {
    x: {
      ticks: {
        color: textColor,
        maxRotation: 0,
        autoSkip: true,
        maxTicksLimit: 8
      },
      beforeBuildTicks: (scale: Scale) => {
        const ticks = (scale.options as unknown as { ticks: NarrowScaleTicks }).ticks;
        const narrow = scale.chart.width < NARROW_PLOT_WIDTH;
        ticks.includeBounds = !narrow;
        ticks.autoSkipPadding = narrow ? 20 : 0;
        // A category scale's own tick value is the point's index, not its label, so restoring the
        // library default here (rather than deleting our override) still needs an explicit lookup.
        const categoryScale = scale as unknown as { getLabelForValue(value: number): string };
        ticks.callback = narrow
          ? (_value, index) => narrowLabels[index] ?? ''
          : (value) => categoryScale.getLabelForValue(Number(value));
      },
      grid: { display: false },
      border: { display: false }
    },
    y: {
      beginAtZero: true,
      grace: '10%',
      ticks: {
        color: textColor,
        callback: (value) => formatBytes(typeof value === 'number' ? value : Number(value))
      },
      grid: { color: getThemeColor('--theme-chart-grid') },
      border: { display: false }
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
