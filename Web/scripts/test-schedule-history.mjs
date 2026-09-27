import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, liftHookCallback, parseSource } from './transpile-module.mjs';

const componentPath = 'src/components/features/management/schedules/ScheduleHistory.tsx';
const fetchSource = liftHookCallback(componentPath, 'useCallback', 'ApiService.getScheduleHistory');
const changePageSource = liftHookCallback(
  componentPath,
  'useCallback',
  'pageRef.current = nextPage'
);

const defer = () => {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
};

const settle = () => new Promise((resolve) => setImmediate(resolve));

const page = (number, operationId) => ({
  items: [{ id: number, operationId }],
  page: number,
  pageSize: 20,
  totalCount: 60,
  totalPages: 3
});

const start = () => {
  const state = {
    confirmed: null,
    error: null,
    loaded: false,
    expanded: new Set([1]),
    requests: [],
    mountedRef: { current: true },
    pageRef: { current: 1 },
    requestRef: {
      current: {
        running: false,
        pending: false,
        owner: 0,
        controller: null
      }
    }
  };
  const ApiService = {
    getScheduleHistory: (requestedPage, pageSize, signal) => {
      const request = defer();
      request.page = requestedPage;
      request.pageSize = pageSize;
      request.signal = signal;
      signal.addEventListener(
        'abort',
        () => {
          request.aborted = true;
          if (request.ignoreAbort) return;
          const error = new Error('aborted');
          error.name = 'AbortError';
          request.reject(error);
        },
        { once: true }
      );
      state.requests.push(request);
      return request.promise;
    }
  };
  const fetchHistory = bindLifted(fetchSource, {
    requestRef: state.requestRef,
    pageRef: state.pageRef,
    mountedRef: state.mountedRef,
    ApiService,
    SCHEDULE_HISTORY_PAGE_SIZE: 20,
    isAbortError: (error) => error?.name === 'AbortError',
    getErrorMessage: (error) => error.message,
    setConfirmed: (value) => {
      state.confirmed = value;
    },
    setError: (value) => {
      state.error = value;
    },
    setLoaded: (value) => {
      state.loaded = value;
    }
  });
  const changePage = bindLifted(changePageSource, {
    pageRef: state.pageRef,
    requestRef: state.requestRef,
    setExpandedIds: (value) => {
      state.expanded = value;
    },
    fetchHistory
  });
  return { state, fetchHistory, changePage };
};

test('delayed push and reconnect requests coalesce and ignore the cancelled read', async () => {
  const { state, fetchHistory } = start();
  const initial = fetchHistory();
  state.requests[0].resolve(page(1, 'first'));
  await initial;
  assert.equal(state.confirmed.items[0].operationId, 'first');

  const pushed = fetchHistory();
  assert.equal(state.requests.length, 2);
  state.requests[1].ignoreAbort = true;
  await fetchHistory();
  assert.equal(state.requests[1].signal.aborted, true);
  state.requests[1].resolve(page(1, 'stale'));
  await settle();
  assert.equal(state.requests.length, 3);
  assert.equal(state.confirmed.items[0].operationId, 'first');
  assert.equal(state.error, null);

  state.requests[2].resolve(page(1, 'latest'));
  await pushed;
  assert.equal(state.confirmed.items[0].operationId, 'latest');
});

test('page events retain confirmed rows, reset expansion, and publish only the latest page', async () => {
  const { state, fetchHistory, changePage } = start();
  const initial = fetchHistory();
  state.requests[0].resolve(page(1, 'first'));
  await initial;

  changePage(2);
  assert.equal(state.requests.at(-1).page, 2);
  assert.equal(state.confirmed.items[0].operationId, 'first');
  assert.equal(state.expanded.size, 0);
  changePage(3);
  await settle();
  assert.equal(state.requests.at(-1).page, 3);
  state.requests.at(-1).resolve(page(3, 'third'));
  await settle();
  assert.equal(state.confirmed.page, 3);
  assert.equal(state.confirmed.items[0].operationId, 'third');
});

test('a refresh error keeps confirmed rows and the next event clears the error', async () => {
  const { state, fetchHistory } = start();
  const initial = fetchHistory();
  state.requests[0].resolve(page(1, 'confirmed'));
  await initial;

  const failed = fetchHistory();
  state.requests.at(-1).reject(new Error('refresh failed'));
  await failed;
  assert.equal(state.confirmed.items[0].operationId, 'confirmed');
  assert.equal(state.error, 'refresh failed');

  const retried = fetchHistory();
  state.requests.at(-1).resolve(page(1, 'recovered'));
  await retried;
  assert.equal(state.confirmed.items[0].operationId, 'recovered');
  assert.equal(state.error, null);
});

test('the component binds the same refetch to schedule pushes and reconnects', () => {
  const source = parseSource(componentPath, ts.ScriptKind.TSX).getFullText();
  assert.match(source, /on\('SchedulesUpdated', handleSchedulesUpdated\)/);
  assert.match(source, /useReconnectRefetch\(isConnected, fetchHistory\)/);
  assert.match(source, /confirmed && confirmed\.items\.length > 0/);
  assert.match(source, /error && \(/);
});
