import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import {
  MemoryStorage,
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * The Downloads page size offers "All" again. The old one asked the endpoint for every row in a
 * single request and drove the service from 2.3 to 23.7 GiB before it was killed. This one asks for
 * a bounded page at a time and accumulates, so the endpoint never builds more than one page for one
 * response.
 *
 * Everything below runs the code that ships. The walk, the fetch effect that chooses it and the
 * dropdown entry that selects it are all lifted out of their files, so a renamed free variable or a
 * moved arrow fails here rather than quietly leaving a copy under test.
 */

const RETRO_HOOK = 'src/components/features/downloads/useRetroDownloads.ts';
const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const hookSource = parseSource(RETRO_HOOK);
const downloadsTab = parseSource(DOWNLOADS_TAB, ts.ScriptKind.TSX);

/** Source text of the value a named `const` is declared with. */
const initializerOf = (sourceFile, name) =>
  findSoleNode(
    sourceFile,
    `the ${name} declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === name &&
      node.initializer !== undefined
  ).initializer.getText(sourceFile);

/** The value a numeric `const` in the hook is declared with, read from the file rather than typed. */
const numericConstant = (name) => Number(initializerOf(hookSource, name));

/** Runs a lifted expression with its free variables supplied and hands back what it evaluates to. */
const evaluate = (source, bindings = {}) => bindLifted(`() => (${source})`, bindings)();

const SERVER_PAGE_LIMIT = numericConstant('SERVER_PAGE_LIMIT');
const ALL_ITEMS_PAGE_SIZE = numericConstant('ALL_ITEMS_PAGE_SIZE');

assert.ok(
  Number.isInteger(SERVER_PAGE_LIMIT) && SERVER_PAGE_LIMIT > 0,
  'the walk needs a real page limit to ask for'
);

/** The walk that ships, with the page limit it reads supplied by name. */
const fetchEveryPage = bindLifted(liftConstArrow(RETRO_HOOK, 'fetchEveryPage'), {
  SERVER_PAGE_LIMIT
});

// The generator reads SERVICES only on the dashboard paths, never on the retro one under test. The
// real constants module cannot be imported here because it reads `import.meta.env`.
const constantsUrl = moduleUrl(
  `export const SERVICES = ['steam', 'epicgames', 'origin', 'blizzard', 'wsus', 'riot', 'xbox'];`
);
const serviceDisplayNameUrl = await compileToUrl('../src/utils/serviceDisplayName.ts');
const { default: MockDataService } = await import(
  await compileToUrl('../src/test/mockData.service.ts', {
    '../utils/constants': constantsUrl,
    '../utils/serviceDisplayName': serviceDisplayNameUrl
  })
);

/** Enough of i18next to read a label key back. */
const t = (key) => key;

const abortError = () => {
  const error = new Error('The operation was aborted.');
  error.name = 'AbortError';
  return error;
};

/**
 * A stand-in for the endpoint holding `totalRows` grouped rows, answering the same shape
 * `/api/downloads/retro` does and clamping an over-large page the way it does.
 *
 * @param {number} totalRows How many rows the whole filtered set holds.
 * @param {{ failAtPage?: number, onPage?: (page: number) => void }} [behavior] `failAtPage` rejects
 *   that one request; `onPage` runs before each answer, which is where a test changes its mind
 *   mid-walk.
 */
const fakeEndpoint = (totalRows, behavior = {}) => {
  const requests = [];
  const getRetroDownloads = async (params, signal) => {
    requests.push({ ...params });
    if (signal?.aborted) throw abortError();
    behavior.onPage?.(params.page);
    if (params.page === behavior.failAtPage) throw new Error('the endpoint is down');
    const size = Math.min(Math.max(params.pageSize, 1), SERVER_PAGE_LIMIT);
    const start = (params.page - 1) * size;
    const items = [];
    for (let row = start; row < Math.min(start + size, totalRows); row += 1) {
      items.push({ id: `game-appid-${row}`, appName: `Game ${row}`, requestCount: 3 });
    }
    return {
      items,
      totalItems: totalRows,
      totalDownloads: totalRows * 3,
      totalPages: Math.ceil(totalRows / size),
      currentPage: params.page,
      pageSize: size
    };
  };
  return { requests, api: { getRetroDownloads } };
};

/** Lets every already-resolved promise in the walk settle before the assertions read the calls. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/**
 * Runs the hook's fetch effect with every free variable supplied by name, so the branch that picks
 * the walk is the one that ships.
 */
const runFetchEffect = ({ pageSize, mockMode = false, endpoint, options = {} }) => {
  const calls = { setData: [], setError: [], setIsFetching: [], setIsLoading: [] };
  const box = {};
  const effect = bindLifted(
    liftHookCallback(RETRO_HOOK, 'useEffect', 'ApiService.getRetroDownloads'),
    {
      refreshVersion: 0,
      prevRefreshVersionRef: { current: 0 },
      enabled: true,
      page: 1,
      pageSize,
      sort: 'recent',
      service: 'all',
      client: 'all',
      search: '',
      hideLocalhost: false,
      hideMetadata: false,
      hideSmallFiles: false,
      hideEvicted: false,
      hideUnknown: false,
      includeActive: true,
      hitMiss: 'all',
      groupByGame: true,
      groupByService: undefined,
      mergeAcrossServices: true,
      groupUnknownGames: false,
      groupByFrequency: false,
      startTime: undefined,
      endTime: undefined,
      eventId: undefined,
      ...options,
      mockMode,
      MockDataService,
      ApiService: endpoint.api,
      ApiError: class ApiError extends Error {},
      ALL_ITEMS_PAGE_SIZE,
      fetchEveryPage,
      EMPTY_RESPONSE: {
        items: [],
        totalItems: 0,
        totalDownloads: 0,
        totalPages: 0,
        currentPage: 1,
        pageSize: 0
      },
      hasInitialDataRef: { current: false },
      setData: (value) => calls.setData.push(value),
      setError: (value) => calls.setError.push(value),
      setIsFetching: (value) => calls.setIsFetching.push(value),
      setIsLoading: (value) => calls.setIsLoading.push(value)
    }
  );
  box.cleanup = effect();
  return { calls, cleanup: () => box.cleanup(), requests: endpoint.requests };
};

test('All keeps asking for the next page and stops when the set runs out', async () => {
  const endpoint = fakeEndpoint(1000);
  const { calls, requests } = runFetchEffect({ pageSize: ALL_ITEMS_PAGE_SIZE, endpoint });
  await settle();

  assert.deepEqual(
    requests.map((request) => request.page),
    [1, 2, 3, 4, 5],
    'five pages of 200 hold a thousand rows, and there is no sixth request'
  );
  assert.equal(calls.setData.length, 1, 'the reader is handed the set once, when it is complete');
});

test('the rows handed over are every row, each of them once', async () => {
  const endpoint = fakeEndpoint(1000);
  const { calls } = runFetchEffect({ pageSize: ALL_ITEMS_PAGE_SIZE, endpoint });
  await settle();

  const [page] = calls.setData;
  const ids = page.items.map((item) => item.id);
  assert.equal(ids.length, 1000, 'no page was dropped');
  assert.equal(new Set(ids).size, 1000, 'no row arrived twice');
  assert.deepEqual(ids.slice(0, 3), ['game-appid-0', 'game-appid-1', 'game-appid-2']);
  assert.equal(ids[999], 'game-appid-999', 'the last page is on the end, in the server order');
});

// The server re-groups and re-sorts the whole set for each request of the walk, so a download that
// commits between two of them pushes the rest along: the group that ended one page starts the next.
// Two rows with one id are one React key, and expanding either would expand both.
test('a group that shifts across a page boundary mid-walk is kept once', async () => {
  const ids = ['a', 'b', 'c', 'd'];
  const requests = [];
  const shiftingEndpoint = async (params) => {
    requests.push(params.page);
    // The second request finds the list one row longer, so 'b' has moved onto page two.
    const rows = params.page === 1 ? ids : ['new', ...ids];
    const size = 2;
    const start = (params.page - 1) * size;
    return {
      items: rows.slice(start, start + size).map((id) => ({ id, appName: id, requestCount: 1 })),
      totalItems: rows.length,
      totalDownloads: rows.length,
      totalPages: Math.ceil(rows.length / size),
      currentPage: params.page,
      pageSize: size
    };
  };

  const page = await fetchEveryPage({ sort: 'recent' }, shiftingEndpoint);

  const handed = page.items.map((item) => item.id);
  assert.equal(new Set(handed).size, handed.length, `no row arrived twice (${handed.join(', ')})`);
  assert.ok(requests.length > 1, 'the walk still ran to the end of the set');
});

test('the totals describe the whole set and the pager collapses to one page', async () => {
  const endpoint = fakeEndpoint(1000);
  const { calls } = runFetchEffect({ pageSize: ALL_ITEMS_PAGE_SIZE, endpoint });
  await settle();

  const [page] = calls.setData;
  assert.equal(page.totalItems, 1000);
  assert.equal(page.totalDownloads, 3000, 'the sub-label counts the downloads behind every group');
  assert.equal(page.totalPages, 1, 'one page holds everything, so the pager stays hidden');
  assert.equal(page.currentPage, 1);
  assert.equal(page.pageSize, 1000, 'the page is as big as the set it holds');
});

// The whole reason this is a walk. A single request for every row is what ran the service out of
// memory, so no request may ask for more than the endpoint serves - the sentinel included.
test('no request asks for more rows than the endpoint serves', async () => {
  const endpoint = fakeEndpoint(1000);
  const { requests } = runFetchEffect({ pageSize: ALL_ITEMS_PAGE_SIZE, endpoint });
  await settle();

  const sizes = [...new Set(requests.map((request) => request.pageSize))];
  assert.deepEqual(sizes, [SERVER_PAGE_LIMIT], 'every page asked for is the endpoint page limit');
  assert.ok(
    !requests.some((request) => request.pageSize === ALL_ITEMS_PAGE_SIZE),
    'the sentinel that means All is never sent as a page size'
  );
});

test('an empty set is one request and no rows', async () => {
  const endpoint = fakeEndpoint(0);
  const { calls, requests } = runFetchEffect({ pageSize: ALL_ITEMS_PAGE_SIZE, endpoint });
  await settle();

  assert.equal(requests.length, 1);
  assert.deepEqual(calls.setData[0].items, []);
  assert.equal(calls.setData[0].totalItems, 0);
});

// Changing a filter unmounts the effect, which aborts the walk. The rows it had gathered live in
// that call's own list, so there is nothing for them to merge into.
test('a walk abandoned partway hands over nothing', async () => {
  // The effect has to exist before its cleanup can be called, so the walk reaches back for it.
  const walk = { abandon: null };
  const endpoint = fakeEndpoint(1000, {
    onPage: (page) => {
      if (page === 3) walk.abandon();
    }
  });
  const run = runFetchEffect({ pageSize: ALL_ITEMS_PAGE_SIZE, endpoint });
  walk.abandon = run.cleanup;
  await settle();

  assert.deepEqual(run.calls.setData, [], 'the rows from the abandoned walk never reach the page');
  assert.deepEqual(run.calls.setError, [], 'abandoning is not a failure to report');
  assert.ok(
    run.requests.length < 5,
    `the walk stopped instead of running to the end (it asked for ${run.requests.length} pages)`
  );
});

// Six pages of a twelve-page set are not the set. Presenting them would read as a complete list
// that is simply shorter than the reader expected.
test('a request that fails partway is reported instead of the pages gathered so far', async () => {
  const endpoint = fakeEndpoint(1000, { failAtPage: 4 });
  const { calls } = runFetchEffect({ pageSize: ALL_ITEMS_PAGE_SIZE, endpoint });
  await settle();

  assert.deepEqual(calls.setData, [], 'the three pages it had are not presented as the whole set');
  assert.equal(calls.setError.length, 1);
  assert.equal(calls.setError[0].message, 'the endpoint is down');
});

// Discarding the partial set leaves the last page that worked on screen. Without a banner over it
// there is nothing to tell a stale fifty rows apart from a fresh answer to "All".
test('a failed page says so over the rows the last one left behind', () => {
  const banner = findSoleNode(
    downloadsTab,
    'the error banner drawn over the rows',
    (node) =>
      ts.isJsxExpression(node) &&
      node.expression !== undefined &&
      ts.isBinaryExpression(node.expression) &&
      node.expression.left.getText(downloadsTab) === 'serverPage.error'
  ).getText(downloadsTab);

  assert.match(banner, /downloads\.tab\.errors\.loadFailed/);
});

test('a numbered page size still asks once, for that page', async () => {
  const endpoint = fakeEndpoint(1000);
  const { calls, requests } = runFetchEffect({ pageSize: 50, endpoint, options: { page: 3 } });
  await settle();

  assert.equal(requests.length, 1);
  assert.equal(requests[0].page, 3);
  assert.equal(requests[0].pageSize, 50);
  assert.equal(calls.setData[0].items.length, 50);
  assert.equal(calls.setData[0].totalPages, 20, 'the pager still has every page to walk');
});

test('mock mode answers All out of the generator and asks the server for nothing', async () => {
  const endpoint = fakeEndpoint(1000);
  const { calls, requests } = runFetchEffect({
    pageSize: ALL_ITEMS_PAGE_SIZE,
    mockMode: true,
    endpoint
  });
  await settle();

  assert.deepEqual(requests, [], 'no request leaves the browser while the toggle is on');

  const [page] = calls.setData;
  const firstTwenty = MockDataService.generateMockRetroData({
    page: 1,
    pageSize: 20,
    sort: 'recent',
    service: 'all',
    client: 'all',
    search: '',
    hideLocalhost: false,
    showZeroBytes: true,
    hideUnknown: false,
    includeActive: true,
    hitMiss: 'all',
    groupByGame: true,
    mergeAcrossServices: true
  });
  assert.equal(
    page.items.length,
    firstTwenty.totalItems,
    'All in mock mode holds every generated row, not one page of them'
  );
  assert.ok(page.items.length > firstTwenty.items.length, 'and more than a numbered page holds');
  assert.equal(page.totalPages, 1);
});

// -- the dropdown entry -------------------------------------------------------------------------

const itemsPerPageOptions = (viewMode) =>
  bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useMemo', 'downloads.tab.filters.allItems'), {
    settings: { viewMode },
    t,
    ALL_ITEMS_PAGE_SIZE
  })();

test('the page size dropdown offers All, labeled from the key that was already there', () => {
  const options = itemsPerPageOptions('normal');
  const all = options.find((option) => option.label === 'downloads.tab.filters.allItems');
  assert.ok(all, 'the grouped views offer All');
  assert.equal(
    Number(all.value),
    ALL_ITEMS_PAGE_SIZE,
    'picking it hands the hook the size the walk branches on'
  );
  assert.deepEqual(
    options.map((option) => option.value),
    ['20', '50', '100', '200', String(ALL_ITEMS_PAGE_SIZE)],
    'All sits at the end of the sizes that were already there'
  );
});

// The retro rows carry banners, tooltips and a wide column set, which is why 200 is already off its
// list. All would be the same choice, only more so.
test('the retro table is offered neither 200 nor All', () => {
  const options = itemsPerPageOptions('retro').map((option) => option.value);
  assert.deepEqual(options, ['20', '50', '100']);
});

// -- the size the retro table opens on ----------------------------------------------------------

// The dropdown above offers the retro table 20, 50 and 100 only, but the page writes the size it
// is already showing under the retro key on the render that switches there. So a reader on All, or
// on 200, leaves a size behind in storage that this table has no entry for - and All means walk
// every page of a table whose rows carry banners, tooltips and a wide column set.

const STORAGE_KEYS = evaluate(initializerOf(downloadsTab, 'STORAGE_KEYS'));
const DEFAULT_ITEMS_PER_PAGE = evaluate(initializerOf(downloadsTab, 'DEFAULT_ITEMS_PER_PAGE'));

/** What the switch effect writes when the reader picks the retro table with `stored` under its key. */
const switchToRetro = (stored) => {
  const storage = new MemoryStorage();
  storage.setItem(STORAGE_KEYS.ITEMS_PER_PAGE_RETRO, stored);
  const settings = { viewMode: 'retro', itemsPerPage: ALL_ITEMS_PAGE_SIZE };
  const written = [];
  bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useEffect', 'Cap at 100 when switching to retro'), {
    prevViewModeRef: { current: 'normal' },
    settings,
    compactEverMounted: { current: false },
    cardEverMounted: { current: false },
    normalEverMounted: { current: true },
    retroEverMounted: { current: false },
    previousNonRetroItemsPerPage: { current: 50 },
    storage,
    STORAGE_KEYS,
    DEFAULT_ITEMS_PER_PAGE,
    ALL_ITEMS_PAGE_SIZE,
    setSettings: (update) => written.push(update(settings))
  })();
  return written;
};

test('switching to the retro table off All lands on 100, not on All', () => {
  const written = switchToRetro(String(ALL_ITEMS_PAGE_SIZE));
  assert.equal(written.length, 1, 'the size is rewritten on the way in');
  assert.equal(written[0].itemsPerPage, 100, 'the largest size the retro table offers');
});

test('switching to the retro table off 200 lands on 100', () => {
  assert.equal(switchToRetro('200')[0].itemsPerPage, 100);
});

/** The settings a visit starts with, given what browser storage holds. */
const savedSettings = (stored) => {
  const storage = new MemoryStorage();
  for (const [key, value] of Object.entries(stored)) {
    storage.setItem(key, value);
  }
  const readStorage = { storage, STORAGE_KEYS };
  return bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useState', 'savedViewMode'), {
    ...readStorage,
    DEFAULT_ITEMS_PER_PAGE,
    loadHideMetadata: bindLifted(initializerOf(downloadsTab, 'loadHideMetadata'), readStorage),
    loadHideSmallFiles: bindLifted(initializerOf(downloadsTab, 'loadHideSmallFiles'), readStorage),
    ALL_ITEMS_PAGE_SIZE
  })();
};

test('reopening on the retro table reads a stored All back as 100', () => {
  const settings = savedSettings({
    [STORAGE_KEYS.VIEW_MODE]: 'retro',
    [STORAGE_KEYS.ITEMS_PER_PAGE_RETRO]: String(ALL_ITEMS_PAGE_SIZE)
  });

  assert.equal(settings.viewMode, 'retro');
  assert.equal(
    settings.itemsPerPage,
    100,
    'the walk does not start itself again on the next visit'
  );
});

test('reopening on the retro table keeps a size it does offer', () => {
  const settings = savedSettings({
    [STORAGE_KEYS.VIEW_MODE]: 'retro',
    [STORAGE_KEYS.ITEMS_PER_PAGE_RETRO]: '20'
  });

  assert.equal(settings.itemsPerPage, 20);
});

test('the All label is the one both locales already carry', () => {
  const locale = (file) =>
    JSON.parse(readFileSync(new URL(`../src/i18n/locales/${file}`, import.meta.url), 'utf8'));
  for (const file of ['en.json', 'zh.json']) {
    const filters = locale(file).downloads.tab.filters;
    assert.ok(filters.allItems, `${file} has downloads.tab.filters.allItems`);
  }
});
