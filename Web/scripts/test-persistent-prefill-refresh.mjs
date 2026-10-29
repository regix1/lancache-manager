import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow, liftHookCallback, compileToUrl } from './transpile-module.mjs';

const { mergePrefillRuns, dropUnlistedFinishedRuns } = await import(
  await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts')
);
const { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } = await import(
  await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/constants.ts'
  )
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
  const load = bindLifted(arrow, {
    mergePrefillRuns,
    dropUnlistedFinishedRuns,
    persistentContainersRequestRef: requestRef,
    persistentContainersRef: containersRef,
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
  return { calls, state, ref: requestRef, containersRef, load };
};

test('terminal triggers drain after an older downloading snapshot', async () => {
  const run = create();
  const first = run.load();
  const joined = run.load();
  void run.load();
  assert.equal(run.calls.length, 1);
  run.calls[0].resolve([{ activeRunCount: 1 }]);
  await settle();
  assert.equal(run.calls.length, 2);
  assert.equal(run.state.containers[0].activeRunCount, 1);
  assert.equal(run.state.loading, true);
  run.calls[1].resolve([{ activeRunCount: 0 }]);
  await Promise.all([first, joined]);
  assert.equal(run.state.containers[0].activeRunCount, 0);
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
  await settle();
  assert.equal(run.calls.length, 3);
  assert.equal(run.state.loading, false);
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
  run.calls[1].resolve([{ activeRunCount: 0 }]);
  await next;
  assert.equal(run.state.containers[0].activeRunCount, 0);
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
  run.calls[1].resolve([{ activeRunCount: 0 }]);
  await Promise.all([first, joined]);

  assert.equal(run.state.containers[0].activeRunCount, 0);
  assert.equal(run.state.error, null);
  assert.equal(run.ref.current, null);
});

const runSavedService = ({ request, notifications, pending, order }) =>
  bindLifted(runServiceSource, {
    ApiService: {
      runScheduledPrefillService: async (...args) => {
        order.push(['request', ...args]);
        return request();
      }
    },
    markStarting: (key) => pending.add(key),
    clearPending: (key) => pending.delete(key),
    notifyError: (message, error) => notifications.push({ message, error }),
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY,
    t: (key, options) => (options ? `${key}(${options.service})` : key)
  });

test('saved-row run dispatches its exact service and schedule', async () => {
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
  assert.deepEqual(order, [['request', 'Steam', 'schedule-7']]);
  assert.deepEqual([...pending], ['Steam:schedule-7']);
  assert.deepEqual(notifications, []);
});

test('an already-running saved row draws no popup and stays pending', async () => {
  const notifications = [];
  const pending = new Set();
  const run = runSavedService({
    request: async () => ({ alreadyRunning: true }),
    notifications,
    pending,
    order: []
  });
  await run('Steam', 'schedule-7');
  // The platform's own card answers; the row waits for server truth.
  assert.deepEqual(notifications, []);
  assert.deepEqual([...pending], ['Steam:schedule-7']);
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
  const refusal = new Error('Log in first');
  const request = run.load();
  const dispatch = runSavedService({
    request: async () => {
      throw refusal;
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
  // One popup: the title names the platform, and the refusal is its second line.
  assert.deepEqual(notifications, [
    {
      message:
        'management.schedules.runNowFailed(management.schedules.services.scheduledPrefill.config.services.steam)',
      error: refusal
    }
  ]);
  assert.deepEqual(order, [['request', 'Steam', 'schedule-7']]);
});

test('a container refresh drops the finished runs the server no longer lists and keeps running ones', async () => {
  const runOf = (runId, state) => ({
    runId,
    sessionId: 'session',
    daemonInstanceId: 'daemon',
    snapshot: { sequence: 3, startedAt: `2026-09-12T10:00:0${runId}Z`, state },
    progress: null,
    cancelRequested: false
  });
  const run = create();
  run.state.containers = [
    { sessionId: 'session', runs: [runOf('1', 'failed'), runOf('2', 'downloading')] }
  ];
  const deleted = run.load();
  run.calls[0].resolve([{ sessionId: 'session', runs: [] }]);
  await deleted;
  assert.deepEqual(
    run.state.containers[0].runs.map((entry) => entry.runId),
    ['2']
  );

  run.state.containers = [{ sessionId: 'session', runs: [runOf('1', 'completed')] }];
  const kept = run.load();
  run.calls[1].resolve([{ sessionId: 'session', runs: [runOf('1', 'completed')] }]);
  await kept;
  assert.deepEqual(
    run.state.containers[0].runs.map((entry) => entry.runId),
    ['1']
  );
});

test('a refresh asked while a slow list request is out still shows that answer', async () => {
  const run = create();
  const first = run.load();
  assert.equal(run.state.loading, true);
  // Each list read makes the server push a session update, which asks for another load.
  void run.load();
  run.calls[0].resolve([{ sessionId: 'session', runs: [] }]);
  await settle();
  assert.deepEqual(run.state.containers, [{ sessionId: 'session', runs: [] }]);
  assert.equal(run.calls.length, 2);
  void run.load();
  run.calls[1].resolve([{ sessionId: 'session', activeRunCount: 1, runs: [] }]);
  await settle();
  assert.equal(run.state.containers[0].activeRunCount, 1);
  assert.equal(run.calls.length, 3);
  run.calls[2].resolve([{ sessionId: 'session', activeRunCount: 0, runs: [] }]);
  await first;
  await settle();
  assert.equal(run.state.containers[0].activeRunCount, 0);
  assert.equal(run.state.loading, false);
});

test('a Stop settles while every slow list answer queues the next read', async () => {
  const actSource = liftConstArrow(path, 'act');
  const pushSource = liftHookCallback(
    'src/components/features/management/schedules/scheduled-prefill/usePersistentPrefillContainerSignalR.ts',
    'useEffect',
    'scheduleRefresh'
  );
  // Live refresh interval 500 ms, list answers 600 ms, and each read makes the server push a session
  // update 10 ms in, so the list loop never runs dry.
  const interval = 500;
  const answerMs = 600;
  let clock = 1_000_000;
  const timers = [];
  let nextTimerId = 1;
  const setTimer = (fn, ms) => {
    const id = nextTimerId++;
    timers.push({ id, at: clock + ms, fn });
    return id;
  };
  const clearTimer = (id) => {
    const index = timers.findIndex((timer) => timer.id === id);
    if (index >= 0) timers.splice(index, 1);
  };
  const handlers = [];
  const containersRef = { current: null };
  const load = bindLifted(arrow, {
    mergePrefillRuns,
    dropUnlistedFinishedRuns,
    persistentContainersRequestRef: { current: null },
    persistentContainersRef: containersRef,
    ApiService: {
      getPersistentPrefillContainers: () => {
        setTimer(() => handlers.forEach((handler) => handler({ isPersistent: true })), 10);
        return new Promise((resolve) =>
          setTimer(() => resolve([{ sessionId: 'session', runs: [] }]), answerMs)
        );
      }
    },
    setLoadingPersistentContainers: () => undefined,
    setPersistentContainers: (update) => {
      containersRef.current = update(containersRef.current);
    },
    setPersistentError: () => undefined,
    isAbortError: () => false,
    getErrorMessage: String
  });
  const actions = { current: {} };
  const attempts = { current: new Map() };
  const act = bindLifted(actSource, {
    privateAvailabilityIdentity: 'me',
    privateAvailabilityIdentityRef: { current: 'me' },
    attempts,
    view: { current: 'view' },
    persistentContainerByServiceRef: { current: new Map([['steam', { sessionId: 'session' }]]) },
    getPersistentServiceId: () => 'steam',
    setActions: (update) => {
      actions.current = update(actions.current);
    },
    setErrors: () => undefined,
    setErrorActions: () => undefined,
    loadPersistentContainers: load,
    getErrorMessage: String
  });
  bindLifted(pushSource, {
    enabled: true,
    getRefreshIntervalRef: { current: () => interval },
    lastRefreshRef: { current: 0 },
    debounceTimerRef: { current: null },
    onRefreshRef: { current: () => void load() },
    PERSISTENT_PREFILL_CONTAINER_SIGNALR_EVENTS: ['SteamDaemonSessionUpdated'],
    on: (_name, handler) => handlers.push(handler),
    off: () => undefined,
    Date: { now: () => clock },
    setTimeout: setTimer,
    clearTimeout: clearTimer
  })();
  const runUntil = async (until) => {
    while (timers.length > 0) {
      timers.sort((left, right) => left.at - right.at);
      if (timers[0].at > until) break;
      const timer = timers.shift();
      clock = timer.at;
      timer.fn();
      for (let i = 0; i < 30; i++) await Promise.resolve();
    }
    clock = until;
  };

  void load();
  await runUntil(clock + 3000);
  let stopSettled = false;
  let stops = 0;
  void act('steam', 'stop', async () => {
    stops += 1;
  }).then(() => {
    stopSettled = true;
  });
  await runUntil(clock + 10_000);

  assert.equal(stops, 1);
  assert.equal(stopSettled, true);
  assert.equal(actions.current.steam, undefined);
  assert.equal(attempts.current.has('steam'), false);

  void act('steam', 'stop', async () => {
    stops += 1;
  });
  await runUntil(clock + 1000);
  assert.equal(stops, 2);
});
