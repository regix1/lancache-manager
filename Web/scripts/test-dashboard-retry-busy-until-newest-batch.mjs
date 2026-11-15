import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { compileToUrl, make, moduleUrl, parseSource } from './transpile-module.mjs';

/**
 * When the dashboard's services section fails, the Service analytics card shows an error block
 * with Retry. A forced batch (a reconnect, a time range change, a finished scan) can start while
 * the batch Retry asked for is out, and replace it. The newer batch is the one that will replace
 * the box, so Retry shows busy until that batch lands, in either order of the click and the
 * forced batch.
 *
 * The shipped dashboard data provider, the shipped `useStats`, the shipped card and the shipped
 * error block run here on small hook stores; every other component is a stub, and the test reads
 * the props the Retry button is drawn with, so it does not care which of them keeps the busy state.
 */

const DASHBOARD_STATE = 'src/contexts/DashboardDataContext/index.tsx';
const CHART = 'src/components/features/dashboard/ServiceAnalyticsChart/ServiceAnalyticsChart.tsx';
const ERROR_BLOCK = 'src/components/ui/ErrorBlock.tsx';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name `path` imports, so a stub stands in for any the test does not bind itself. */
const importedNames = (path) =>
  parseSource(path)
    .statements.filter(typescript.isImportDeclaration)
    .flatMap((declaration) => {
      const clause = declaration.importClause;
      if (!clause || clause.isTypeOnly) return [];
      const names = clause.name ? [clause.name.text] : [];
      const bindings = clause.namedBindings;
      if (bindings && typescript.isNamedImports(bindings))
        names.push(
          ...bindings.elements
            .filter((element) => !element.isTypeOnly)
            .map((element) => element.name.text)
        );
      return names;
    });

/**
 * One component's hooks; `draw` starts a render. A memo is rebuilt on every render, so a value
 * built from state is never older than that state. No effect runs: the test starts every read.
 */
const hookStore = () => {
  const slots = [];
  let cursor = 0;
  const hooks = {
    useState: (initial) => {
      const index = cursor++;
      const slot = (slots[index] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => {
      const index = cursor++;
      return (slots[index] ??= { current: initial });
    },
    useMemo: (build) => {
      cursor++;
      return build();
    },
    useCallback: (callback) => {
      cursor++;
      return callback;
    },
    useEffect: () => {
      cursor++;
    },
    useLayoutEffect: () => {
      cursor++;
    }
  };
  const draw = () => {
    cursor = 0;
  };
  return { hooks, draw };
};

const Button = stub('Button');

const turn = () => new Promise((resolve) => setImmediate(resolve));

/** A batch whose services sub-query failed and every other section answered. */
const SERVICES_FAILED = {
  cache: { totalCacheSize: 1, usedCacheSize: 0 },
  clients: [],
  services: null,
  dashboard: {},
  downloadTotals: {},
  filteredDownloadTotals: {},
  serviceOptions: [],
  clientOptions: [],
  recentDownloads: { groups: [], rows: [] },
  detection: null,
  sparklines: {},
  hourlyActivity: {},
  cacheSnapshot: {}
};

// The selectors read the provider's value from here, as `useContext` would.
const contextBox = moduleUrl(`
  export const box = { value: null };
  export const createContextHook = () => () => box.value;
`);
const { box } = await import(contextBox);
const { useStats, useGameDetection } = await import(
  await compileToUrl('../src/contexts/DashboardDataContext/hooks.ts', {
    react: moduleUrl('export const useMemo = (build) => build();'),
    '../createContextHook': contextBox,
    './types': moduleUrl('export const DashboardDataContext = {};')
  })
);
const batchModule = await import(
  await compileToUrl('../src/contexts/DashboardDataContext/applyBatchResponse.ts')
);
const chartConstants = await import(
  await compileToUrl('../src/components/features/dashboard/ServiceAnalyticsChart/constants.ts')
);

/** The provider and the Service analytics card; batches are answered when the test chooses. */
const mountDashboard = () => {
  const out = [];
  const getDashboardBatch = (signal) =>
    new Promise((resolve, reject) => {
      const read = { answer: resolve, aborted: false };
      signal.addEventListener('abort', () => {
        read.aborted = true;
        reject(Object.assign(new Error('aborted'), { name: 'AbortError' }));
      });
      out.push(read);
    });
  const newest = () => out.filter((read) => !read.aborted).at(-1);

  let reconnect = () => undefined;
  const stateStore = hookStore();
  const DashboardState = make(DASHBOARD_STATE, 'DashboardDataProvider', {
    ...Object.fromEntries(importedNames(DASHBOARD_STATE).map((name) => [name, stub(name)])),
    React: { ...React, ...stateStore.hooks },
    ...stateStore.hooks,
    i18n: { t: (key) => key },
    ApiService: { getDashboardBatch, getEvents: async () => [] },
    ApiError: class ApiError extends Error {},
    getErrorMessage: (error) => error.message,
    isAbortError: (error) => error?.name === 'AbortError',
    ...batchModule,
    useTimeFilter: () => ({
      getTimeRangeParams: () => ({ startTime: undefined, endTime: undefined }),
      timeRange: 'live',
      customStartDate: null,
      customEndDate: null,
      selectedEventIds: [],
      setSelectedEventIds: () => undefined,
      setTimeRange: () => undefined
    }),
    useRefreshRate: () => ({ getRefreshInterval: () => 1000 }),
    useRefreshThrottle: () => () => undefined,
    useSignalR: () => ({ isConnected: true, on: () => undefined, off: () => undefined }),
    useAuth: () => ({ hasSession: true, isLoading: false }),
    useConnectionLost: () => false,
    useReconnectRefetch: (_isConnected, onReconnect) => {
      reconnect = onReconnect;
    },
    useRepairEnd: () => undefined,
    useTimezone: () => undefined,
    getEffectiveTimezone: () => 'UTC',
    DashboardDataContext: { Provider: stub('DashboardDataProvider') },
    DownloadFilterFetchContext: { Provider: stub('DownloadFilterFetchProvider') }
  });

  const blockStore = hookStore();
  const ErrorBlock = make(ERROR_BLOCK, 'ErrorBlock', {
    ...Object.fromEntries(importedNames(ERROR_BLOCK).map((name) => [name, stub(name)])),
    React: { ...React, ...blockStore.hooks },
    ...blockStore.hooks,
    useConnectionLost: () => false,
    Button
  });
  const chartStore = hookStore();
  const ServiceAnalyticsChart = make(CHART, 'ServiceAnalyticsChart', {
    ...Object.fromEntries(importedNames(CHART).map((name) => [name, stub(name)])),
    React: { ...React, ...chartStore.hooks },
    ...chartStore.hooks,
    useTranslation: () => ({ t: (key) => key }),
    useStats,
    useGameDetection,
    getChartGames: () => [],
    buildGamesOnDiskDisplayStats: () => null,
    isActiveGame: () => true,
    formatServiceLabel: (key) => key,
    useChartData: () => ({ isEmpty: true, datasets: [], labels: [], total: 0 }),
    getInsightCards: () => [],
    getLegendColorClass: () => '',
    formatBytes: (bytes) => `${bytes} B`,
    ...chartConstants,
    APP_EVENTS: { NAVIGATE_TO_TAB: 'navigate-to-tab' },
    ErrorBlock,
    Button
  });

  /** Draws the provider, then the card under it, and returns the card's Retry buttons. */
  const retryButtons = () => {
    stateStore.draw();
    box.value = DashboardState({ children: null }).props.value;
    chartStore.draw();
    const found = [];
    const walk = (node) => {
      if (Array.isArray(node)) {
        node.forEach(walk);
        return;
      }
      if (!React.isValidElement(node)) return;
      if (node.type === ErrorBlock) {
        blockStore.draw();
        walk(ErrorBlock(node.props));
        return;
      }
      if (node.type === Button && node.props.children === 'common.retry') found.push(node.props);
      Object.values(node.props).forEach(walk);
    };
    walk(ServiceAnalyticsChart.type({ serviceStats: [], loading: false }));
    return found;
  };
  return { retryButtons, newest, reconnect: () => reconnect() };
};

for (const order of ['the click, then the forced batch', 'the forced batch, then the click']) {
  test(`a Service analytics Retry shows busy until the newest batch lands when ${order} come in that order`, async () => {
    const originalWarn = console.warn;
    console.warn = () => undefined;
    try {
      const dashboard = mountDashboard();
      dashboard.retryButtons();
      void box.value.refreshData(true);
      await turn();
      dashboard.newest().answer(SERVICES_FAILED);
      await turn();
      await turn();

      const [failed] = dashboard.retryButtons();
      assert.ok(failed, 'a failed services section draws no Retry button on Service analytics');
      assert.ok(!failed.loading, 'Retry shows busy before it was clicked');

      const steps = {
        click: () => dashboard.retryButtons()[0].onClick(),
        forced: () => dashboard.reconnect()
      };
      const sequence =
        order === 'the click, then the forced batch' ? ['click', 'forced'] : ['forced', 'click'];
      for (const step of sequence) {
        steps[step]();
        await turn();
      }
      await turn();

      const [running] = dashboard.retryButtons();
      assert.ok(running, 'clicking Retry removes the Retry button while a batch is out');
      assert.ok(dashboard.newest(), 'no batch is out after the click and the forced batch');
      assert.equal(
        running.loading,
        true,
        `after ${order}, the Retry button is drawn with loading ${String(running.loading)} while the newest batch, the one that will replace the box, is still out`
      );

      dashboard.newest().answer(SERVICES_FAILED);
      await turn();
      await turn();
      await turn();
      const [again] = dashboard.retryButtons();
      assert.ok(again, 'a batch that failed the services section again draws no Retry button');
      assert.ok(!again.loading, 'Retry still shows busy after the newest batch landed');
    } finally {
      console.warn = originalWarn;
    }
  });
}
