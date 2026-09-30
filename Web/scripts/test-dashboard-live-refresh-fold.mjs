import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

const PATH = 'src/contexts/DashboardDataContext/index.tsx';

const makeHarness = () => {
  const calls = [];
  const fetchInProgress = { current: false };
  const liveRefetchPendingRef = { current: false };
  const liveFollowUpQueuedRef = { current: false };
  const currentRequestIdRef = { current: 0 };
  const abortControllerRef = { current: null };
  const lastFetchTime = { current: 0 };
  const isInitialLoad = { current: false };
  let liveFollowUps = 0;

  const ApiService = {
    getDashboardBatch(signal) {
      return new Promise((resolve, reject) => calls.push({ signal, resolve, reject }));
    }
  };
  class ApiError extends Error {}
  const fetchBatch = bindLifted(liftHookCallback(PATH, 'useCallback', 'const thisRequestId'), {
    mockModeRef: { current: false },
    authLoadingRef: { current: false },
    hasAccessRef: { current: true },
    setLoading: () => undefined,
    lastFetchTime,
    fetchInProgress,
    isInitialLoad,
    abortControllerRef,
    currentRequestIdRef,
    selectedEventIdsRef: { current: [] },
    getTimeRangeParamsRef: { current: () => ({ startTime: undefined, endTime: undefined }) },
    downloadFiltersRef: { current: { service: 'all', client: 'all' } },
    buildRangeKey: () => 'live',
    ApiService,
    applyDetectionFromBatch: () => undefined,
    applyDashboardBatchResponse: () => ({
      next: {},
      hadPartialFailure: false,
      failedSectionKeys: []
    }),
    slicesRef: { current: {} },
    appliedRangeKeyRef: { current: null },
    applySlices: () => undefined,
    setError: () => undefined,
    setFailedSectionKeys: () => undefined,
    setUnconfirmedSectionKeys: () => undefined,
    unconfirmedSections: () => [],
    setDataStale: () => undefined,
    isAbortError: (error) => error?.name === 'AbortError',
    ApiError,
    setSelectedEventIdsRef: { current: () => undefined },
    currentTimeRangeRef: { current: 'live' },
    setTimeRangeRef: { current: () => undefined },
    getErrorMessage: (error) => error.message,
    FAILED_BATCH: {},
    i18n: { t: (key) => key },
    setIsRefreshing: () => undefined,
    liveRefetchPendingRef,
    liveFollowUpQueuedRef,
    setLiveFollowUps: (update) => {
      liveFollowUps = update(liveFollowUps);
    }
  });

  const fold = bindLifted(
    liftHookCallback(PATH, 'scheduleLiveRefresh', 'trigger: `signalr:${eventName'),
    {
      fetchInProgress,
      liveRefetchPendingRef,
      liveFollowUpQueuedRef,
      fetchAllData: fetchBatch,
      eventName: 'DownloadsRefresh'
    }
  );

  return {
    calls,
    fetchBatch,
    fetchInProgress,
    fold,
    liveRefetchPendingRef,
    liveFollowUpQueuedRef,
    getLiveFollowUps: () => liveFollowUps
  };
};

const abortError = () => new DOMException('request replaced', 'AbortError');

test('a superseded settle cannot clear the current request and live events fold after it', async () => {
  const harness = makeHarness();
  void harness.fetchBatch({ forceRefresh: true, trigger: 'R0' });
  void harness.fetchBatch({ forceRefresh: true, trigger: 'R1' });
  assert.equal(harness.calls.length, 2);
  assert.equal(harness.calls[0].signal.aborted, true, 'R1 replaces R0');

  harness.calls[0].reject(abortError());
  await delay(0);
  assert.equal(harness.fetchInProgress.current, true, 'R0 cannot clear the R1 in-flight fact');

  harness.fold();
  assert.equal(harness.calls.length, 2, 'the live event folds instead of aborting R1');
  assert.equal(harness.liveRefetchPendingRef.current, true);
  await harness.fetchBatch({ trigger: 'disconnected-poll' });
  assert.equal(harness.calls.length, 2, 'a plain poll call starts nothing while R1 runs');
  assert.equal(harness.calls[1].signal.aborted, false);

  harness.calls[1].reject(abortError());
  await delay(0);
  assert.equal(harness.getLiveFollowUps(), 1);
  assert.equal(harness.liveFollowUpQueuedRef.current, true);
});

test('the follow-up effect starts one request and an idle event fetches immediately', async () => {
  const harness = makeHarness();
  void harness.fetchBatch({ forceRefresh: true, trigger: 'R0' });
  harness.fold();
  harness.calls[0].reject(abortError());
  await delay(0);

  const runFollowUp = () =>
    bindLifted(liftHookCallback(PATH, 'useEffect', 'liveFollowUps === 0'), {
      liveFollowUps: harness.getLiveFollowUps(),
      liveFollowUpQueuedRef: harness.liveFollowUpQueuedRef,
      currentTimeRangeRef: { current: 'live' },
      fetchAllData: harness.fetchBatch
    })();

  harness.fold();
  assert.equal(
    harness.calls.length,
    1,
    'an event before the effect folds into the queued follow-up'
  );
  runFollowUp();
  assert.equal(harness.calls.length, 2, 'one queued follow-up starts');
  assert.equal(harness.calls[1].signal.aborted, false);

  harness.calls[1].reject(abortError());
  await delay(0);
  assert.equal(harness.getLiveFollowUps(), 2, 'the interleaved event queues one later request');
  runFollowUp();
  assert.equal(harness.calls.length, 3);
  harness.calls[2].reject(abortError());
  await delay(0);

  harness.fold();
  assert.equal(harness.calls.length, 4, 'an idle event starts its forced request immediately');
  harness.calls[3].reject(abortError());
  await delay(0);
});

test('the disconnected poll and terminal reset paths preserve their source contracts', () => {
  const source = readFileSync(new URL(`../${PATH}`, import.meta.url), 'utf8');
  const poll = source.slice(
    source.indexOf('// While the hub is down no refresh event arrives'),
    source.indexOf('// Custom date changes')
  );
  assert.match(poll, /signalR\.isConnected/);
  assert.match(poll, /DISCONNECTED_POLL_MS/);
  assert.match(poll, /fetchAllData\(\{ trigger: 'disconnected-poll' \}\)/);
  assert.doesNotMatch(poll, /currentTimeRangeRef/);
  assert.match(poll, /clearInterval\(interval\)/);
  assert.match(source, /status === 'completed' \|\| status === 'failed'/);
  assert.match(
    source,
    /fetchAllData\(\{ forceRefresh: true, trigger: 'signalr:DatabaseResetCompleted' \}\)/
  );
});
