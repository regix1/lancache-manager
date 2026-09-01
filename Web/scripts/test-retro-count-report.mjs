import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * The retro table owns the fetch while it is showing, so the row count the page reads comes back
 * from the table. Getting that handover wrong once emptied the page: the table reported the zero it
 * holds before its first response, the page replaced the whole view with its empty-range notice,
 * and unmounting the table aborted the very fetch that would have corrected the number.
 *
 * Every assertion below drives the arrow that ships, lifted out of the component or the hook, so a
 * guard that stops holding fails here rather than on someone's screen.
 */

const RETRO_HOOK = 'src/components/features/downloads/useRetroDownloads.ts';
const RETRO_VIEW = 'src/components/features/downloads/RetroView.tsx';
const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const downloadsTab = parseSource(DOWNLOADS_TAB, ts.ScriptKind.TSX);

/** The count report, with the hook result and the page's setter supplied by name. */
const runReportEffect = (serverMode, serverRetro) => {
  const reported = [];
  bindLifted(liftHookCallback(RETRO_VIEW, 'useEffect', 'onTotalItemsChange'), {
    serverMode,
    serverRetro,
    onTotalItemsChange: (total) => reported.push(total)
  })();
  return reported;
};

/** A hook result shaped like the one RetroView reads, with the flags the caller cares about. */
const hookResult = (overrides) => ({
  items: [],
  totalItems: 0,
  totalDownloads: 0,
  totalPages: 0,
  currentPage: 1,
  pageSize: 0,
  isLoading: false,
  isFetching: false,
  hasResponse: false,
  error: null,
  ...overrides
});

test('the table reports nothing on the commit before its first response has arrived', () => {
  // Both flags are false here, and that is the whole trap: `isLoading` is initialized false and is
  // only set true by the fetch effect, which is a sibling of the report effect, so its update is
  // not visible on the commit the table mounts in.
  assert.deepEqual(
    runReportEffect(true, hookResult({ isLoading: false, isFetching: false, totalItems: 0 })),
    [],
    'a count reported before the first response is the placeholder zero, not a row count'
  );
});

test('the table reports the count the server answered with', () => {
  assert.deepEqual(runReportEffect(true, hookResult({ hasResponse: true, totalItems: 42 })), [42]);
});

test('a response that really is empty is reported, so the export button can read it', () => {
  assert.deepEqual(runReportEffect(true, hookResult({ hasResponse: true, totalItems: 0 })), [0]);
});

test('a table that is not showing reports nothing at all', () => {
  assert.deepEqual(runReportEffect(false, hookResult({ hasResponse: true, totalItems: 42 })), []);
});

// -- the page keeps the table on screen when the count is genuinely zero -------------------------

const emptyStateGuard = findSoleNode(
  downloadsTab,
  'the empty-range guard',
  (node) =>
    ts.isIfStatement(node) && node.expression.getText(downloadsTab).includes('hasNarrowingFilter')
).expression.getText(downloadsTab);

/** The page's empty-range guard, evaluated against a view mode and a count. */
const replacesThePage = (viewMode, visibleTotalItems) =>
  bindLifted(`() => (${emptyStateGuard})`, {
    visibleTotalItems,
    hasNarrowingFilter: false,
    activeTab: 'recent',
    settings: { viewMode }
  })();

test('an empty retro table stays on screen instead of being replaced by the notice', () => {
  // Replacing the page here unmounts the table, which aborts its fetch and drops its refresh
  // subscription, so the first download to land would never clear the notice. The table draws its
  // own empty state and keeps fetching instead.
  assert.equal(replacesThePage('retro', 0), false);
});

test('the other views still get the notice when nothing has been recorded', () => {
  assert.equal(replacesThePage('normal', 0), true);
  assert.equal(replacesThePage('compact', 0), true);
  assert.equal(replacesThePage('card', 0), true);
});

test('a page with rows in it is never replaced', () => {
  assert.equal(replacesThePage('retro', 12), false);
  assert.equal(replacesThePage('normal', 12), false);
});

// -- what brings the rows back once the table has stayed on screen ------------------------------

test('a refresh event while the table is showing asks it to fetch again', () => {
  const scheduled = [];
  const handlers = new Map();
  const cleanup = bindLifted(
    liftHookCallback(RETRO_HOOK, 'useEffect', 'RETRO_REFRESH_EVENTS.forEach'),
    {
      mockMode: false,
      enabled: true,
      timeRange: 'live',
      on: (name, handler) => handlers.set(name, handler),
      off: (name) => handlers.delete(name),
      reload: () => 'reload',
      scheduleReload: (fn) => scheduled.push(fn()),
      RETRO_REFRESH_EVENTS: ['DownloadCompleted'],
      RETRO_LIVE_ONLY_EVENTS: ['DownloadsRefresh']
    }
  )();

  handlers.get('DownloadCompleted')();
  handlers.get('DownloadsRefresh')();

  assert.deepEqual(scheduled, ['reload', 'reload'], 'a landed download has to refetch the table');

  cleanup();
  assert.equal(handlers.size, 0, 'every handler the effect added has to be taken off again');
});

// -- the busy indicator ------------------------------------------------------------------------

test('switching the fetch off clears the busy flags it left behind', () => {
  const calls = { setIsFetching: [], setIsLoading: [] };
  const cleanup = bindLifted(
    liftHookCallback(RETRO_HOOK, 'useEffect', 'ApiService.getRetroDownloads'),
    {
      refreshVersion: 0,
      prevRefreshVersionRef: { current: 0 },
      enabled: false,
      setIsFetching: (value) => calls.setIsFetching.push(value),
      setIsLoading: (value) => calls.setIsLoading.push(value)
    }
  )();

  // The settle skips an aborted request on purpose, so nothing else can turn these off: the
  // request that was in flight when the view switched away is aborted by the cleanup and its
  // `finally` returns early. Left set, the spinner beside the search box never stops.
  assert.deepEqual(calls.setIsFetching, [false]);
  assert.deepEqual(calls.setIsLoading, [false]);
  assert.equal(cleanup, undefined, 'no request started, so there is nothing to abort');
});

// -- an answer with no rows in it is still an answer --------------------------------------------

const retroHook = parseSource(RETRO_HOOK);

const hasResponseText = findSoleNode(
  retroHook,
  'the hasResponse the hook returns',
  (node) => ts.isPropertyAssignment(node) && node.name.getText(retroHook) === 'hasResponse'
).initializer.getText(retroHook);

test('a response holding no rows still counts as a response', () => {
  const hasResponse = (data) => bindLifted(`() => (${hasResponseText})`, { data })();

  assert.equal(hasResponse(null), false, 'nothing has answered yet');
  assert.equal(
    hasResponse({ items: [], totalItems: 0, totalDownloads: 0, totalPages: 0 }),
    true,
    'an empty table is an answer; without this the count is never reported and the notice sticks'
  );
});

test('the hook stores the empty answer it was given rather than dropping it', async () => {
  const calls = { setData: [], setError: [], setIsFetching: [], setIsLoading: [] };
  const empty = {
    items: [],
    totalItems: 0,
    totalDownloads: 0,
    totalPages: 0,
    currentPage: 1,
    pageSize: 20
  };
  bindLifted(liftHookCallback(RETRO_HOOK, 'useEffect', 'ApiService.getRetroDownloads'), {
    refreshVersion: 0,
    prevRefreshVersionRef: { current: 0 },
    enabled: true,
    page: 1,
    pageSize: 20,
    sort: 'recent',
    service: 'all',
    client: 'all',
    search: '',
    hideLocalhost: false,
    hideMetadata: false,
    hideSmallFiles: false,
    hideEvicted: false,
    hideUnknown: false,
    includeActive: undefined,
    hitMiss: 'all',
    groupByGame: false,
    groupByService: false,
    mergeAcrossServices: undefined,
    groupUnknownGames: undefined,
    groupByFrequency: undefined,
    startTime: undefined,
    endTime: undefined,
    eventId: undefined,
    mockMode: false,
    MockDataService: {
      generateMockRetroData: () => {
        throw new Error('the toggle is off, so the generator must not be reached');
      }
    },
    ApiService: { getRetroDownloads: () => Promise.resolve(empty) },
    ApiError: class ApiError extends Error {},
    ALL_ITEMS_PAGE_SIZE: null,
    fetchEveryPage: () => {
      throw new Error('a numbered page size must not take the All walk');
    },
    EMPTY_RESPONSE: empty,
    hasInitialDataRef: { current: false },
    setData: (value) => calls.setData.push(value),
    setError: (value) => calls.setError.push(value),
    setIsFetching: (value) => calls.setIsFetching.push(value),
    setIsLoading: (value) => calls.setIsLoading.push(value)
  })();

  await Promise.resolve();
  await Promise.resolve();

  assert.deepEqual(
    calls.setData,
    [empty],
    'the empty page has to reach state to count as an answer'
  );
  assert.deepEqual(calls.setError, [null], 'an empty page is not an error');
});
