import React, { useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Bar } from 'react-chartjs-2';
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Tooltip,
  Legend,
  type Chart,
  type ChartData,
  type ChartOptions,
  type Plugin
} from 'chart.js';
import { formatBytes, formatPercent } from '@utils/formatters';
import type { ServiceStat } from '@/types';
import { useServiceColors } from './useServiceColors';
import { getThemeColor, useThemeRevision } from './chartTheme';

ChartJS.register(CategoryScale, LinearScale, BarElement, Tooltip, Legend);

declare module 'chart.js' {
  interface TooltipPositionerMap {
    divergingBarEdge: (
      items: readonly { element: { x: number; y: number; base: number } }[]
    ) => { x: number; y: number; xAlign: 'left' | 'right'; yAlign: 'center' } | false;
  }
}

Tooltip.positioners.divergingBarEdge = function (items) {
  if (!items.length) return false;
  const el = items[0].element;
  const isRightSide = el.x >= el.base;
  return {
    x: el.x,
    y: el.y,
    xAlign: isRightSide ? 'left' : 'right',
    yAlign: 'center'
  };
};

interface CompareLineChartProps {
  serviceStats: ServiceStat[];
}

interface TooltipRow {
  background: string;
  border: string;
  text: string;
}

interface TooltipContent {
  title: string;
  rows: TooltipRow[];
}

/**
 * Per-render visual config consumed by the canvas plugin. Reading these from
 * theme CSS vars (via useServiceColors / getThemeColor) keeps every paint on
 * theme — no hardcoded colors, no CSS for the canvas overlay.
 */
interface DivergingBarTheme {
  rowHighlight: string;
  labelColor: string;
  zeroLineColor: string;
}

const TOOLTIP_GAP = 12;

/**
 * Minimum painted bar length (px). Tiny services (wsus, small blizzard/epic)
 * would otherwise render a sub-pixel hairline that cannot be hovered. With
 * intersect:false this also guarantees a real, visible swatch per row.
 */
const MIN_BAR_LENGTH = 6;

// A bar at least this many px wide gets its value painted inline at the end;
// narrower "slivers" stay tooltip-only to avoid clutter (web-research Topic 2 #4).
const INLINE_LABEL_MIN_BAR_PX = 44;
const INLINE_LABEL_GAP = 6;
const VALUE_LABEL_FONT =
  '600 11px "Segoe UI", system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif';

/**
 * Canvas plugin that paints, beneath the bars, a faint full-width highlight
 * across the hovered row's whole band — so hovering anywhere on a service's
 * row (label gutter, either bar track, the value gutter) reads as "this row".
 * Combined with options.interaction.intersect:false this gives a full-row
 * hit-area without any DOM/CSS overlay. The highlight color comes from the
 * theme (--theme-bg-hover), so it tracks light/dark.
 */
function createRowHighlightPlugin(getTheme: () => DivergingBarTheme): Plugin<'bar'> {
  return {
    id: 'divergingRowHighlight',
    beforeDatasetsDraw(chart: Chart<'bar'>) {
      const active = chart.getActiveElements();
      if (!active.length) return;
      const rowIndex = active[0].index;
      const yScale = chart.scales.y;
      const xScale = chart.scales.x;
      if (!yScale || !xScale) return;

      const band = (yScale as unknown as { getPixelForValue: (v: number) => number })
        .getPixelForValue;
      const center = band.call(yScale, rowIndex);
      const step = (yScale.height || 0) / Math.max(1, yScale.ticks.length || 1);
      const halfBand = Math.max(step / 2, 8);

      const { ctx, chartArea } = chart;
      ctx.save();
      ctx.fillStyle = getTheme().rowHighlight;
      ctx.fillRect(
        chartArea.left,
        center - halfBand,
        chartArea.right - chartArea.left,
        halfBand * 2
      );
      ctx.restore();
    }
  };
}

/**
 * Canvas plugin that paints the exact byte value at the end of each readable
 * bar (>= INLINE_LABEL_MIN_BAR_PX). Slivers are skipped (tooltip-only). Hits
 * are right-anchored to the right of their bar end; misses left-anchored to the
 * left of theirs. Numbers reuse formatBytes; the canvas font is fixed-digit so
 * values align (the tabular-figure intent for canvas text).
 */
function createValueLabelPlugin(getTheme: () => DivergingBarTheme): Plugin<'bar'> {
  return {
    id: 'divergingValueLabels',
    afterDatasetsDraw(chart: Chart<'bar'>) {
      const xScale = chart.scales.x;
      if (!xScale) return;
      const zeroX = xScale.getPixelForValue(0);
      const { ctx, chartArea } = chart;

      ctx.save();
      ctx.font = VALUE_LABEL_FONT;
      ctx.fillStyle = getTheme().labelColor;
      ctx.textBaseline = 'middle';

      chart.data.datasets.forEach((_dataset, datasetIndex) => {
        const meta = chart.getDatasetMeta(datasetIndex);
        if (meta.hidden) return;
        meta.data.forEach((element, dataIndex) => {
          const raw = Number(chart.data.datasets[datasetIndex].data[dataIndex] ?? 0);
          if (!raw) return;
          const bar = element as unknown as { x: number; y: number };
          const barLength = Math.abs(bar.x - zeroX);
          if (barLength < INLINE_LABEL_MIN_BAR_PX) return;

          const text = formatBytes(Math.abs(raw));
          const isPositive = raw >= 0;
          ctx.textAlign = isPositive ? 'left' : 'right';
          const textX = isPositive ? bar.x + INLINE_LABEL_GAP : bar.x - INLINE_LABEL_GAP;
          const textWidth = ctx.measureText(text).width;
          // Skip if the label would spill outside the plot area.
          if (isPositive && textX + textWidth > chartArea.right) return;
          if (!isPositive && textX - textWidth < chartArea.left) return;
          ctx.fillText(text, textX, bar.y);
        });
      });
      ctx.restore();
    }
  };
}

const CompareLineChart: React.FC<CompareLineChartProps> = React.memo(({ serviceStats }) => {
  const themeRevision = useThemeRevision();
  const { getCacheHitColor, getCacheMissColor, getBorderColor } = useServiceColors();
  const tooltipElRef = useRef<HTMLDivElement | null>(null);
  const lastDataKeyRef = useRef<string>('');
  const [tooltipContent, setTooltipContent] = useState<TooltipContent>({ title: '', rows: [] });

  // Live theme snapshot the canvas plugins read at paint time. A ref keeps the
  // plugin identity stable across renders while still resolving fresh colors.
  const themeRef = useRef<DivergingBarTheme>({
    rowHighlight: '',
    labelColor: '',
    zeroLineColor: ''
  });
  themeRef.current = useMemo(() => {
    void themeRevision;
    return {
      rowHighlight: getThemeColor('--theme-bg-hover'),
      labelColor: getThemeColor('--theme-chart-text'),
      zeroLineColor: getThemeColor('--theme-border-primary')
    };
  }, [themeRevision]);

  const plugins = useMemo<Plugin<'bar'>[]>(
    () => [
      createRowHighlightPlugin(() => themeRef.current),
      createValueLabelPlugin(() => themeRef.current)
    ],
    []
  );

  const services = useMemo(
    () =>
      [...serviceStats]
        .filter((service) => service.totalBytes > 0)
        .sort((a, b) => b.totalBytes - a.totalBytes)
        .slice(0, 10),
    [serviceStats]
  );

  const labels = useMemo(() => services.map((service) => service.service), [services]);

  const chartData: ChartData<'bar'> = useMemo(() => {
    void themeRevision;
    // Diverging color pair = the dedicated cache-hit / cache-miss theme colors.
    // Solid fills (no pattern fills); hover darkens to the same hue.
    const hitColor = getCacheHitColor() || getThemeColor('--theme-chart-cache-hit');
    const missColor = getCacheMissColor() || getThemeColor('--theme-chart-cache-miss');
    const borderColor = getBorderColor() || getThemeColor('--theme-chart-border');

    return {
      labels,
      datasets: [
        {
          label: 'Cache Hits',
          data: services.map((service) => service.totalCacheHitBytes),
          backgroundColor: hitColor,
          hoverBackgroundColor: hitColor,
          borderColor,
          borderWidth: 1,
          borderRadius: 4,
          borderSkipped: false,
          minBarLength: MIN_BAR_LENGTH,
          barPercentage: 0.92,
          categoryPercentage: 0.86
        },
        {
          label: 'Cache Misses',
          data: services.map((service) => -service.totalCacheMissBytes),
          backgroundColor: missColor,
          hoverBackgroundColor: missColor,
          borderColor,
          borderWidth: 1,
          borderRadius: 4,
          borderSkipped: false,
          minBarLength: MIN_BAR_LENGTH,
          barPercentage: 0.92,
          categoryPercentage: 0.86
        }
      ]
    };
  }, [labels, services, themeRevision, getCacheHitColor, getCacheMissColor, getBorderColor]);

  const options: ChartOptions<'bar'> = useMemo(() => {
    void themeRevision;
    const textColor = getThemeColor('--theme-chart-text');
    const mutedColor = getThemeColor('--theme-text-muted');
    const zeroLineColor = getThemeColor('--theme-border-primary');
    const maxMagnitude = services.reduce(
      (max, service) => Math.max(max, service.totalCacheHitBytes, service.totalCacheMissBytes),
      0
    );

    return {
      responsive: true,
      maintainAspectRatio: false,
      indexAxis: 'y',
      // Explicit entry animation: bars grow in from the zero baseline. (chart.js
      // default grow is flattened by minBarLength + the per-frame canvas plugins,
      // so set it here to restore a clear, smooth animate-in on mount/tab-switch.)
      animation: {
        duration: 650,
        easing: 'easeOutQuart'
      },
      // Full-row hit-area: snap to the whole category row (mode 'y') and never
      // require the pointer to land on the painted rect (intersect:false), so
      // even a min-length sliver is hoverable anywhere along its row band.
      interaction: {
        mode: 'y',
        intersect: false
      },
      hover: {
        mode: 'y',
        intersect: false
      },
      layout: {
        padding: {
          top: 4,
          right: 16,
          bottom: 4,
          left: 4
        }
      },
      scales: {
        x: {
          stacked: false,
          suggestedMin: -maxMagnitude,
          suggestedMax: maxMagnitude,
          // No vertical gridlines except a single 1px zero baseline; both halves
          // grow from it so rows compare across the center (web-research #3).
          grid: {
            color: zeroLineColor,
            lineWidth: (ctx) => (ctx.tick.value === 0 ? 1 : 0),
            drawTicks: false
          },
          border: {
            display: false
          },
          ticks: {
            color: mutedColor,
            maxTicksLimit: 5,
            callback: (value) => formatBytes(Math.abs(Number(value)))
          }
        },
        y: {
          stacked: false,
          grid: {
            display: false
          },
          border: {
            display: false
          },
          ticks: {
            color: mutedColor,
            autoSkip: false,
            callback: (_value, index) => {
              const label = labels[index] ?? '';
              return label.length > 14 ? `${label.slice(0, 14)}...` : label;
            }
          }
        }
      },
      plugins: {
        legend: {
          position: 'top',
          align: 'end',
          labels: {
            color: textColor,
            boxWidth: 10,
            boxHeight: 10,
            usePointStyle: true,
            padding: 14
          }
        },
        tooltip: {
          enabled: false,
          position: 'divergingBarEdge',
          animation: {
            duration: 280,
            easing: 'easeOutQuart'
          },
          external: ({ chart, tooltip }) => {
            const el = tooltipElRef.current;
            if (!el) return;

            if (tooltip.opacity === 0) {
              el.classList.remove('is-visible');
              lastDataKeyRef.current = '';
              return;
            }

            const canvasRect = chart.canvas.getBoundingClientRect();
            const anchorX = canvasRect.left + tooltip.caretX;
            const anchorY = canvasRect.top + tooltip.caretY;
            const isPositive = (tooltip.dataPoints?.[0]?.parsed?.x ?? 0) >= 0;

            // Measure the tooltip so we can keep it inside the viewport on every edge.
            // Bars near the right edge would otherwise push the tooltip off-screen on
            // the right; the same applies on the left for cache-miss bars.
            const tooltipWidth = el.offsetWidth || 0;
            const tooltipHeight = el.offsetHeight || 0;
            const viewportWidth = window.innerWidth;
            const viewportHeight = window.innerHeight;
            const SAFE_MARGIN = 12;

            // Prefer the side that matches the bar direction, but flip if it would
            // overflow the viewport. If both sides overflow (very narrow viewport),
            // keep whichever leaves more room.
            const spaceRight = viewportWidth - anchorX - TOOLTIP_GAP - SAFE_MARGIN;
            const spaceLeft = anchorX - TOOLTIP_GAP - SAFE_MARGIN;
            let placeOnRight = isPositive;
            if (placeOnRight && spaceRight < tooltipWidth) {
              placeOnRight = spaceLeft >= tooltipWidth || spaceLeft > spaceRight;
            } else if (!placeOnRight && spaceLeft < tooltipWidth) {
              placeOnRight = spaceRight > spaceLeft;
            }

            // Vertical clamp so the tooltip never spills above or below the viewport.
            const halfHeight = tooltipHeight / 2;
            const clampedY = Math.max(
              SAFE_MARGIN + halfHeight,
              Math.min(anchorY, viewportHeight - SAFE_MARGIN - halfHeight)
            );

            el.style.transform = placeOnRight
              ? `translate3d(${anchorX + TOOLTIP_GAP}px, ${clampedY}px, 0) translateY(-50%)`
              : `translate3d(${anchorX - TOOLTIP_GAP}px, ${clampedY}px, 0) translate(-100%, -50%)`;
            el.classList.toggle('compare-chart-tooltip--arrow-left', placeOnRight);
            el.classList.toggle('compare-chart-tooltip--arrow-right', !placeOnRight);
            el.classList.add('is-visible');

            const dp = tooltip.dataPoints?.[0];
            const key = dp ? `${dp.datasetIndex}-${dp.dataIndex}` : '';
            if (key === lastDataKeyRef.current) return;
            lastDataKeyRef.current = key;

            const titleText = (tooltip.title ?? []).join(' ');
            const rows: TooltipRow[] = [];
            tooltip.body.forEach((entry, i) => {
              const colors = tooltip.labelColors[i];
              entry.lines.forEach((line: string) => {
                rows.push({
                  background: String(colors?.backgroundColor ?? 'transparent'),
                  border: String(colors?.borderColor ?? 'transparent'),
                  text: line
                });
              });
            });
            setTooltipContent({ title: titleText, rows });
          },
          callbacks: {
            title: (items) => {
              const item = items[0];
              if (!item) return '';
              return services[item.dataIndex]?.service ?? item.label ?? '';
            },
            label: (context) => {
              const service = services[context.dataIndex];
              if (!service) return '';
              const hitRate = service.totalBytes
                ? (service.totalCacheHitBytes / service.totalBytes) * 100
                : 0;
              const value = Math.abs(Number(context.raw));

              if (context.dataset.label === 'Cache Misses') {
                return `${context.dataset.label}: ${formatBytes(value)} (misses can be normal for first-time downloads)`;
              }

              return `${context.dataset.label}: ${formatBytes(value)} (${formatPercent(hitRate)} hit rate)`;
            }
          }
        }
      }
    };
  }, [labels, services, themeRevision]);

  return (
    <>
      <div className="compare-line-chart">
        <Bar data={chartData} options={options} plugins={plugins} />
      </div>
      {createPortal(
        <div ref={tooltipElRef} className="themed-card tooltip-edge compare-chart-tooltip">
          {tooltipContent.title && (
            <div className="compare-chart-tooltip__title">{tooltipContent.title}</div>
          )}
          {tooltipContent.rows.map((row, i) => (
            <div key={i} className="compare-chart-tooltip__row">
              <span
                className="compare-chart-tooltip__swatch"
                style={{ backgroundColor: row.background, borderColor: row.border }}
              />
              <span className="compare-chart-tooltip__text">{row.text}</span>
            </div>
          ))}
        </div>,
        document.body
      )}
    </>
  );
});

CompareLineChart.displayName = 'CompareLineChart';

export default CompareLineChart;
