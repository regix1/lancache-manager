import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * A failed read shows an error box with Retry. Clicking Retry keeps that box drawn, with Retry
 * busy, until the new read answers: no loader clears its error before the request settles, no
 * loading line takes the box's place, and a Retry that joins a read already out waits for it.
 *
 * Each loader that ships is lifted out of its file and run against a request the test holds open;
 * each box is found in the file that draws it and its render conditions are evaluated with the
 * loading flag up and the error set.
 */

const noop = () => undefined;
const quiet = { error: noop, warn: noop, log: noop };
const flush = () => new Promise((resolve) => setImmediate(resolve));

/** A promise the test settles by hand. */
const held = () => {
  const gate = {};
  gate.promise = new Promise((resolve, reject) => {
    gate.resolve = resolve;
    gate.reject = reject;
  });
  return gate;
};

/** A state setter that records each value, applying updaters to the last recorded value. */
const recorder = (initial) => {
  const values = [];
  let current = initial;
  const set = (next) => {
    current = typeof next === 'function' ? next(current) : next;
    values.push(current);
  };
  return { set, values, current: () => current };
};

/** `true` when a value counts as the error being gone: null, undefined or a map without `key`. */
const cleared = (value, key) =>
  value === null || value === undefined || (key !== undefined && !(key in value));

/** The sole `<ErrorBlock ...>` whose `onRetry` text contains `retryText`. */
const errorBlock = (path, retryText) => {
  const file = parseSource(path);
  const node = findSoleNode(
    file,
    `ErrorBlock with onRetry ${retryText}`,
    (candidate) =>
      typescript.isJsxSelfClosingElement(candidate) &&
      candidate.tagName.getText(file) === 'ErrorBlock' &&
      candidate.attributes.properties.some(
        (attribute) =>
          typescript.isJsxAttribute(attribute) &&
          attribute.name.getText(file) === 'onRetry' &&
          attribute.initializer?.getText(file).includes(retryText)
      )
  );
  return { file, node };
};

/** Every `ErrorBlock` whose `onRetry` text contains `retryText`. */
const errorBlocks = (path, retryText) => {
  const file = parseSource(path);
  const found = [];
  const visit = (candidate) => {
    if (
      typescript.isJsxSelfClosingElement(candidate) &&
      candidate.tagName.getText(file) === 'ErrorBlock' &&
      candidate.attributes.properties.some(
        (attribute) =>
          typescript.isJsxAttribute(attribute) &&
          attribute.name.getText(file) === 'onRetry' &&
          attribute.initializer?.getText(file).includes(retryText)
      )
    )
      found.push(candidate);
    typescript.forEachChild(candidate, visit);
  };
  typescript.forEachChild(file, visit);
  return { file, nodes: found };
};

/** The expression text of a block's `retrying` attribute, or `undefined` when it has none. */
const retryingText = (file, node) => {
  const attribute = node.attributes.properties.find(
    (candidate) =>
      typescript.isJsxAttribute(candidate) && candidate.name.getText(file) === 'retrying'
  );
  const value = attribute?.initializer;
  return value && typescript.isJsxExpression(value) ? value.expression.getText(file) : undefined;
};

/**
 * Whether the block is drawn under `state`: every ternary above it must take the branch that holds
 * it, and every `&&` above it must pass. Only conditions that name `flag` are evaluated, so the
 * state needs to carry only the names those conditions read.
 */
const drawnWith = (file, node, flag, state) => {
  let child = node;
  for (
    let parent = node.parent;
    parent && !typescript.isSourceFile(parent);
    parent = parent.parent
  ) {
    if (typescript.isArrowFunction(parent) || typescript.isFunctionDeclaration(parent)) break;
    let condition;
    let wanted;
    if (typescript.isConditionalExpression(parent) && parent.condition !== child) {
      condition = parent.condition;
      wanted = parent.whenTrue === child;
    } else if (
      typescript.isBinaryExpression(parent) &&
      parent.operatorToken.kind === typescript.SyntaxKind.AmpersandAmpersandToken &&
      parent.right === child
    ) {
      condition = parent.left;
      wanted = true;
    }
    if (condition && condition.getText(file).includes(flag)) {
      const value = bindLifted(`() => (${condition.getText(file)})`, state)();
      if (Boolean(value) !== wanted) return false;
    }
    child = parent;
  }
  return true;
};

test('game cache detection keeps its box while the Retry read runs, and Retry shows busy', async () => {
  const path = 'src/components/features/management/game-detection/GameCacheDetector.tsx';
  const errors = recorder('the read failed');
  const read = held();
  const effect = bindLifted(liftHookCallback(path, 'useEffect', 'const loadCachedGames'), {
    mockMode: false,
    setLoadError: errors.set,
    setIsLoadingInitialCache: noop,
    syncCachedDetection: () => read.promise,
    getErrorMessage: (error) => error.message
  });
  effect();
  await flush();
  assert.deepEqual(errors.values, [], 'the box is not cleared before the read answers');
  read.resolve(null);
  await flush();

  const { file, node } = errorBlock(path, 'setInitialLoadRetry');
  assert.equal(retryingText(file, node), 'isLoadingInitialCache');
});

test('the failed games box stays inside the open dialog with Retry busy until the read answers', async () => {
  const path =
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillActivityModal.tsx';
  const errors = recorder(new Error('the read failed'));
  const read = held();
  const loadFailedGames = bindLifted(liftConstArrow(path, 'loadFailedGames'), {
    abortRef: { current: null },
    setFailedGamesError: errors.set,
    setFailedGamesLoading: noop,
    setFailedGames: noop,
    ApiService: { getPersistentPrefillRunFailedGames: () => read.promise },
    getPersistentServiceId: (key) => key,
    serviceKey: 'steam',
    run: { runId: 'run-1' }
  });
  const retry = loadFailedGames();
  await flush();
  assert.deepEqual(errors.values, [], 'the box is not cleared before the read answers');
  assert.ok(retry instanceof Promise, 'Retry gets the read, so it shows busy while it runs');
  read.resolve([]);
  await retry;
  assert.equal(errors.current(), null, 'an answer clears the box');

  const { file, node } = errorBlock(path, 'loadFailedGames');
  assert.equal(
    drawnWith(file, node, 'failedGamesLoading', {
      failedGamesLoading: true,
      failedGamesError: new Error('the read failed')
    }),
    true,
    'the loading line does not take the box’s place while the Retry read runs'
  );
});

test('the prefill connection box stays up through Retry, which shows busy for any running start', async () => {
  const hook = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
  const panel = 'src/components/features/prefill/PrefillPanel.tsx';
  const failed = recorder(true);
  const connect = held();
  const initializeSession = bindLifted(liftHookCallback(hook, 'useCallback', "'GetMySessions'"), {
    initializationAttempted: { current: false },
    setHubConnectFailed: failed.set,
    setIsInitializing: noop,
    connectToHub: () => connect.promise,
    addLog: noop,
    t: (key) => key,
    sessionStore: { getItem: () => null },
    STORAGE_KEYS: { PREFILL_SESSION_ID: 'prefill-session' },
    clearAllPrefillStorage: noop,
    acceptedConnectionRef: { current: null },
    sessionRef: { current: null },
    HubConnectionState: { Connected: 'Connected' },
    getErrorMessage: (error) => String(error),
    console: quiet
  });
  const start = initializeSession();
  await flush();
  assert.deepEqual(failed.values, [], 'the box is not cleared before the hub answers');
  connect.resolve({ state: 'Connected', invoke: async () => [] });
  await start;
  assert.equal(failed.current(), false, 'the hub answering clears the box');

  let runs = 0;
  const retryConnection = bindLifted(
    liftHookCallback(hook, 'useCallback', 'initializationAttempted.current = false'),
    {
      initializationAttempted: { current: true },
      initializeSession: async () => {
        runs += 1;
      }
    }
  );
  assert.ok(retryConnection() instanceof Promise, 'Retry gets the start it runs');
  assert.equal(runs, 1);

  const { file, nodes } = errorBlocks(panel, 'retryConnection');
  assert.equal(nodes.length, 2);
  for (const node of nodes) {
    assert.equal(retryingText(file, node), 'signalR.isConnecting || signalR.isInitializing');
  }

  // A reconnect while a start runs folds into that start instead of beginning a second one.
  const panelFile = parseSource(panel);
  const reconnect = findSoleNode(
    panelFile,
    'reconnect callback that reloads games',
    (candidate) =>
      typescript.isCallExpression(candidate) &&
      candidate.expression.getText(panelFile) === 'useReconnectRefetch' &&
      candidate.arguments[1]?.getText(panelFile).includes('reloadGamesOnce')
  );
  let retries = 0;
  const retryAfterStartRef = { current: false };
  bindLifted(reconnect.arguments[1].getText(panelFile), {
    reloadGamesOnce: async () => undefined,
    signalR: { hubConnectFailed: true, isConnecting: false, isInitializing: true },
    retryConnection: async () => {
      retries += 1;
    },
    retryAfterStartRef
  })();
  assert.equal(retries, 0, 'no second start runs beside the one already out');
  assert.equal(retryAfterStartRef.current, true);
});

test('schedules: a Retry during a read already out waits for that read', async () => {
  const path = 'src/components/features/management/schedules/SchedulesSection.tsx';
  const reads = [];
  const fetchSchedules = bindLifted(liftHookCallback(path, 'useCallback', 'getSchedules()'), {
    mockMode: false,
    setSchedules: noop,
    isFetchingRef: { current: null },
    pendingRefetchRef: { current: false },
    signalrGenerationRef: { current: 0 },
    ApiService: {
      getSchedules: () => {
        const read = held();
        reads.push(read);
        return read.promise;
      }
    },
    readSchedules: (schedules) => schedules,
    setError: noop,
    getErrorMessage: (error) => error.message
  });
  const first = fetchSchedules();
  let retrySettled = false;
  const retry = fetchSchedules().then(() => {
    retrySettled = true;
  });
  await flush();
  assert.equal(retrySettled, false, 'Retry stays busy while the read it joined is out');
  reads[0].resolve([]);
  await flush();
  reads[1].resolve([]);
  await Promise.all([first, retry]);
  assert.equal(retrySettled, true);
});

test('community themes: a Retry during a load already out waits for that load', async () => {
  const path = 'src/components/features/management/theme/CommunityThemeImporter.tsx';
  const response = held();
  const errors = recorder('GitHub could not be reached');
  const loadCommunityThemes = bindLifted(liftConstArrow(path, 'loadCommunityThemes'), {
    loadingInProgressRef: { current: null },
    setLoading: noop,
    rateLimitRemaining: { current: null },
    storage: { getItem: () => null, setItem: noop },
    GITHUB_CACHE_KEY: 'cache',
    GITHUB_ETAG_KEY: 'etag',
    GITHUB_API_BASE: 'https://example.test/themes',
    fetch: () => response.promise,
    t: (key) => key,
    setLoadError: errors.set,
    getErrorMessage: (error) => error.message,
    fetchThemeContents: async () => [],
    setCommunityThemes: noop,
    autoCheckUpdates: false,
    isAdmin: false,
    checkAndUpdateThemes: async () => undefined,
    console: quiet
  });
  const first = loadCommunityThemes();
  let retrySettled = false;
  const retry = loadCommunityThemes().then(() => {
    retrySettled = true;
  });
  await flush();
  assert.equal(retrySettled, false, 'Retry stays busy while the load it joined is out');
  response.reject(new Error('offline'));
  await Promise.all([first, retry]);
  assert.equal(retrySettled, true);
});

test('cache size: the box stays through Retry and Retry shows busy while the read is out', async () => {
  const path = 'src/contexts/CacheSizeContext.tsx';
  const errors = recorder('the read failed');
  const read = held();
  const fetchCacheSize = bindLifted(liftHookCallback(path, 'useCallback', 'getCacheSize('), {
    abortControllerRef: { current: null },
    pendingRefetchRef: { current: false },
    document: { hidden: false },
    setIsLoading: noop,
    setError: errors.set,
    setDenialReason: noop,
    ApiService: { getCacheSize: () => read.promise },
    setHasFetched: noop,
    setCacheSize: noop,
    isAbortError: () => false,
    isRefusal: () => false,
    getErrorMessage: (error) => error.message,
    i18n: { t: (key) => key },
    fetchRef: { current: noop },
    console: quiet
  });
  const retry = fetchCacheSize();
  await flush();
  assert.deepEqual(errors.values, [], 'the box is not cleared before the read answers');
  read.resolve({ usedBytes: 1 });
  await retry;
  assert.equal(errors.current(), null);

  const { file, node } = errorBlock(
    'src/components/features/management/cache/CacheManager.tsx',
    'fetchCacheSize'
  );
  assert.equal(retryingText(file, node), 'cacheSizeLoading');
});

test('shared list loads: the box stays through Retry and a Retry that folds in waits', async () => {
  const path = 'src/hooks/useLoadLifecycle.ts';
  const errors = recorder('the read failed');
  const reads = [];
  const load = bindLifted(liftHookCallback(path, 'useCallback', 'settings.canLoad()'), {
    mountedRef: { current: true },
    optionsRef: {
      current: {
        canLoad: () => true,
        request: () => {
          const read = held();
          reads.push(read);
          return read.promise;
        },
        onLoaded: noop,
        onFailed: noop
      }
    },
    abortControllerRef: { current: null },
    runningLoadRef: { current: null },
    loadedAtRef: { current: 0 },
    STALE_AFTER_MS: 30_000,
    LOAD_TIMEOUT_MS: 30_000,
    setError: errors.set,
    getErrorMessage: (error) => error.message,
    isAbortError: () => false
  });
  const running = load(false);
  let retrySettled = false;
  const retry = load(false).then(() => {
    retrySettled = true;
  });
  await flush();
  assert.deepEqual(errors.values, [], 'the box is not cleared before the read answers');
  assert.equal(retrySettled, false, 'Retry stays busy while the load it folded into is out');
  reads[0].resolve([]);
  await Promise.all([running, retry]);
  assert.equal(retrySettled, true);
  assert.equal(errors.current(), null);
});

test('events: the box stays through Retry until the pair of reads answers', async () => {
  const path = 'src/contexts/EventContext.tsx';
  const errors = recorder('the read failed');
  const read = held();
  const refreshEvents = bindLifted(liftHookCallback(path, 'useCallback', 'getActiveEvents()'), {
    refreshRequestIdRef: { current: 0 },
    hasLoadedRef: { current: true },
    setLoading: noop,
    setError: errors.set,
    mockMode: false,
    ApiService: { getEvents: () => read.promise, getActiveEvents: async () => [] },
    setActiveEvents: noop,
    setEvents: noop,
    selectedEventId: null,
    setSelectedEventId: noop,
    authMode: 'authenticated',
    getErrorMessage: (error) => error.message,
    console: quiet
  });
  const retry = refreshEvents();
  await flush();
  assert.deepEqual(errors.values, [], 'the box is not cleared before the reads answer');
  read.reject(new Error('still failing'));
  await retry;
  assert.deepEqual(errors.values, ['still failing'], 'one write of the pair’s failure');
});

test('hostnames: the box stays through Retry until the read answers', async () => {
  const path = 'src/contexts/ClientHostnameContext.tsx';
  const errors = recorder('the read failed');
  const read = held();
  const refreshHostnames = bindLifted(
    liftHookCallback(path, 'useCallback', 'getClientHostnames()'),
    {
      mockModeRef: { current: false },
      attemptRef: { current: 0 },
      setLoading: noop,
      setError: errors.set,
      ApiService: { getClientHostnames: () => read.promise },
      setHostnameLookup: noop,
      getErrorMessage: (error) => error.message,
      console: quiet
    }
  );
  const retry = refreshHostnames();
  await flush();
  assert.deepEqual(errors.values, [], 'the box is not cleared before the read answers');
  read.resolve({ enabled: true, hostnames: {} });
  await retry;
  assert.equal(errors.current(), null);
});

test('corruption results: both boxes stay through Retry, with Retry busy', async () => {
  const path = 'src/components/features/management/cache/CorruptionManager.tsx';
  const cachedErrors = recorder('the read failed');
  const cachedRead = held();
  const loadCachedResults = bindLifted(
    liftHookCallback(path, 'useCallback', 'getCachedCorruptionDetection('),
    {
      mockMode: false,
      markLoaded: noop,
      markFailed: noop,
      detectionMethod: 'repeated_miss',
      beginLoad: noop,
      setCachedLoadError: cachedErrors.set,
      resultEpochRef: { current: 0 },
      ApiService: { getCachedCorruptionDetection: () => cachedRead.promise },
      getErrorMessage: (error) => error.message,
      notifyError: noop,
      t: (key) => key
    }
  );
  const cachedRetry = loadCachedResults();
  await flush();
  assert.deepEqual(
    cachedErrors.values,
    [],
    'the cached box is not cleared before the read answers'
  );
  cachedRead.reject(new Error('still failing'));
  await cachedRetry;

  const detailErrors = recorder({ steam: 'the read failed' });
  const detailRead = held();
  const loadDetails = bindLifted(liftConstArrow(path, 'loadDetails'), {
    scanId: 'scan-1',
    loadingDetailsServices: new Set(),
    resultEpochRef: { current: 0 },
    setDetailErrors: detailErrors.set,
    setLoadingDetailsServices: noop,
    setCorruptionDetails: noop,
    ApiService: { getCorruptionDetails: () => detailRead.promise },
    getErrorMessage: (error) => error.message,
    getServiceDisplayName: (service) => service,
    notifyError: noop,
    t: (key) => key
  });
  const detailRetry = loadDetails('steam');
  await flush();
  assert.ok(
    detailErrors.values.every((value) => !cleared(value, 'steam')),
    'the detail box is not cleared before the read answers'
  );
  detailRead.resolve([{ url: 'chunk' }]);
  await detailRetry;
  assert.equal(cleared(detailErrors.current(), 'steam'), true, 'an answer clears the detail box');

  const cached = errorBlock(path, 'loadCachedData()');
  assert.equal(
    drawnWith(cached.file, cached.node, 'isLoading', {
      isLoading: true,
      cachedLoadError: 'the read failed'
    }),
    true
  );
  assert.equal(retryingText(cached.file, cached.node), 'isLoading');
  const detail = errorBlock(path, 'loadDetails(service)');
  assert.equal(
    drawnWith(detail.file, detail.node, 'loadingDetailsServices', {
      loadingDetailsServices: new Set(['steam']),
      detailErrors: { steam: 'the read failed' },
      service: 'steam'
    }),
    true
  );
  assert.equal(retryingText(detail.file, detail.node), 'loadingDetailsServices.has(service)');
});

test('corruption scan history: list and detail boxes stay through Retry', async () => {
  const path = 'src/components/features/management/cache/CorruptionScanHistory.tsx';
  const listErrors = recorder('the read failed');
  const listRead = held();
  const loadHistory = bindLifted(
    liftHookCallback(path, 'useCallback', 'getCorruptionScanHistory()'),
    {
      mockMode: false,
      setEntries: noop,
      setListLoading: noop,
      listRequestSeqRef: { current: 0 },
      setListError: listErrors.set,
      ApiService: { getCorruptionScanHistory: () => listRead.promise },
      validateCorruptionScanHistory: (value) => value,
      getErrorMessage: (error) => error.message,
      notifyError: noop,
      t: (key) => key
    }
  );
  const listRetry = loadHistory();
  await flush();
  assert.deepEqual(listErrors.values, [], 'the list box is not cleared before the read answers');
  listRead.resolve([]);
  await listRetry;
  assert.equal(listErrors.current(), null);

  const detailErrors = recorder({ steam: 'the read failed' });
  const detailRead = held();
  const loadHistoryDetails = bindLifted(
    liftHookCallback(path, 'useCallback', 'getCorruptionHistoryDetails('),
    {
      detailRequestSeqRef: { current: 0 },
      setDetailErrorServices: detailErrors.set,
      setDetailLoadingService: noop,
      setDetailChunks: noop,
      ApiService: { getCorruptionHistoryDetails: () => detailRead.promise },
      validateCorruptionHistoryDetails: (value) => value,
      getErrorMessage: (error) => error.message,
      getServiceDisplayName: (service) => service,
      notifyError: noop,
      t: (key) => key
    }
  );
  const detailRetry = loadHistoryDetails(
    { scanId: 'scan-1', detectionMethod: 'structural' },
    'steam'
  );
  await flush();
  assert.ok(
    detailErrors.values.every((value) => !cleared(value, 'steam')),
    'the detail box is not cleared before the read answers'
  );
  detailRead.resolve([{ url: 'chunk' }]);
  await detailRetry;
  assert.equal(cleared(detailErrors.current(), 'steam'), true);

  const detail = errorBlock(path, 'loadHistoryDetails(');
  assert.equal(
    drawnWith(detail.file, detail.node, 'detailLoadingService', {
      detailLoadingService: 'steam',
      detailErrorServices: { steam: 'the read failed' },
      service: 'steam'
    }),
    true
  );
  assert.equal(retryingText(detail.file, detail.node), 'detailLoadingService === service');
});

test('log positions, log counts and themes keep their boxes through Retry', async () => {
  const cases = [
    {
      path: 'src/components/features/management/datasources/DatasourcesInfo.tsx',
      lift: (path) => liftHookCallback(path, 'useCallback', 'setLoading(true)'),
      setter: 'setLoadError',
      bindings: (read) => ({
        positionsRequestRef: { current: 0 },
        setLoading: noop,
        fetchLogPositions: () => read.promise,
        refreshLogResetRunning: async () => undefined,
        setLogPositions: noop,
        getErrorMessage: (error) => error.message
      }),
      answer: []
    },
    {
      path: 'src/components/features/management/log-processing/LogRemovalManager.tsx',
      lift: (path) => liftConstArrow(path, 'loadData'),
      setter: 'setLoadError',
      bindings: (read) => ({
        mockMode: false,
        markLoaded: noop,
        markFailed: noop,
        countsRequestRef: { current: 0 },
        beginLoad: noop,
        ApiService: { getServiceLogCountsByDatasource: () => read.promise },
        setDatasourceCounts: noop,
        getErrorMessage: (error) => error.message,
        console: quiet
      }),
      answer: []
    },
    {
      path: 'src/components/features/management/theme/ThemeManager.tsx',
      lift: (path) => liftHookCallback(path, 'useCallback', 'themeService.loadThemes()'),
      setter: 'setLoadError',
      bindings: (read) => ({
        themesRequestRef: { current: 0 },
        setLoading: noop,
        themeService: { loadThemes: () => read.promise },
        setThemes: noop,
        getErrorMessage: (error) => error.message
      }),
      answer: { themes: [], loadError: null }
    }
  ];
  for (const { path, lift, setter, bindings, answer } of cases) {
    const errors = recorder('the read failed');
    const read = held();
    const loader = bindLifted(lift(path), { ...bindings(read), [setter]: errors.set });
    const retry = loader();
    await flush();
    assert.deepEqual(errors.values, [], `${path}: the box is not cleared before the read answers`);
    read.resolve(answer);
    await retry;
    assert.equal(errors.current(), null, `${path}: an answer clears the box`);
  }

  const positions = errorBlock(
    'src/components/features/management/datasources/DatasourcesInfo.tsx',
    'loadData()'
  );
  assert.equal(
    drawnWith(positions.file, positions.node, 'loading', {
      loading: true,
      loadError: 'the read failed'
    }),
    true,
    'the skeleton does not take the box’s place while the Retry read runs'
  );
});

test('eviction settings keep their box through Retry, with Retry busy', () => {
  const { file, node } = errorBlock(
    'src/components/features/management/sections/StorageSection.tsx',
    'loadEvictionSettings()'
  );
  assert.equal(
    drawnWith(file, node, 'evictionLoading', {
      evictionLoading: true,
      evictionLoadError: 'the read failed'
    }),
    true,
    'the loading line does not take the box’s place while the Retry read runs'
  );
  assert.equal(retryingText(file, node), 'evictionLoading');
});
