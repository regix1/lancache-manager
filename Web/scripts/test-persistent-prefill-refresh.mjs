import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback, liftConstArrow, compileToUrl } from './transpile-module.mjs';

const { canStartPrefill, mergePrefillRuns } = await import(
  await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts')
);

const path =
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx';
const arrow = liftHookCallback(path, 'useCallback', 'const nextContainers');
const settle = () => new Promise((resolve) => setImmediate(resolve));
const create = () => {
  const calls = [];
  const state = { loading: false, containers: null, error: null };
  const ref = { current: null };
  const load = bindLifted(arrow, {
    mergePrefillRuns,
    persistentContainersRequestRef: ref,
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
    },
    setPersistentError: (value) => {
      state.error = value;
    },
    isAbortError: (error) => error.name === 'AbortError',
    getErrorMessage: (error) => error.message
  });
  return { calls, state, ref, load };
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

test('download rejection refreshes before restoring the action error', async () => {
  let error = null;
  let action = null;
  const order = [];
  const download = bindLifted(liftConstArrow(path, 'handlePersistentDownload'), {
    config: { steam: { schedules: [{ id: 'schedule', enabled: true }] } },
    canStartPrefill,
    getPersistentServiceId: () => 'Steam',
    persistentContainerByService: new Map([
      ['Steam', { isRunning: true, isAuthenticated: true, sessionId: 'session' }]
    ]),
    isScheduledPrefillAnonymousService: () => false,
    setPersistentAction: (value) => {
      action = value;
    },
    setPersistentError: (value) => {
      error = value;
    },
    recordEditAction: () => ({ editSession: { editSessionId: 'edit' }, editActionId: 'action' }),
    getPersistentPrefillRunOptions: () => ({}),
    ApiService: {
      startPersistentPrefill: async () => {
        throw new Error('Log in first');
      }
    },
    loadPersistentContainers: async () => {
      order.push('refresh');
      error = 'refresh failed';
    },
    getErrorMessage: (value) => value.message
  });
  await download('steam', 'schedule');
  assert.deepEqual(order, ['refresh']);
  assert.equal(error, 'Log in first');
  assert.equal(action, null);
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

test('download rejection joins a successful refresh before restoring its error', async () => {
  const run = create();
  let action = null;
  const request = run.load();
  const download = bindLifted(liftConstArrow(path, 'handlePersistentDownload'), {
    config: { steam: { schedules: [{ id: 'schedule', enabled: true }] } },
    canStartPrefill,
    getPersistentServiceId: () => 'Steam',
    persistentContainerByService: new Map([
      ['Steam', { isRunning: true, isAuthenticated: true, sessionId: 'session' }]
    ]),
    isScheduledPrefillAnonymousService: () => false,
    setPersistentAction: (value) => {
      action = value;
    },
    setPersistentError: (value) => {
      run.state.error = value;
    },
    recordEditAction: () => ({ editSession: { editSessionId: 'edit' }, editActionId: 'action' }),
    getPersistentPrefillRunOptions: () => ({}),
    ApiService: {
      startPersistentPrefill: async () => {
        throw new Error('Log in first');
      }
    },
    loadPersistentContainers: run.load,
    getErrorMessage: (value) => value.message
  });
  const pending = download('steam', 'schedule');
  await settle();
  assert.equal(run.calls.length, 1);
  assert.equal(run.ref.current.again, true);
  run.calls[0].resolve([{ isAuthenticated: true }]);
  await settle();
  assert.equal(run.calls.length, 2);
  assert.equal(run.state.error, null);
  assert.notEqual(action, null);
  run.calls[1].resolve([{ isAuthenticated: false }]);
  await Promise.all([request, pending]);
  assert.equal(run.state.containers[0].isAuthenticated, false);
  assert.equal(run.state.error, 'Log in first');
  assert.equal(action, null);
});
