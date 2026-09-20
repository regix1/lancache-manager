import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback, compileToUrl } from './transpile-module.mjs';

const { mergePrefillRuns } = await import(
  await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts')
);

const path =
  'src/components/features/management/schedules/scheduled-prefill/useScheduledPrefillContainers.ts';
const arrow = liftHookCallback(path, 'useCallback', 'const nextContainers');
const runServiceSource = liftHookCallback(
  'src/components/features/management/schedules/SchedulesSection.tsx',
  'useCallback',
  'runScheduledPrefillService'
);
const settle = () => new Promise((resolve) => setImmediate(resolve));
const create = () => {
  const calls = [];
  const state = { loading: false, containers: null, error: null };
  const requestRef = { current: null };
  const containersRef = { current: null };
  const revisionRef = { current: 0 };
  const load = bindLifted(arrow, {
    mergePrefillRuns,
    persistentContainersRequestRef: requestRef,
    persistentContainersRef: containersRef,
    persistentContainersRevisionRef: revisionRef,
    ApiService: {
      getPersistentPrefillContainers: (signal) =>
        new Promise((resolve, reject) => {
          calls.push({ signal, resolve, reject });
        })
    },
    setLoadingPersistentContainers: (value) => {
      state.loading = value;
    },
    setPersistentContainers: (value) => {
      state.containers = typeof value === 'function' ? value(state.containers) : value;
      containersRef.current = state.containers;
    },
    setPersistentError: (value) => {
      state.error = value;
    },
    isAbortError: (error) => error.name === 'AbortError',
    getErrorMessage: (error) => error.message
  });
  return { calls, state, ref: requestRef, containersRef, revisionRef, load };
};

test('terminal triggers drain after an older downloading snapshot', async () => {
  const run = create();
  const first = run.load();
  const joined = run.load();
  void run.load();
  assert.equal(run.calls.length, 1);
  run.calls[0].resolve([{ isPrefilling: true }]);
  await settle();
  assert.equal(run.calls.length, 2);
  assert.equal(run.state.containers, null);
  assert.equal(run.state.loading, true);
  run.calls[1].resolve([{ isPrefilling: false }]);
  await Promise.all([first, joined]);
  assert.equal(run.state.containers[0].isPrefilling, false);
  assert.equal(run.state.loading, false);
  assert.equal(run.ref.current, null);
});

test('a trigger during the trailing response drains another pass', async () => {
  const run = create();
  const first = run.load();
  void run.load();
  run.calls[0].resolve([]);
  await settle();
  void run.load();
  run.calls[1].resolve([]);
  await settle();
  assert.equal(run.calls.length, 3);
  run.calls[2].resolve([]);
  await first;
});

test('close and reopen replaces aborted work and ignores its late response and finalizer', async () => {
  const run = create();
  const closed = new AbortController();
  const first = run.load(closed.signal);
  closed.abort();
  const next = run.load(new AbortController().signal);
  assert.equal(run.calls.length, 2);
  run.calls[0].resolve(['old']);
  await first;
  assert.equal(run.state.containers, null);
  assert.equal(run.state.loading, true);
  assert.notEqual(run.ref.current, null);
  run.calls[1].resolve(['new']);
  await next;
  assert.deepEqual(run.state.containers, ['new']);
  assert.equal(run.state.loading, false);
});

test('aborted callers start nothing and failed requests allow a later reconnect refresh', async () => {
  const run = create();
  const closed = new AbortController();
  closed.abort();
  await run.load(closed.signal);
  assert.equal(run.calls.length, 0);
  const failed = run.load();
  run.calls[0].reject(new Error('offline'));
  await failed;
  assert.equal(run.state.loading, false);
  assert.equal(run.ref.current, null);
  const next = run.load();
  run.calls[1].resolve([{ isPrefilling: false }]);
  await next;
  assert.equal(run.state.containers[0].isPrefilling, false);
});

test('a successful empty snapshot stays loaded through refresh failure without a loading pulse', async () => {
  const run = create();
  const initial = run.load();
  assert.equal(run.state.loading, true);
  run.calls[0].resolve([]);
  await initial;
  assert.deepEqual(run.state.containers, []);
  assert.deepEqual(run.containersRef.current, []);
  assert.equal(run.state.loading, false);

  const refresh = run.load();
  assert.equal(run.state.loading, false);
  assert.deepEqual(run.state.containers, []);
  run.calls[1].reject(new Error('offline'));
  await refresh;

  assert.deepEqual(run.state.containers, []);
  assert.equal(run.state.error, 'offline');
  assert.equal(run.state.loading, false);
});

test('a queued refresh runs after the superseded request fails', async () => {
  const run = create();
  const first = run.load();
  const joined = run.load();
  run.calls[0].reject(new Error('superseded failure'));
  await settle();

  assert.equal(run.calls.length, 2);
  assert.equal(run.state.error, null);
  run.calls[1].resolve([{ isPrefilling: false }]);
  await Promise.all([first, joined]);

  assert.equal(run.state.containers[0].isPrefilling, false);
  assert.equal(run.state.error, null);
  assert.equal(run.ref.current, null);
});

const runSavedService = ({ request, notifications, pending, order }) =>
  bindLifted(runServiceSource, {
    sessionStore: {},
    recoverScheduledPrefillEditSession: async (_store, cleanup) => {
      order.push('recover');
      await cleanup({ editSessionId: 'old-edit' });
    },
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY: { Steam: 'steam' },
    ApiService: {
      cleanupPersistentPrefillEditSession: async () => order.push('cleanup'),
      runScheduledPrefillService: async (...args) => {
        order.push(['request', ...args]);
        return request();
      },
      startPersistentPrefill: assert.fail
    },
    markStarting: (key) => pending.add(key),
    clearPending: (key) => pending.delete(key),
    addNotification: (notice) => notifications.push(notice),
    getPersistentPrefillRunOptions: assert.fail,
    recordEditAction: assert.fail,
    getErrorMessage: (error) => error.message,
    t: (key) => key
  });

test('saved-row run dispatches its exact service and schedule only after cleanup', async () => {
  const order = [];
  const notifications = [];
  const pending = new Set();
  const run = runSavedService({
    request: async () => ({ alreadyRunning: false }),
    notifications,
    pending,
    order
  });
  await run('Steam', 'schedule-7');
  assert.deepEqual(order, ['recover', 'cleanup', ['request', 'Steam', 'schedule-7']]);
  assert.deepEqual([...pending], ['Steam:schedule-7']);
  assert.equal(notifications.length, 1);
  assert.equal(notifications[0].status, 'completed');
});

test('successful list retry clears the previous transport error', async () => {
  const run = create();
  const failed = run.load();
  run.calls[0].reject(new Error('offline'));
  await failed;
  assert.equal(run.state.error, 'offline');
  const request = run.load();
  run.calls[1].resolve([{ isAuthenticated: false }]);
  await request;
  assert.equal(run.state.error, null);
  assert.equal(run.state.containers[0].isAuthenticated, false);
});

test('saved-row refusal clears only its pending row and survives successful trailing refresh', async () => {
  const run = create();
  const notifications = [];
  const pendingRows = new Set(['Steam:sibling']);
  const order = [];
  const request = run.load();
  const dispatch = runSavedService({
    request: async () => {
      throw new Error('Log in first');
    },
    notifications,
    pending: pendingRows,
    order
  });
  const refused = dispatch('Steam', 'schedule-7');
  void run.load();
  await settle();
  assert.equal(run.calls.length, 1);
  assert.equal(run.ref.current.again, true);
  run.calls[0].resolve([{ isAuthenticated: true }]);
  await settle();
  assert.equal(run.calls.length, 2);
  assert.equal(run.state.error, null);
  run.calls[1].resolve([{ isAuthenticated: false }]);
  await Promise.all([request, refused]);
  assert.equal(run.state.containers[0].isAuthenticated, false);
  assert.deepEqual([...pendingRows], ['Steam:sibling']);
  assert.equal(notifications.length, 1);
  assert.equal(notifications[0].status, 'failed');
  assert.equal(notifications[0].message, 'Log in first');
  assert.deepEqual(order, ['recover', 'cleanup', ['request', 'Steam', 'schedule-7']]);
});
