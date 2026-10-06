import { useCallback, useState } from 'react';
import type { Scale, ScaleOptions } from 'chart.js';
import { formatTimestamp, type ReaderClock } from '@utils/dateTimeFormat';
import {
  byteAxisStep,
  formatAxisBytes,
  getChartFontFamily,
  getThemeColor
} from '../ServiceAnalyticsChart/chartTheme';

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
  stepSize?: number;
  callback?: (value: number | string, index: number) => string;
}

// A narrow plot can't fit the full label ("Aug 27, 2026, 10:00 AM") twice without the boundary
// ticks colliding, but it has room for several short ones ("Aug 27"). `narrowLabels` is the short
// form of the same points as `labels`, both indexed by point. Both axes are typed as linear
// options so a caller can turn x into a linear axis; every field set here is also valid on the
// category x axis the compare chart keeps.
export function lineChartScales(
  labels: string[],
  narrowLabels: string[]
): { x: ScaleOptions<'linear'>; y: ScaleOptions<'linear'> } {
  // Chart chrome reads the chart token family, the one the theme editor exposes for
  // charts, so an author retuning it moves every canvas. `border` is off on both
  // scales because chart.js otherwise draws an axis rule in its own library default,
  // which no theme can reach.
  const textColor = getThemeColor('--theme-chart-text');
  const font = { family: getChartFontFamily() };
  return {
    x: {
      ticks: {
        color: textColor,
        font,
        maxRotation: 0,
        autoSkip: true,
        maxTicksLimit: 8
      },
      beforeBuildTicks: (scale: Scale) => {
        const ticks = (scale.options as unknown as { ticks: NarrowScaleTicks }).ticks;
        const narrow = scale.chart.width < NARROW_PLOT_WIDTH;
        // A linear axis's bounds fall between buckets and have no label, so a tick forced onto
        // them only crowds out the dated ticks beside it, which then blink in and out as the
        // bounds move during a zoom or drag.
        ticks.includeBounds = !narrow && scale.type !== 'linear';
        ticks.autoSkipPadding = narrow ? 20 : 0;
        if (scale.type === 'linear') {
          // Power-of-two steps nest: zooming in only adds dates between the ones shown and
          // zooming out only drops every other one, so no date blinks out while still in view.
          // The step fits about seven gaps, four on a narrow plot, and never splits a bucket.
          const gaps = narrow ? 4 : 7;
          ticks.stepSize = Math.max(1, 2 ** Math.ceil(Math.log2((scale.max - scale.min) / gaps)));
        }
        // A tick's value is its point's index on both a category and a linear axis. Look up by
        // that value, not the tick's position: a zoomed axis starts its ticks at the first visible
        // point, and a linear axis can put a tick between two points, which then has no label.
        const pointLabels = narrow ? narrowLabels : labels;
        ticks.callback = (value) => pointLabels[Number(value)] ?? '';
      },
      grid: { display: false },
      border: { display: false }
    },
    y: {
      beginAtZero: true,
      grace: '10%',
      // Four gaps keep the value axis quiet behind the lines.
      beforeBuildTicks: (scale: Scale) => {
        const ticks = (scale.options as unknown as { ticks: NarrowScaleTicks }).ticks;
        ticks.stepSize = byteAxisStep(scale.max, 4);
      },
      ticks: {
        color: textColor,
        font,
        callback: (value) => formatAxisBytes(Number(value))
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
