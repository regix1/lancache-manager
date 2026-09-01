import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

/**
 * The dashboard provider's in-flight signal for a download-filter change, driven through the
 * effect that ships.
 *
 * A filter fetch asks for no skeleton, so the provider's one loading flag stays false all the way
 * through it and nothing else can tell that the narrowed figures on screen are still the previous
 * selection's. The effect below is what raises the signal and what has to put it back down again,
 * including when the request it started is thrown away by the next selection. It is never
 * exported, so it is lifted out of the file by shape and called with its free variables supplied
 * by name.
 */

const PROVIDER = 'src/contexts/DashboardDataContext/index.tsx';

const effectSource = liftHookCallback(PROVIDER, 'useEffect', "trigger: 'downloadFilterChange'");

/** The provider's count of unanswered filter fetches, stepped the way React steps state. */
const createPendingCount = () => {
  let count = 0;
  return {
    step: (updater) => {
      count = updater(count);
    },
    get fetching() {
      return count > 0;
    }
  };
};

/**
 * One provider: the pending count, the fetches the effect started, and a way to hand it a new
 * selection. Each fetch is answered when the test says so, so a request can still be out while
 * the next one starts.
 */
const createProvider = () => {
  const pending = createPendingCount();
  const calls = [];
  const prevDownloadFiltersRef = { current: { service: 'all', client: 'all' } };
  const fetchAllData = (options) => {
    let deliver;
    const answered = new Promise((resolve) => {
      deliver = resolve;
    });
    calls.push({ options, deliver });
    return answered;
  };
  const selectFilters = (downloadFilters) =>
    bindLifted(effectSource, {
      mockMode: false,
      hasAccess: true,
      prevDownloadFiltersRef,
      downloadFilters,
      setDownloadFilterFetches: pending.step,
      fetchAllData
    })();
  return { pending, calls, selectFilters };
};

/** Lets the callbacks hung off a delivered answer run before the next assertion. */
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

test('a new selection is in flight until its own answer lands', async () => {
  const { pending, calls, selectFilters } = createProvider();

  selectFilters({ service: 'steam', client: 'all' });

  assert.equal(calls.length, 1);
  assert.equal(pending.fetching, true);

  calls[0].deliver();
  await flush();

  assert.equal(pending.fetching, false);
});

test('the answer to a replaced selection leaves the newer one in flight', async () => {
  const { pending, calls, selectFilters } = createProvider();

  selectFilters({ service: 'steam', client: 'all' });
  selectFilters({ service: 'steam', client: '10.0.0.5' });
  assert.equal(calls.length, 2);

  // The replaced request settles first, because the newer one aborted it.
  calls[0].deliver();
  await flush();

  assert.equal(pending.fetching, true);

  calls[1].deliver();
  await flush();

  assert.equal(pending.fetching, false);
});

test('the selection already held starts nothing and reports nothing in flight', async () => {
  const { pending, calls, selectFilters } = createProvider();
  const selection = { service: 'steam', client: 'all' };

  selectFilters(selection);
  calls[0].deliver();
  await flush();

  selectFilters(selection);

  assert.equal(calls.length, 1);
  assert.equal(pending.fetching, false);
});

test('a filter change asks for no skeleton and is not dropped by the debounce', () => {
  const { calls, selectFilters } = createProvider();

  selectFilters({ service: 'steam', client: 'all' });

  assert.deepEqual(calls[0].options, {
    showLoading: false,
    forceRefresh: true,
    trigger: 'downloadFilterChange'
  });
});
