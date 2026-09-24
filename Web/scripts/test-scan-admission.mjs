import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  loadNotificationModules,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * A scan button is busy while the server's run list says its run waits, runs or is cancelling,
 * and for the length of its own start request - nothing else holds it. These cases build the run
 * list with the real run store, feed it to the real `useOperationBusy`, and drive the scan
 * handlers and reconnect callbacks lifted from the components that ship them.
 */

const { isConfirmedScanRefusalStatus } = await import(
  await compileToUrl('../src/components/features/management/game-detection/scanAdmission.ts')
);

const { applyRun, createRunStoreState, deriveRuns } = await loadNotificationModules(
  moduleUrl('export default { t: (key) => key, exists: () => true };')
);

// Runs the factory on every call and ignores the dependency array, so these cases check what the
// hook computes, never whether the memo is keyed on the right values.
const reactStubUrl = moduleUrl('export const useMemo = (fn) => fn();');
const notificationsStubUrl = moduleUrl(`
  export const box = { runs: [] };
  export const useNotifications = () => ({ runs: box.runs });
`);
const { useOperationBusy } = await import(
  await compileToUrl('../src/hooks/useOperationBusy.ts', {
    react: reactStubUrl,
    '@contexts/notifications/useNotifications': notificationsStubUrl
  })
);
const { box } = await import(notificationsStubUrl);

const GAME_DETECTOR = 'src/components/features/management/game-detection/GameCacheDetector.tsx';
const STORAGE = 'src/components/features/management/sections/StorageSection.tsx';
const DATASOURCES = 'src/components/features/management/datasources/DatasourcesInfo.tsx';

let revision = 0;
const row = (operationId, fields = {}) => ({
  operationId,
  operationType: 'evictionScan',
  name: 'Eviction Scan',
  status: 'running',
  visibility: 'card',
  percentComplete: 0,
  message: 'signalr.evictionScan.scanning',
  startedAt: new Date(Date.UTC(2026, 8, 22, 10, 0, 0)).toISOString(),
  revision: ++revision,
  ...fields
});

/** The live runs the provider would hand the pages after these rows arrived. */
const runsAfter = (...rows) => {
  let state = createRunStoreState();
  for (const value of rows) {
    state = applyRun(state, value, {
      keepSuccessVisible: false,
      localCards: [],
      pushed: true
    }).next;
  }
  return deriveRuns(state);
};

/** The options object a component passes to `useOperationBusy` for `variableName`, as shipped. */
const busyOptions = (relativePath, variableName) => {
  const source = parseSource(relativePath, typescript.ScriptKind.TSX);
  const declaration = findSoleNode(
    source,
    `${variableName} declaration`,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(source) === variableName &&
      node.initializer !== undefined &&
      typescript.isCallExpression(node.initializer)
  );
  return bindLifted(`() => (${declaration.initializer.arguments[0].getText(source)})`, {})();
};

/** The callback a component hands `useReconnectRefetch` (its second argument), as shipped. */
const reconnectCallback = (relativePath) => {
  const source = parseSource(relativePath, typescript.ScriptKind.TSX);
  const call = findSoleNode(
    source,
    'useReconnectRefetch call',
    (node) =>
      typescript.isCallExpression(node) && node.expression.getText(source) === 'useReconnectRefetch'
  );
  return call.arguments[1].getText(source);
};

const busyFor = (runs, options) => {
  box.runs = runs;
  return useOperationBusy(options);
};

test('only a 400 is a confirmed scan refusal', () => {
  assert.equal(isConfirmedScanRefusalStatus(400), true);
  assert.equal(isConfirmedScanRefusalStatus(500), false);
  assert.equal(isConfirmedScanRefusalStatus(undefined), false);
});

test("an eviction scan's folded detection phase never marks game detection busy", () => {
  const runs = runsAfter(
    row('E'),
    row('D', { operationType: 'gameDetection', name: 'Game Detection', parentOperationId: 'E' })
  );
  const detectionBusy = busyOptions(GAME_DETECTOR, 'isDetectionFromNotification');
  const evictionBusy = busyOptions(STORAGE, 'isEvictionScanNotificationRunning');

  assert.deepEqual(
    runs.map((run) => run.type),
    ['eviction_scan'],
    'the child phase is folded under its scan'
  );
  assert.equal(busyFor(runs, detectionBusy), false);
  assert.equal(busyFor(runs, evictionBusy), true);
});

test('a Hidden game detection run keeps the scan buttons busy though it draws no card', () => {
  const runs = runsAfter(
    row('G', { operationType: 'gameDetection', name: 'Game Detection', visibility: 'hidden' })
  );
  assert.equal(busyFor(runs, busyOptions(GAME_DETECTOR, 'isDetectionFromNotification')), true);
});

test('a cancelling eviction scan keeps the Storage Scan button busy until its run ends', () => {
  const options = busyOptions(STORAGE, 'isEvictionScanNotificationRunning');
  assert.equal(busyFor(runsAfter(row('E', { status: 'cancelling' })), options), true);
  assert.equal(
    busyFor(
      runsAfter(row('E', { status: 'cancelling' }), row('E', { status: 'cancelled' })),
      options
    ),
    false,
    'the ended run leaves the list'
  );
});

test('a cancelling game detection run keeps the detector busy', () => {
  const runs = runsAfter(
    row('G', { operationType: 'gameDetection', name: 'Game Detection', status: 'cancelling' })
  );
  assert.equal(busyFor(runs, busyOptions(GAME_DETECTOR, 'isDetectionFromNotification')), true);
});

test('the detector reloads its results on reconnect and holds nothing', () => {
  const calls = [];
  const syncCachedDetection = async (...args) => {
    calls.push(args);
  };
  const loadErrors = [];
  // Every free name the callback reads must be bound here; a hold setter it still called would
  // throw a ReferenceError.
  const bindings = {
    mockMode: false,
    syncCachedDetection,
    setLoadError: (value) => loadErrors.push(value),
    getErrorMessage: (error) => `reason: ${error.message}`
  };
  bindLifted(reconnectCallback(GAME_DETECTOR), bindings)();
  assert.equal(calls.length, 1);
  const [errorContext, options] = calls[0];
  assert.equal(errorContext, 'Failed to refresh cached results after reconnect');
  assert.equal(options.invalidateImages, true);
  options.onError(new Error('offline'));
  assert.deepEqual(loadErrors, ['reason: offline'], 'a failed reload shows the section error');

  bindLifted(reconnectCallback(GAME_DETECTOR), { ...bindings, mockMode: true })();
  assert.equal(calls.length, 1, 'mock mode never pulls real cache data');
});

test('the Storage page reloads its lists and eviction settings on reconnect and holds nothing', () => {
  const reloads = { evictedItems: 0, orphanedDownloads: 0, evictionSettings: 0 };
  const bindings = {
    isAnyEvictedRemovalRunning: false,
    fetchEvictedItems: async () => {
      reloads.evictedItems += 1;
    },
    fetchOrphanedDownloads: async () => {
      reloads.orphanedDownloads += 1;
    },
    loadEvictionSettings: async () => {
      reloads.evictionSettings += 1;
    }
  };
  bindLifted(reconnectCallback(STORAGE), bindings)();
  assert.deepEqual(reloads, { evictedItems: 1, orphanedDownloads: 1, evictionSettings: 1 });

  bindLifted(reconnectCallback(STORAGE), { ...bindings, isAnyEvictedRemovalRunning: true })();
  assert.deepEqual(
    reloads,
    { evictedItems: 1, orphanedDownloads: 2, evictionSettings: 2 },
    'a running evicted removal still owns the evicted list'
  );
});

test('an older eviction settings read that fails after a newer one succeeded shows no error', async () => {
  const state = { modes: [], savedModes: [], errors: [], loading: [] };
  const reads = [];
  const loadEvictionSettings = bindLifted(
    liftHookCallback(STORAGE, 'useCallback', 'getEvictionSettings'),
    {
      mockMode: false,
      evictionSettingsRequestRef: { current: 0 },
      ApiService: {
        getEvictionSettings: () =>
          new Promise((resolve, reject) => {
            reads.push({ resolve, reject });
          })
      },
      setEvictionLoading: (value) => state.loading.push(value),
      setEvictionMode: (value) => state.modes.push(value),
      setSavedEvictionMode: (value) => state.savedModes.push(value),
      setEvictionLoadError: (value) => state.errors.push(value),
      getErrorMessage: (error) => error.message
    }
  );

  const older = loadEvictionSettings();
  const newer = loadEvictionSettings();
  reads[1].resolve({ evictedDataMode: 'remove' });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.modes, ['remove']);
  assert.deepEqual(state.savedModes, ['remove']);
  assert.deepEqual(state.errors, [null], 'the stale failure never reaches the section');
  assert.equal(state.loading.at(-1), false);
});

test('an older evicted items read that fails after a newer one succeeded shows no error', async () => {
  const state = { games: [], services: [], errors: [], loading: [], logged: [] };
  const reads = [];
  const fetchEvictedItems = bindLifted(
    liftHookCallback(STORAGE, 'useCallback', 'getEvictedGames'),
    {
      mockMode: false,
      evictedItemsRequestRef: { current: 0 },
      hasLoadedEvictedItemsRef: { current: true },
      loadCachedDetectionSnapshot: () =>
        new Promise((resolve, reject) => {
          reads.push({ resolve, reject });
        }),
      getEvictedGames: (games) => games,
      getEvictedServices: (services) => services,
      setEvictedGames: (value) => state.games.push(value),
      setEvictedServices: (value) => state.services.push(value),
      setEvictedItemsLoading: (value) => state.loading.push(value),
      setEvictedItemsError: (value) => state.errors.push(value),
      getErrorMessage: (error) => error.message,
      notifyError: (message, error) => state.logged.push(error.message),
      t: (key) => key
    }
  );

  const older = fetchEvictedItems();
  const newer = fetchEvictedItems();
  reads[1].resolve({ games: ['game'], services: ['service'] });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.games, [['game']]);
  assert.deepEqual(state.services, [['service']]);
  assert.deepEqual(state.errors, [null], 'the stale failure never reaches the section');
  assert.equal(state.loading.at(-1), false);
});

test('a reset whose follow-up position read fails reports the reset, never a failed reset', async () => {
  const calls = { success: [], errors: [], refreshes: 0, actionLoading: [], modal: [] };
  const handleResetPosition = bindLifted(liftConstArrow(DATASOURCES, 'handleResetPosition'), {
    isAdmin: true,
    t: (key) => key,
    setActionLoading: (value) => calls.actionLoading.push(value),
    setResetModal: (value) => calls.modal.push(value),
    ApiService: {
      resetDatasourceLogPosition: async () => ({}),
      resetLogPosition: async () => ({})
    },
    fetchLogPositions: async () => {
      throw new Error('positions read failed');
    },
    setLogPositions: (value) => calls.errors.push(['positions written', value]),
    refreshPositions: async () => {
      calls.refreshes += 1;
    },
    onSuccess: (message) => calls.success.push(message),
    onError: (message, error) => calls.errors.push([message, error?.message]),
    onDataRefresh: () => calls.success.push('refreshed')
  });

  await handleResetPosition('Default', 'bottom');

  assert.deepEqual(calls.errors, [], 'the reset succeeded, so nothing says it failed');
  assert.deepEqual(calls.success, ['management.datasources.messages.positionReset', 'refreshed']);
  assert.equal(calls.refreshes, 1, 'the owned position read runs once');
  assert.deepEqual(calls.actionLoading, ['reset-Default', null]);
});

/** The detector's `startDetection`, lifted, with its React state as plain recorders. */
const liftStartDetection = ({ startGameCacheDetection }) => {
  const state = { starting: [], scanType: [], toasts: [], errors: [] };
  const detectionInFlightRef = { current: false };
  class ApiError extends Error {
    constructor(status) {
      super(`refused ${status}`);
      this.status = status;
    }
  }
  const startDetection = bindLifted(
    liftHookCallback(GAME_DETECTOR, 'useCallback', 'startGameCacheDetection'),
    {
      mockMode: false,
      loading: false,
      t: (key) => key,
      addNotification: (card) => state.toasts.push(card),
      notifyError: (message, error) => state.errors.push([message, error]),
      detectionInFlightRef,
      setIsStartingDetection: (value) => state.starting.push(value),
      setScanType: (value) => state.scanType.push(value),
      ApiService: { startGameCacheDetection },
      ApiError,
      isConfirmedScanRefusalStatus,
      getErrorMessage: (err) => err.message,
      isAbortError: () => false
    }
  );
  return { startDetection, state, detectionInFlightRef, ApiError };
};

test('a started detection opens no card of its own and is busy only while its request is out', async () => {
  let release;
  const { startDetection, state, detectionInFlightRef } = liftStartDetection({
    startGameCacheDetection: () =>
      new Promise((resolve) => {
        release = () => resolve({ operationId: 'G', status: 'running' });
      })
  });

  const pending = startDetection(true, 'full');
  assert.deepEqual(state.starting, [true]);
  assert.equal(detectionInFlightRef.current, true, 'a second click is ignored meanwhile');

  release();
  await pending;
  assert.deepEqual(state.starting, [true, false]);
  assert.deepEqual(state.scanType, ['full', null]);
  assert.equal(detectionInFlightRef.current, false);
  assert.deepEqual(state.toasts, [], 'the server run row opens the only card');
});

test('a refused detection says why and frees the buttons', async () => {
  let ApiErrorClass;
  let refusal;
  const lifted = liftStartDetection({
    startGameCacheDetection: async () => {
      refusal = new ApiErrorClass(400);
      throw refusal;
    }
  });
  ApiErrorClass = lifted.ApiError;

  await lifted.startDetection(false, 'incremental');
  assert.deepEqual(lifted.state.errors, [['management.gameDetection.errors.startFailed', refusal]]);
  assert.deepEqual(lifted.state.toasts, [], 'the titled error popup is the only one');
  assert.deepEqual(lifted.state.starting, [true, false]);
  assert.equal(lifted.detectionInFlightRef.current, false);
});
