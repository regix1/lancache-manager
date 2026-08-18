import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const formattersUrl = `data:text/javascript;base64,${Buffer.from(
  'export function formatBytes(n) { return `${n} B`; }\n'
).toString('base64')}`;
const viewportClampUrl = await compileToUrl('../src/utils/viewportClamp.ts');
const { lineChartTooltip, hideLineChartTooltip } = await import(
  await compileToUrl('../src/components/features/dashboard/widgets/lineChartTooltip.ts', {
    '@utils/formatters': formattersUrl,
    '@utils/viewportClamp': viewportClampUrl
  })
);

function createEl() {
  const el = {
    className: '',
    hidden: false,
    children: [],
    textContent: '',
    isConnected: false,
    style: {
      props: {},
      setProperty(name, value) {
        this.props[name] = value;
      }
    },
    offsetWidth: 160,
    offsetHeight: 48,
    appendChild(child) {
      this.children.push(child);
      child.isConnected = this.isConnected || this === globalThis.document.body;
      return child;
    },
    append(...nodes) {
      for (const node of nodes) {
        this.appendChild(node);
      }
    },
    replaceChildren(...nodes) {
      this.children = [];
      for (const node of nodes) {
        this.appendChild(node);
      }
    }
  };
  return el;
}

function installDom() {
  const body = createEl();
  body.isConnected = true;
  globalThis.document = {
    body,
    createElement() {
      return createEl();
    }
  };
  const listeners = [];
  globalThis.window = {
    innerWidth: 400,
    innerHeight: 300,
    listeners,
    addEventListener(type, handler, capture) {
      listeners.push({ type, handler, capture });
    },
    fire(type) {
      for (const listener of listeners) {
        if (listener.type === type) {
          listener.handler();
        }
      }
    }
  };
}

function tooltipModel(overrides = {}) {
  return {
    opacity: 1,
    caretX: 80,
    caretY: 20,
    title: ['10:00'],
    body: [{ lines: ['Total served: 1024 B'] }, { lines: ['Cache hits: 512 B'] }],
    dataPoints: [{ datasetIndex: 0 }, { datasetIndex: 1 }],
    ...overrides
  };
}

function chartHost() {
  return {
    canvas: {
      getBoundingClientRect() {
        return { left: 350, top: 40, width: 200, height: 160 };
      }
    }
  };
}

const swatchClass = (datasetIndex) =>
  datasetIndex === 1 ? 'line-trend-swatch-success' : 'line-trend-swatch-primary';

function renderTooltip(ctx, config) {
  lineChartTooltip(config).external(ctx);
}

describe('line chart HTML tooltip', { concurrency: false }, () => {
  test('disables canvas tooltip paint and omits pointStyle keys', () => {
    const options = lineChartTooltip({ swatchClass });

    assert.equal(options.enabled, false);
    assert.equal(typeof options.external, 'function');
    assert.equal('usePointStyle' in options, false);
    assert.equal('pointStyle' in options, false);
    assert.equal(options.usePointStyle, undefined);
    assert.equal(options.callbacks.labelColor, undefined);
  });

  test('formats series labels with byte strings and keeps a title override', () => {
    const options = lineChartTooltip({
      swatchClass,
      title: () => 'Elapsed 5m'
    });

    assert.equal(
      options.callbacks.label({ parsed: { y: 2048 }, dataset: { label: 'Total served' } }),
      'Total served: 2048 B'
    );
    assert.equal(
      options.callbacks.label({ parsed: { y: null }, dataset: { label: 'Total served' } }),
      ''
    );
    assert.equal(options.callbacks.title(), 'Elapsed 5m');
  });

  test('builds rows with line-trend legend classes', () => {
    installDom();
    renderTooltip({ chart: chartHost(), tooltip: tooltipModel() }, { swatchClass });

    const root = globalThis.document.body.children[0];
    assert.match(root.className, /themed-card/);
    assert.match(root.className, /tooltip-edge/);
    assert.match(root.className, /line-trend-tooltip/);
    assert.equal(root.className.includes('compare-chart-tooltip'), false);
    assert.equal(root.hidden, false);

    const title = root.children[0];
    assert.equal(title.className, 'line-trend-tooltip-title');
    assert.equal(title.textContent, '10:00');

    const firstRow = root.children[1];
    const secondRow = root.children[2];
    assert.equal(firstRow.className, 'line-trend-legend-item');
    assert.equal(secondRow.className, 'line-trend-legend-item');
    assert.match(firstRow.children[0].className, /line-trend-legend-swatch/);
    assert.match(firstRow.children[0].className, /line-trend-swatch-primary/);
    assert.equal(firstRow.children[1].className, 'line-trend-legend-label');
    assert.equal(firstRow.children[1].textContent, 'Total served: 1024 B');
    assert.match(secondRow.children[0].className, /line-trend-swatch-success/);
  });

  test('hides the tooltip when opacity is zero or there are no points', () => {
    installDom();
    const ctx = { chart: chartHost(), tooltip: tooltipModel() };
    renderTooltip(ctx, { swatchClass });
    renderTooltip({ chart: ctx.chart, tooltip: tooltipModel({ opacity: 0 }) }, { swatchClass });
    assert.equal(globalThis.document.body.children[0].hidden, true);

    renderTooltip(ctx, { swatchClass });
    renderTooltip({ chart: ctx.chart, tooltip: tooltipModel({ dataPoints: [] }) }, { swatchClass });
    assert.equal(globalThis.document.body.children[0].hidden, true);
  });

  test('hides the tooltip when a chart goes away without a pointer leaving it', () => {
    installDom();
    renderTooltip({ chart: chartHost(), tooltip: tooltipModel() }, { swatchClass });
    assert.equal(globalThis.document.body.children[0].hidden, false);

    hideLineChartTooltip();
    assert.equal(globalThis.document.body.children[0].hidden, true);
  });

  test('hides the tooltip when the page scrolls or the window resizes', () => {
    installDom();
    renderTooltip({ chart: chartHost(), tooltip: tooltipModel() }, { swatchClass });
    const root = globalThis.document.body.children[0];

    const scroll = globalThis.window.listeners.find((entry) => entry.type === 'scroll');
    assert.equal(scroll.capture, true);

    globalThis.window.fire('scroll');
    assert.equal(root.hidden, true);

    renderTooltip({ chart: chartHost(), tooltip: tooltipModel() }, { swatchClass });
    assert.equal(root.hidden, false);
    globalThis.window.fire('resize');
    assert.equal(root.hidden, true);
  });

  test('clamps the tooltip origin inside the viewport', () => {
    installDom();
    renderTooltip({ chart: chartHost(), tooltip: tooltipModel() }, { swatchClass });
    const root = globalThis.document.body.children[0];
    assert.equal(root.style.props['--line-trend-tooltip-x'], '228px');
    assert.equal(root.style.props['--line-trend-tooltip-y'], '60px');
  });
});
