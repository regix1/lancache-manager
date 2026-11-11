import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

/**
 * Status Check opened while nothing else is happening on the page. Its mount read asks for the
 * status and the domain list together and applies the status only once both have answered. The
 * status answers at once; the domain list can take up to 30 s when it is more than a day old,
 * because the server fetches it from GitHub first. A newer state can land in that window: the
 * refreshed list's CacheDomainsRefreshed event, or a sweep another browser ran. The page has to end
 * on the newer state, not on the status read that answered before it.
 *
 * The mount read, the domain reload and the SignalR handlers are lifted from the shipped file and
 * share one set of refs. Each case runs every order of the three steps it names, each step held
 * until the test releases it.
 */

const SECTION = 'src/components/features/management/status-check/StatusCheckSection.tsx';

/** A promise with its resolve function, for a reply the test answers when it chooses. */
const deferred = () => {
  let resolve;
  const promise = new Promise((done) => {
    resolve = done;
  });
  return { promise, resolve };
};

/** Lets every reply already released run its continuation. */
const flush = async () => {
  for (let turn = 0; turn < 10; turn += 1) await new Promise((done) => setImmediate(done));
};

/** Every order of the given steps. */
const orders = (steps) =>
  steps.length <= 1
    ? [steps]
    : steps.flatMap((step, index) =>
        orders([...steps.slice(0, index), ...steps.slice(index + 1)]).map((rest) => [step, ...rest])
      );

const oldSource = {
  repoUrl: 'https://github.com/uklans/cache-domains',
  branch: 'master',
  envFilePath: null,
  envSource: 'defaults',
  noFetch: false,
  fetchedAtUtc: '2026-11-10T09:00:00Z',
  fromCache: true
};
const newSource = { ...oldSource, fetchedAtUtc: '2026-11-11T09:00:00Z', fromCache: false };
const previousResult = { services: [{ service: 'steam', status: 'resolved' }] };
const latestResult = { services: [{ service: 'steam', status: 'unresolved' }] };

const idle = (overrides = {}) => ({
  lastResult: previousResult,
  domainsSource: oldSource,
  isRunning: false,
  operationId: null,
  resolverMode: 'auto',
  ...overrides
});

/**
 * Opens the page from its cached status and starts the mount read. The first status read and the
 * first domain-list read wait for the test; every later read answers at once with `serverNow`, the
 * state the server holds after the newer change.
 */
const openPage = ({ cached, serverNow }) => {
  const page = { status: cached, isRunning: false };
  const firstStatusRead = deferred();
  const firstDomainsRead = deferred();
  let statusReads = 0;
  let domainReads = 0;
  const handlers = new Map();
  const lastFinishedOperationRef = { current: null };
  const finishedOperationIdsRef = { current: new Set() };
  const currentOperationRef = { current: null };
  const statusVersionRef = { current: 0 };
  const setStatus = (value) => {
    page.status = typeof value === 'function' ? value(page.status) : value;
  };
  const setIsRunning = (value) => {
    page.isRunning = value;
  };
  const ignore = () => undefined;

  const ApiService = {
    getStatusCheck: () => {
      statusReads += 1;
      return statusReads === 1 ? firstStatusRead.promise : Promise.resolve(serverNow);
    },
    getCacheDomains: () => {
      domainReads += 1;
      return domainReads === 1 ? firstDomainsRead.promise : Promise.resolve({ services: [] });
    }
  };
  const refs = {
    lastFinishedOperationRef,
    finishedOperationIdsRef,
    currentOperationRef,
    statusVersionRef
  };

  const reloadDomains = bindLifted(liftHookCallback(SECTION, 'useCallback', '(domainsSource'), {
    ...refs,
    ApiService,
    setStatus,
    setDomainGroups: ignore,
    setReloadError: ignore,
    getErrorMessage: (error) => error.message
  });

  bindLifted(liftHookCallback(SECTION, 'useEffect', 'handleComplete'), {
    ...refs,
    on: (name, handler) => handlers.set(name, handler),
    off: ignore,
    setIsRunning,
    setProgress: ignore,
    setRunError: ignore,
    setStatus,
    reloadDomains
  })();

  let loadAll;
  loadAll = bindLifted(liftHookCallback(SECTION, 'useCallback', 'getCacheDomains(signal)'), {
    ...refs,
    mockMode: false,
    ApiService,
    loadAll: (signal) => loadAll(signal),
    setStatus,
    setIsRunning,
    setStatusError: ignore,
    setDomainGroups: ignore,
    setReloadError: ignore,
    setIsLoading: ignore,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message
  });

  const mounted = loadAll(new AbortController().signal);
  return { page, handlers, mounted, firstStatusRead, firstDomainsRead };
};

/**
 * Runs `steps` in every order on a fresh page and returns the orders whose page did not end on
 * `expected` (read from the page's status by `shown`).
 */
const ordersThatEndStale = async ({ cached, serverNow, staleRead, fire, shown, expected }) => {
  const stale = [];
  const steps = [
    {
      label: 'the status read answers',
      run: (page) => page.firstStatusRead.resolve(staleRead)
    },
    {
      label: 'the domain list answers',
      run: (page) => page.firstDomainsRead.resolve({ services: [] })
    },
    { label: 'the newer event lands', run: (page) => fire(page.handlers) }
  ];
  for (const order of orders(steps)) {
    const page = openPage({ cached, serverNow });
    for (const step of order) {
      step.run(page);
      await flush();
    }
    await page.mounted;
    await flush();
    if (shown(page.page.status) !== expected)
      stale.push(
        `${order.map((step) => step.label).join(', then ')} -> ${shown(page.page.status)}`
      );
  }
  return stale;
};

test('a domain list fetched during the mount read keeps its new fetch time on the page', async () => {
  const stale = await ordersThatEndStale({
    cached: idle(),
    serverNow: idle({ domainsSource: newSource }),
    // The server answered the status at once, from the list it held before the fetch.
    staleRead: idle(),
    fire: (handlers) => handlers.get('CacheDomainsRefreshed')({ domainsSource: newSource }),
    shown: (status) => status?.domainsSource?.fetchedAtUtc,
    expected: newSource.fetchedAtUtc
  });
  assert.deepEqual(
    stale,
    [],
    `the footer goes back to the old fetch time ${oldSource.fetchedAtUtc} after the list was fetched at ${newSource.fetchedAtUtc}, in these orders: ${stale.join(' | ')}`
  );
});

test('a sweep that finishes during the mount read keeps its result on the page', async () => {
  const shownResult = (status) =>
    status?.lastResult === latestResult
      ? 'the new result'
      : status?.lastResult === previousResult
        ? 'the previous result'
        : 'no result';
  const stale = await ordersThatEndStale({
    cached: idle(),
    serverNow: idle({ lastResult: latestResult }),
    // Another browser's sweep started after the server answered this status read.
    staleRead: idle(),
    fire: (handlers) =>
      handlers.get('StatusCheckComplete')({
        operationId: 'other-browser-sweep',
        success: true,
        result: latestResult,
        canceled: false
      }),
    shown: shownResult,
    expected: 'the new result'
  });
  assert.deepEqual(
    stale,
    [],
    `the page puts the previous sweep result back over the newer one, in these orders: ${stale.join(' | ')}`
  );
});
