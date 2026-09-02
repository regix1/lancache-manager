import { formatBytes } from '@utils/formatters';
import { clampToViewport } from '@utils/viewportClamp';
import type { Chart, ChartOptions, TooltipItem, TooltipModel } from 'chart.js';

const VIEWPORT_GUTTER = 12;

type LineTooltip = NonNullable<NonNullable<ChartOptions<'line'>['plugins']>['tooltip']>;

interface LineChartTooltipConfig {
  swatchClass: (datasetIndex: number) => string;
  title?: (items: TooltipItem<'line'>[]) => string | string[] | void;
}

const tooltipNodes = new WeakMap<Document, HTMLDivElement>();

function tooltipRoot(): HTMLDivElement {
  const existing = tooltipNodes.get(document);
  if (existing?.isConnected) {
    return existing;
  }

  const node = document.createElement('div');
  node.className = 'themed-card tooltip-edge line-trend-tooltip';
  document.body.appendChild(node);
  tooltipNodes.set(document, node);
  // The position is read from the canvas rect at hover time and Chart.js does not re-run the
  // external callback on scroll or resize, so the tooltip would sit at the old spot while the
  // canvas moves under it. Capture phase catches a scroll on any ancestor, not just the page.
  window.addEventListener('scroll', hideLineChartTooltip, true);
  window.addEventListener('resize', hideLineChartTooltip);
  // Touch has no hover-out to hide the tooltip on, so a tap elsewhere on the page must close it.
  // A tap on a canvas is left alone so Chart.js's own pointer handling can show/move the tooltip.
  document.addEventListener(
    'pointerdown',
    (event) => {
      if (!(event.target instanceof Element) || !event.target.closest('canvas')) {
        hideLineChartTooltip();
      }
    },
    true
  );
  return node;
}

/**
 * Chart.js only runs the external callback on pointer events, so a chart torn
 * down while the tooltip is up leaves it on screen: switching the chart tab from
 * the keyboard, or a refresh that empties the series, both drop the canvas
 * without a pointer ever leaving it. Every widget using this tooltip calls it on
 * unmount.
 */
export function hideLineChartTooltip(): void {
  const node = tooltipNodes.get(document);
  if (node) {
    node.hidden = true;
  }
}

function renderLineChartTooltip(
  ctx: { chart: Chart; tooltip: TooltipModel<'line'> },
  config: LineChartTooltipConfig
): void {
  const node = tooltipRoot();
  const { chart, tooltip } = ctx;

  if (tooltip.opacity === 0 || !tooltip.dataPoints.length) {
    node.hidden = true;
    return;
  }

  node.hidden = false;
  node.replaceChildren();

  if (tooltip.title.length) {
    const title = document.createElement('div');
    title.className = 'line-trend-tooltip-title';
    title.textContent = tooltip.title.join(' ');
    node.appendChild(title);
  }

  tooltip.dataPoints.forEach((point, index) => {
    const row = document.createElement('div');
    row.className = 'line-trend-legend-item';

    const swatch = document.createElement('span');
    swatch.className = `line-trend-legend-swatch ${config.swatchClass(point.datasetIndex)}`;

    const label = document.createElement('span');
    label.className = 'line-trend-legend-label';
    label.textContent = tooltip.body[index]?.lines.join(' ') ?? '';

    row.append(swatch, label);
    node.appendChild(row);
  });

  const canvasRect = chart.canvas.getBoundingClientRect();
  const left = clampToViewport(
    canvasRect.left + tooltip.caretX,
    node.offsetWidth,
    window.innerWidth,
    VIEWPORT_GUTTER
  );
  const top = clampToViewport(
    canvasRect.top + tooltip.caretY,
    node.offsetHeight,
    window.innerHeight,
    VIEWPORT_GUTTER
  );
  node.style.setProperty('--line-trend-tooltip-x', `${left}px`);
  node.style.setProperty('--line-trend-tooltip-y', `${top}px`);
}

export function lineChartTooltip(config: LineChartTooltipConfig): LineTooltip {
  const callbacks: NonNullable<LineTooltip['callbacks']> = {
    label(item) {
      const y = item.parsed.y;
      if (typeof y !== 'number') {
        return '';
      }
      return `${item.dataset.label}: ${formatBytes(y)}`;
    }
  };

  if (config.title) {
    callbacks.title = config.title;
  }

  return {
    enabled: false,
    external: (ctx) => renderLineChartTooltip(ctx, config),
    callbacks
  };
}
