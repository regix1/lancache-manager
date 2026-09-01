import assert from 'node:assert/strict';
import test from 'node:test';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  liftHookCallback,
  moduleUrl
} from './transpile-module.mjs';

/**
 * Mock mode promises two things on the Downloads and Retro views: rows appear, and nothing is
 * asked of the server. Both halves live in shipped files, and both are driven here rather than
 * restated:
 *
 *   - the fetch effect inside `useRetroDownloads`, lifted out of the hook, so the branch that
 *     picks the generator over `ApiService` is the one that ships;
 *   - `MockDataService.generateMockRetroData`, imported and asked the same queries the Downloads
 *     controls build, so a control that stopped changing the answer fails here.
 *   - the three places in `DownloadsTab` that still hold an `ApiService` call of their own: the
 *     eviction-settings read, the sessions of an expanded group, and the export button.
 */

const RETRO_HOOK = 'src/components/features/downloads/useRetroDownloads.ts';
const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

// The mock generator reads SERVICES only from the paths that build the dashboard's rows, never
// from the retro path under test. The real module cannot be imported here because it reads
// `import.meta.env`, which has no value outside Vite.
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

/** The query the Downloads page builds, with one page big enough to hold every generated row. */
const query = (overrides = {}) => ({
  page: 1,
  pageSize: 200,
  sort: 'latest',
  service: 'all',
  client: 'all',
  search: '',
  hideLocalhost: false,
  showZeroBytes: true,
  hideSmallFiles: false,
  hideEvicted: false,
  hideUnknown: false,
  includeActive: true,
  hitMiss: 'all',
  groupByGame: true,
  mergeAcrossServices: true,
  groupUnknownGames: false,
  groupByFrequency: false,
  ...overrides
});

const retro = (overrides = {}) => MockDataService.generateMockRetroData(query(overrides));

/** Every generated row, ungrouped, which is what the row-level filters act on. */
const rows = (overrides = {}) =>
  retro({ groupByGame: false, mergeAcrossServices: false, ...overrides }).items;

/**
 * Runs the hook's fetch effect with every free variable supplied by name. A renamed or moved one
 * throws here instead of quietly testing a copy.
 */
const runFetchEffect = ({ mockMode, apiService, options = {} }) => {
  const calls = { setData: [], setError: [], setIsFetching: [], setIsLoading: [] };
  const settings = query(options);
  const effect = bindLifted(
    liftHookCallback(RETRO_HOOK, 'useEffect', 'ApiService.getRetroDownloads'),
    {
      refreshVersion: 0,
      prevRefreshVersionRef: { current: 0 },
      enabled: true,
      page: settings.page,
      pageSize: settings.pageSize,
      sort: settings.sort,
      service: settings.service,
      client: settings.client,
      search: settings.search,
      hideLocalhost: settings.hideLocalhost,
      hideMetadata: !settings.showZeroBytes,
      hideSmallFiles: settings.hideSmallFiles,
      hideEvicted: settings.hideEvicted,
      hideUnknown: settings.hideUnknown,
      includeActive: settings.includeActive,
      hitMiss: settings.hitMiss,
      groupByGame: settings.groupByGame,
      groupByService: settings.groupByService,
      mergeAcrossServices: settings.mergeAcrossServices,
      groupUnknownGames: settings.groupUnknownGames,
      groupByFrequency: settings.groupByFrequency,
      startTime: settings.startTime,
      endTime: settings.endTime,
      eventId: settings.eventId,
      mockMode,
      MockDataService,
      ApiService: apiService,
      ApiError: class ApiError extends Error {},
      // The "All" page size and the walk it takes. Every query below asks for a numbered page, so
      // a value no page size can equal keeps them on the single-page path, and the walk throws if
      // one ever reaches it. All is covered in scripts/test-all-items-page-size.mjs.
      ALL_ITEMS_PAGE_SIZE: null,
      fetchEveryPage: () => {
        throw new Error('a numbered page size must not take the All walk');
      },
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
  const cleanup = effect();
  return { calls, cleanup };
};

test('mock mode serves the Downloads rows from the generator and asks the server for nothing', () => {
  const requested = [];
  const { calls } = runFetchEffect({
    mockMode: true,
    apiService: {
      getRetroDownloads: (params) => {
        requested.push(params);
        return Promise.resolve(null);
      }
    }
  });

  assert.deepEqual(requested, [], 'mock mode must not reach ApiService.getRetroDownloads');
  assert.equal(calls.setData.length, 1);
  assert.ok(calls.setData[0].items.length > 0, 'mock mode must serve rows, not an empty page');
  assert.equal(calls.setIsFetching.at(-1), false);
  assert.equal(calls.setIsLoading.at(-1), false);
});

test('with the toggle off the hook still fetches the page it always did', async () => {
  const requested = [];
  const response = {
    items: [{ id: 'server-row' }],
    totalItems: 1,
    totalDownloads: 1,
    totalPages: 1,
    currentPage: 1,
    pageSize: 200
  };
  const { calls, cleanup } = runFetchEffect({
    mockMode: false,
    apiService: {
      getRetroDownloads: (params, signal) => {
        requested.push({ params, signal });
        return Promise.resolve(response);
      }
    }
  });

  await Promise.resolve();
  await Promise.resolve();

  assert.equal(requested.length, 1, 'the real path must issue exactly one request');
  assert.equal(requested[0].params.pageSize, 200);
  assert.ok(requested[0].signal instanceof AbortSignal);
  assert.deepEqual(calls.setData, [response]);
  assert.equal(typeof cleanup, 'function');
});

test('mock mode opens a group from the row already on the page and asks the server for nothing', () => {
  const [row] = retro().items;
  const members = [];
  const errors = [];
  const cleanup = bindLifted(
    liftHookCallback(DOWNLOADS_TAB, 'useEffect', 'ApiService.getDownloadsByIds'),
    {
      expandedItem: row.id,
      serverPage: { items: [row] },
      mockMode: true,
      setExpandedMembers: (value) => members.push(value),
      ApiService: {
        getDownloadsByIds: () => {
          throw new Error('mock mode must not reach ApiService.getDownloadsByIds');
        }
      },
      notifyError: (...args) => errors.push(args),
      t: (key) => key
    }
  )();

  assert.deepEqual(members, [{ groupId: row.id, downloads: [row.primaryDownload] }]);
  assert.deepEqual(errors, []);
  assert.equal(cleanup, undefined, 'no request started, so there is nothing to abort');
});

test('mock mode keeps the stored evicted-data mode and asks the server for nothing', () => {
  const cleanup = bindLifted(
    liftHookCallback(DOWNLOADS_TAB, 'useEffect', 'ApiService.getEvictionSettings'),
    {
      mockMode: true,
      ApiService: {
        getEvictionSettings: () => {
          throw new Error('mock mode must not reach ApiService.getEvictionSettings');
        }
      }
    }
  )();

  assert.equal(cleanup, undefined, 'no request started, so there is nothing to abort');
});

test('mock mode exports the sessions already on the page and asks the server for nothing', async () => {
  const itemsToDisplay = retro().items.map((row) => ({
    downloads: row.primaryDownload ? [row.primaryDownload] : []
  }));
  const exported = [];
  const errors = [];
  const loading = [];
  const handleExport = bindLifted(liftConstArrow(DOWNLOADS_TAB, 'handleExport'), {
    mockMode: true,
    itemsToDisplay,
    ApiService: {
      getDownloadRows: () => {
        throw new Error('mock mode must not reach ApiService.getDownloadRows');
      }
    },
    retroTimeParams: { startTime: undefined, endTime: undefined },
    retroEventId: null,
    serverClientFilter: 'all',
    evictedDataMode: 'show',
    settings: {
      searchQuery: '',
      hideMetadata: false,
      hideSmallFiles: false,
      hideLocalhost: false,
      hideEvicted: false,
      hideUnknownGames: false,
      hitMissFilter: 'all',
      selectedService: 'all'
    },
    downloadTextFile: (content, name, type) => exported.push({ content, name, type }),
    notifyError: (...args) => errors.push(args),
    t: (key) => key,
    setExportLoading: (value) => loading.push(value),
    exportAbort: { current: null }
  });

  await handleExport('json');

  // The handler catches everything into notifyError, so an unbound free variable would otherwise
  // read as a quiet pass with no file written.
  assert.deepEqual(errors, []);
  assert.equal(exported.length, 1);
  assert.equal(
    exported[0].content,
    JSON.stringify(
      itemsToDisplay.flatMap((group) => group.downloads),
      null,
      2
    )
  );
  assert.equal(exported[0].type, 'application/json');
  assert.deepEqual(loading, [true, false], 'the button must not be left spinning');
});

test('the generated page carries the fields the retro row renders from', () => {
  const [row] = retro().items;
  for (const field of [
    'id',
    'startTimeUtc',
    'lastStartTimeUtc',
    'endTimeUtc',
    'appName',
    'service',
    'datasource',
    'clientIp',
    'clientIps',
    'depotIds',
    'downloadIds',
    'cacheHitPercent',
    'totalBytes',
    'requestCount',
    'isEvicted',
    'isPartiallyEvicted',
    'hasRealGameName',
    'groupType'
  ]) {
    assert.ok(field in row, `a generated row is missing ${field}`);
  }
  // The two fields the generator filters on are its own, and must not reach the row.
  assert.equal('isActive' in row, false);
  assert.equal('eventIds' in row, false);
});

test('hiding localhost, small files, evicted rows and unknown games each removes rows', () => {
  const all = rows().length;
  assert.ok(rows({ hideLocalhost: true }).length < all, 'hideLocalhost changed nothing');
  assert.ok(rows({ hideSmallFiles: true }).length < all, 'hideSmallFiles changed nothing');
  assert.ok(rows({ hideEvicted: true }).length < all, 'hideEvicted changed nothing');
  assert.ok(rows({ hideUnknown: true }).length < all, 'hideUnknown changed nothing');
  assert.ok(rows({ includeActive: false }).length < all, 'includeActive changed nothing');
  assert.ok(rows({ showZeroBytes: false }).length < all, 'showZeroBytes changed nothing');

  assert.equal(
    rows({ hideLocalhost: true }).some((row) => row.clientIp === '127.0.0.1'),
    false
  );
  assert.equal(
    rows({ hideSmallFiles: true }).some((row) => row.totalBytes < 1048576),
    false
  );
  assert.equal(
    rows({ hideEvicted: true }).some((row) => row.isEvicted),
    false
  );
  assert.equal(
    rows({ hideUnknown: true }).some((row) => row.appName === 'Unknown/Other'),
    false
  );
  assert.equal(
    rows({ showZeroBytes: false }).some((row) => row.totalBytes === 0),
    false
  );
});

test('the hit and miss buckets split the rows between them', () => {
  const all = rows().length;
  const hit = rows({ hitMiss: 'hit' });
  const miss = rows({ hitMiss: 'miss' });

  assert.ok(
    hit.length > 0 && miss.length > 0,
    'one bucket is empty, so the control cannot be seen'
  );
  assert.equal(hit.length + miss.length, all);
  assert.equal(
    hit.some((row) => row.cacheHitPercent < 50),
    false
  );
  assert.equal(
    miss.some((row) => row.cacheHitPercent >= 50),
    false
  );
});

test('the service filter folds the Xbox aliases and the client filter takes a list', () => {
  const all = rows().length;
  const steam = rows({ service: 'steam' });
  assert.ok(steam.length > 0 && steam.length < all);
  assert.equal(
    steam.some((row) => row.service !== 'steam'),
    false
  );

  // xboxlive is a raw log alias of xbox; picking "xbox" in the dropdown has to reach both.
  const xbox = rows({ service: 'xbox' });
  assert.ok(xbox.some((row) => row.service === 'xbox'));
  assert.ok(xbox.some((row) => row.service === 'xboxlive'));

  const pair = rows({ client: '192.168.1.100,10.0.0.50' });
  assert.ok(pair.length > 0 && pair.length < all);
  assert.deepEqual([...new Set(pair.map((row) => row.clientIp))].sort(), [
    '10.0.0.50',
    '192.168.1.100'
  ]);
});

test('search narrows to rows that carry the term', () => {
  const found = rows({ search: 'dota' });
  assert.ok(found.length > 0 && found.length < rows().length);
  assert.equal(
    found.some((row) => !row.appName.toLowerCase().includes('dota')),
    false
  );
});

test('the time range and the event filter each narrow the set', () => {
  const nowSeconds = Math.floor(Date.now() / 1000);
  const lastDay = rows({ startTime: nowSeconds - 24 * 60 * 60, endTime: nowSeconds });
  assert.ok(lastDay.length > 0, 'a 24 hour range must still hold rows');
  assert.ok(lastDay.length < rows().length, 'a 24 hour range must drop the older rows');

  // 9002 is the second mock event; generateMockEvents publishes the same id to the picker.
  const tagged = rows({ eventId: 9002 });
  assert.ok(tagged.length > 0, 'a mock event must hold rows or its filter looks broken');
  assert.ok(tagged.length < rows().length);
});

test('grouping by game merges one title seen under two services, and by service folds Xbox', () => {
  const ungrouped = rows().length;
  const byGame = retro().items;
  const perService = retro({ groupByService: true }).items;

  assert.ok(byGame.length < ungrouped, 'grouping by game must produce fewer rows');
  assert.equal(
    byGame.filter((row) => row.appName === 'Rocket League').length,
    1,
    'the same title under two services must merge into one row'
  );
  // Without the cross-service key the two Rocket League rows stay apart.
  assert.equal(
    retro({ mergeAcrossServices: false }).items.filter((row) => row.appName === 'Rocket League')
      .length,
    2
  );

  assert.deepEqual(
    perService.map((row) => row.service).sort(),
    ['blizzard', 'epicgames', 'origin', 'riot', 'steam', 'wsus', 'xbox'],
    'xbox and xboxlive must fold together while wsus keeps its own row'
  );
});

test('grouping unknown games moves the unmapped Steam rows into their own bucket', () => {
  const withoutBucket = retro().items;
  const withBucket = retro({ groupUnknownGames: true }).items;

  assert.equal(
    withoutBucket.some((row) => row.id === 'unknown-other'),
    false
  );
  const bucket = withBucket.find((row) => row.id === 'unknown-other');
  assert.ok(bucket, 'the Unknown/Other bucket never appeared');
  assert.equal(bucket.appName, 'Unknown/Other');
  assert.equal(bucket.service, 'unknown');
  assert.equal(bucket.groupType, 'content');
});

test('grouping by frequency sorts the single-download rows last', () => {
  const plain = rows();
  const bucketed = rows({ groupByFrequency: true });

  assert.notDeepEqual(
    plain.map((row) => row.id),
    bucketed.map((row) => row.id)
  );
  const firstSingle = bucketed.findIndex((row) => row.requestCount === 1);
  assert.ok(firstSingle > 0, 'a repeated group must come first');
  assert.equal(
    bucketed.slice(firstSingle).some((row) => row.requestCount > 1),
    false,
    'a repeated group is stranded behind a single-download one'
  );
});

test('each sort orders the page differently', () => {
  const largest = retro({ sort: 'largest' }).items;
  const smallest = retro({ sort: 'smallest' }).items;
  const alphabetical = retro({ sort: 'alphabetical' }).items;

  assert.deepEqual(
    largest.map((row) => row.totalBytes),
    [...largest.map((row) => row.totalBytes)].sort((a, b) => b - a)
  );
  assert.deepEqual(
    smallest.map((row) => row.totalBytes),
    [...smallest.map((row) => row.totalBytes)].sort((a, b) => a - b)
  );
  assert.deepEqual(
    alphabetical.map((row) => row.appName.toLowerCase()),
    [...alphabetical.map((row) => row.appName.toLowerCase())].sort()
  );
});

test('paging slices the same ordered list the totals are counted from', () => {
  const whole = retro();
  const firstPage = retro({ pageSize: 10 });
  const lastPage = retro({ pageSize: 10, page: firstPage.totalPages });

  assert.equal(firstPage.totalItems, whole.totalItems);
  assert.equal(firstPage.totalPages, Math.ceil(whole.totalItems / 10));
  assert.equal(firstPage.items.length, 10);
  assert.deepEqual(
    firstPage.items.map((row) => row.id),
    whole.items.slice(0, 10).map((row) => row.id)
  );
  assert.equal(lastPage.items.length, whole.totalItems - (firstPage.totalPages - 1) * 10);
});

test('the download count behind the rows survives grouping', () => {
  const ungrouped = retro({ groupByGame: false, mergeAcrossServices: false });
  const grouped = retro();

  assert.equal(
    ungrouped.totalDownloads,
    ungrouped.items.reduce((sum, row) => sum + row.requestCount, 0)
  );
  assert.equal(
    grouped.totalDownloads,
    ungrouped.totalDownloads,
    'merging rows must not change how many downloads they stand for'
  );
  assert.ok(grouped.totalItems < ungrouped.totalItems);
});
