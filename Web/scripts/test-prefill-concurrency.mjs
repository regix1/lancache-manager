import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl, bindLifted, liftHookCallback, liftConstArrow } from './transpile-module.mjs';
import { moduleUrl } from './transpile-module.mjs';

const {
  mergePrefillRuns,
  applyPrefillRunProgress,
  getPrefillRunProgress,
  canStartPrefill,
  prefillRunKey,
  retainPrefillCompletions,
  isPrefillRunActive
} = await import(await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts'));
const features = [
  'concurrentPrefill',
  'operationProgress',
  'targetedCancel',
  'inlineSelection',
  'activeOperations'
];

test('completion identity retention expires at 24 hours and caps at 256 entries', () => {
  const now = 200000000;
  const entries = Array.from({ length: 260 }, (_, index) => ({
    key: String(index),
    completedAt: now - index,
    dismissed: index === 1
  }));
  const kept = retainPrefillCompletions(
    [...entries, { key: 'expired', completedAt: now - 86400000, dismissed: true }],
    now
  );
  assert.equal(kept.length, 256);
  assert.equal(
    kept.some((entry) => entry.key === 'expired'),
    false
  );
  assert.equal(kept.find((entry) => entry.key === '1').dismissed, true);
  assert.deepEqual(retainPrefillCompletions(null, now), []);
  assert.deepEqual(
    retainPrefillCompletions([null, {}, { key: 'future', completedAt: now + 1 }], now),
    []
  );
  assert.equal(prefillRunKey({ sessionId: 's1', daemonInstanceId: 'd1', runId: 'r1' }), 's1:d1:r1');
  assert.notEqual(
    prefillRunKey({ sessionId: 's1', daemonInstanceId: 'd1', runId: 'r1' }),
    prefillRunKey({ sessionId: 's1', daemonInstanceId: 'd2', runId: 'r1' })
  );
});
const run = (id, sequence = 1, state = 'downloading') => ({
  runId: id,
  sessionId: 'session',
  daemonInstanceId: 'daemon',
  options: {
    appIds: [id],
    selection: 'selected',
    operatingSystems: ['windows'],
    force: false,
    maxConcurrency: 4
  },
  snapshot: {
    operationId: id,
    daemonInstanceId: 'daemon',
    sequence,
    startedAt: `2026-09-12T10:00:0${id}Z`,
    updatedAt: '2026-09-12T10:01:00Z',
    state,
    totalApps: 3,
    completedApps: 0,
    cachedApps: 0,
    failedApps: 0,
    cancelledApps: 0,
    skippedApps: 0,
    bytesTransferred: 12,
    selectionResolved: true
  },
  recovering: false,
  cancelRequested: false,
  historyIncomplete: false
});
const progress = (id, sequence, state = 'downloading') => ({
  operationId: id,
  daemonInstanceId: 'daemon',
  sequence,
  state,
  currentAppId: id,
  currentAppName: `Game ${id}`,
  bytesDownloaded: 23,
  totalBytes: 100,
  totalBytesTransferred: 123,
  percentComplete: 23,
  bytesPerSecond: 10,
  elapsedSeconds: 3,
  totalApps: 3,
  updatedApps: 1,
  alreadyUpToDate: 0,
  failedApps: 0,
  skippedApps: 1,
  cancelledApps: 0
});

test('interleaved runs preserve sibling objects, counts and actual bytes', () => {
  const initial = [run('1'), run('2'), run('3')];
  const next = applyPrefillRunProgress(initial, progress('2', 2));
  assert.equal(next[0], initial[0]);
  assert.equal(next[2], initial[2]);
  assert.equal(next[1].snapshot.bytesTransferred, 123);
  assert.equal(getPrefillRunProgress(next[1]).skippedApps, 1);
  assert.equal(getPrefillRunProgress(next[1]).currentAppId, '2');
});

test('late progress, wrong daemon, duplicate terminals and old recovery cannot revive a run', () => {
  const initial = [run('1', 8, 'cancelled'), run('2', 4), run('3', 1)];
  for (const event of [
    progress('1', 9),
    progress('2', 3),
    { ...progress('2', 9), daemonInstanceId: 'old' }
  ]) {
    const next = applyPrefillRunProgress(initial, event);
    assert.deepEqual(next, initial);
    assert.equal(next[0], initial[0]);
    assert.equal(next[1], initial[1]);
  }
  const recovered = mergePrefillRuns(initial, [run('1', 9), run('2', 2)]);
  assert.equal(recovered[0], initial[0]);
  assert.equal(recovered[1], initial[1]);
  assert.equal(recovered[2], initial[2]);
});

test('recovery retains original identity and pending cancellation through equal-sequence snapshots', () => {
  const pending = { ...run('1', 4), cancelRequested: true, recovering: true };
  const next = mergePrefillRuns([pending], [run('1', 4)])[0];
  assert.equal(next.runId, '1');
  assert.equal(next.snapshot.startedAt, pending.snapshot.startedAt);
  assert.equal(next.cancelRequested, true);
  assert.equal(next.recovering, false);
  const terminal = mergePrefillRuns(
    [next],
    [{ ...run('1', 5, 'failed'), historyIncomplete: true }]
  )[0];
  assert.equal(terminal.cancelRequested, false);
  assert.equal(terminal.historyIncomplete, true);
  assert.equal(isPrefillRunActive(terminal), false);
});

test('capacity uses the shared three-run inventory and preserves exclusive legacy admission', () => {
  const runs = [run('1'), run('2'), run('3')];
  assert.equal(canStartPrefill({ features, runs, maxConcurrentRuns: 4 }), true);
  assert.equal(canStartPrefill({ features, runs, maxConcurrentRuns: 3 }), false);
  assert.equal(canStartPrefill({ features, runs, maxConcurrentRuns: 4, recovering: true }), false);
  assert.equal(
    canStartPrefill({ features, runs: [{ ...runs[0], recovering: true }], maxConcurrentRuns: 4 }),
    false
  );
  assert.equal(canStartPrefill({ isPrefilling: true }), false);
  assert.equal(canStartPrefill({ isPrefilling: false }), true);
  assert.equal(
    canStartPrefill({ features: ['concurrentPrefill'], isPrefilling: true, maxConcurrentRuns: 4 }),
    false
  );
});

test('preparing and recovered terminals never manufacture an item or successful transferred bytes', () => {
  const preparing = run('1', 1, 'preparing');
  assert.equal(getPrefillRunProgress(preparing).currentAppId, '');
  const skipped = {
    ...preparing,
    snapshot: { ...preparing.snapshot, state: 'completed', skippedApps: 3, bytesTransferred: 0 }
  };
  assert.equal(getPrefillRunProgress(skipped).updatedApps, 0);
  assert.equal(getPrefillRunProgress(skipped).skippedApps, 3);
  const recovering = { ...run('2'), recovering: true, progress: progress('2', 1) };
  assert.equal(getPrefillRunProgress(recovering).state, 'reconnecting');
  assert.equal(getPrefillRunProgress(recovering).bytesPerSecond, 0);
  const downloading = applyPrefillRunProgress([run('1')], progress('1', 2));
  const nextItem = applyPrefillRunProgress(downloading, {
    ...progress('1', 3, 'preparing'),
    currentAppId: '',
    currentAppName: undefined
  });
  assert.equal(getPrefillRunProgress(nextItem[0]).currentAppId, '');
  assert.equal(nextItem[0].snapshot.currentItem, null);
  const restored = {
    ...run('1'),
    snapshot: {
      ...run('1').snapshot,
      currentItem: {
        appId: '1',
        state: 'downloading',
        sequence: 1,
        bytesTransferred: 30,
        totalBytes: 100
      }
    }
  };
  assert.equal(getPrefillRunProgress(restored).percentComplete, 30);
});

test('targeted cancel waits for server terminal without affecting siblings or arming legacy watchdog', async () => {
  let runs = [run('1'), run('2'), run('3')];
  const runsRef = { current: runs };
  const cancelRunsRef = { current: new Set() };
  const calls = [];
  let release;
  const pending = new Promise((resolve) => {
    release = resolve;
  });
  const cancel = bindLifted(
    liftHookCallback(
      'src/components/features/prefill/hooks/usePrefillSignalR.ts',
      'useCallback',
      'if (runId)'
    ),
    {
      hubConnection: {
        current: {
          invoke: async (...args) => {
            calls.push(args);
            await pending;
          }
        }
      },
      sessionRef: { current: { id: 'session' } },
      runsRef,
      cancelRunsRef,
      isPrefillRunActive,
      updateRuns: (next) => {
        runs = mergePrefillRuns(runs, next);
        runsRef.current = runs;
      },
      setRunErrors: () => undefined,
      refreshRuns: async () => undefined,
      t: (key) => key
    }
  );
  const sibling = runs[1];
  const request = cancel('1');
  await cancel('1');
  assert.equal(runs[0].cancelRequested, true);
  assert.equal(runs[1], sibling);
  assert.deepEqual(calls, [['CancelPrefillRunAsync', 'session', '1']]);
  release();
  await request;
  assert.equal(runs[0].cancelRequested, true);
  assert.equal(runs[1], sibling);
});

test('persistent cancellation targets the selected run while another cancel is pending', async () => {
  let pendingIds = [];
  const calls = [];
  const releases = [];
  const container = { sessionId: 'session', isRunning: true, runs: [run('1'), run('2'), run('3')] };
  const cancel = bindLifted(
    liftConstArrow(
      'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
      'handleCancelPersistentDownload'
    ),
    {
      getPersistentServiceId: () => 'Steam',
      persistentContainerByService: new Map([['Steam', container]]),
      cancellingRunsRef: { current: new Set() },
      isPrefillRunActive,
      setCancellingRunIds: (ids) => {
        pendingIds = ids;
      },
      setRunErrors: () => undefined,
      setPersistentContainers: (update) => update([container]),
      ApiService: {
        cancelPersistentPrefill: (...args) =>
          new Promise((resolve) => {
            calls.push(args);
            releases.push(resolve);
          })
      },
      loadPersistentContainers: async () => undefined
    }
  );
  const first = cancel('steam', '1');
  const second = cancel('steam', '2');
  assert.deepEqual(pendingIds, ['1', '2']);
  assert.deepEqual(calls, [
    ['Steam', 'session', '1'],
    ['Steam', 'session', '2']
  ]);
  releases[0]();
  await first;
  assert.deepEqual(pendingIds, ['2']);
  releases[1]();
  await second;
  assert.deepEqual(pendingIds, []);
});

test('terminal completion storage deduplicates identities and dismisses only the selected run', () => {
  let completions = [];
  const completionsRef = { current: [] };
  const storage = new Map();
  const bindings = {
    completionsRef,
    prefillRunKey,
    retainPrefillCompletions,
    isPrefillRunActive,
    mergePrefillRuns,
    RUN_COMPLETIONS_KEY: 'runs',
    sessionStore: { setJSON: (key, value) => storage.set(key, structuredClone(value)) },
    setRunCompletions: (update) => {
      completions = update(completions);
    }
  };
  const source = 'src/contexts/PrefillContext.tsx';
  const record = bindLifted(
    liftHookCallback(source, 'useCallback', 'if (isPrefillRunActive(run))'),
    bindings
  );
  const dismiss = bindLifted(liftHookCallback(source, 'useCallback', 'dismissed: true'), bindings);
  const isDismissed = bindLifted(
    liftHookCallback(source, 'useCallback', 'entry.key === prefillRunKey(run) && entry.dismissed'),
    bindings
  );
  const first = run('1', 3, 'completed');
  const second = run('2', 4, 'failed');
  record(first);
  record(second);
  record(structuredClone(first));
  assert.equal(completions.length, 2);
  assert.equal(storage.get('runs').length, 2);
  dismiss(first);
  assert.deepEqual(
    completions.map((entry) => entry.runId),
    ['2']
  );
  assert.equal(isDismissed(first), true);
  assert.equal(isDismissed(second), false);
  record(first);
  assert.deepEqual(
    completions.map((entry) => entry.runId),
    ['2']
  );
  assert.equal(storage.get('runs').length, 2);
});

test('all service handlers route keyed progress and terminals without clearing sibling legacy state', async () => {
  const constants = await compileToUrl(
    '../src/components/features/prefill/hooks/prefillConstants.ts'
  );
  const { getEventName } = await import(constants);
  const { registerPrefillEventHandlers } = await import(
    await compileToUrl('../src/components/features/prefill/hooks/usePrefillEventHandlers.ts', {
      '@utils/formatters': moduleUrl('export const formatBytes = String;'),
      '../types': moduleUrl('export const formatDurationFromSeconds = String;'),
      '@/i18n': moduleUrl('export default {t: (key) => key};'),
      './prefillConstants': constants,
      './prefillTypes': await compileToUrl(
        '../src/components/features/prefill/hooks/prefillTypes.ts'
      ),
      '@utils/constants': moduleUrl('export const STORAGE_KEYS = {};'),
      '@utils/storage': moduleUrl('export const sessionStore = {};')
    })
  );
  for (const serviceId of ['steam', 'epic', 'xbox', 'battlenet', 'riot']) {
    const handlers = new Map();
    const routed = [];
    let refreshes = 0;
    const connection = {
      on: (name, fn) => handlers.set(name, fn),
      onreconnecting: () => undefined,
      onreconnected: () => undefined
    };
    registerPrefillEventHandlers(connection, {
      serviceId,
      sessionRef: { current: { id: 'session' } },
      applyRunProgress: (event) => routed.push(event),
      rehydratePrefillProgress: async () => {
        refreshes++;
      },
      setIsPrefillActive: () => {
        throw new Error('Sibling activity cleared');
      },
      setPrefillProgress: () => {
        throw new Error('Sibling progress cleared');
      }
    });
    const onProgress = handlers.get(getEventName('PrefillProgress', serviceId));
    onProgress({ sessionId: 'session', progress: progress('1', 3) });
    onProgress({ sessionId: 'another', progress: progress('2', 4) });
    assert.deepEqual(
      routed.map((event) => event.operationId),
      ['1']
    );
    handlers.get(getEventName('PrefillStateChanged', serviceId))({
      sessionId: 'session',
      operationId: '1',
      daemonInstanceId: 'daemon',
      state: 'completed'
    });
    assert.equal(refreshes, 1);
  }
});
