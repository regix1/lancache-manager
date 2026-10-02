import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { bindLifted, liftConstArrow, liftHookCallback, parseSource } from './transpile-module.mjs';

const CONFIG_CONTEXT = 'src/contexts/ConfigContext.tsx';
const DATASOURCES = 'src/components/features/management/datasources/DatasourcesInfo.tsx';
const CACHE_MANAGER = 'src/components/features/management/cache/CacheManager.tsx';
const LOG_REMOVAL = 'src/components/features/management/log-processing/LogRemovalManager.tsx';

const heldReads = () => {
  const reads = [];
  const start = () =>
    new Promise((resolve, reject) => {
      reads.push({ resolve, reject });
    });
  return { reads, start };
};

const datasource = (name, overrides = {}) => ({
  name,
  cachePath: `/cache/${name}`,
  logsPath: `/logs/${name}`,
  cacheWritable: true,
  logsWritable: true,
  enabled: true,
  layout: 'monolithic',
  nginxReopenAvailable: true,
  nginxReopenRequirement: 'required',
  nginxReopenCheckOnAction: false,
  cacheSizeOverrideBytes: null,
  resolvedCacheSizeBytes: 100,
  cacheSizeSource: 'fullDisk',
  ...overrides
});

const config = (sources, marker) => ({
  cachePath: '/cache',
  logsPath: '/logs',
  dataPath: '/data',
  cacheDeleteMode: 'preserve',
  steamAuthMode: 'anonymous',
  timeZone: 'UTC',
  cacheWritable: true,
  logsWritable: true,
  dataSources: sources,
  marker
});

const setValue = (state, key) => (value) => {
  state[key] = typeof value === 'function' ? value(state[key]) : value;
};

const buildSaveHarness = (initialConfig) => {
  const state = {
    config: initialConfig,
    saving: null,
    draft: { alpha: '250G' },
    errors: {},
    successes: [],
    refreshes: 0,
    dataRefreshes: 0
  };
  const configRef = { current: state.config };
  const cacheSizeSaveRequestRef = { current: 0 };
  const cacheSizeSaveAllowedRef = { current: true };
  const requests = heldReads();
  const setConfig = (update) => {
    state.config = update(state.config);
    configRef.current = state.config;
  };
  const updateConfig = bindLifted(
    liftHookCallback(CONFIG_CONTEXT, 'useCallback', "typeof patch === 'function'"),
    { setConfig }
  );
  const saveCacheSize = bindLifted(liftConstArrow(DATASOURCES, 'saveCacheSize'), {
    cacheSizeSaving: null,
    isAdmin: true,
    mockMode: false,
    config: initialConfig,
    cacheSizeSaveRequestRef,
    cacheSizeSaveAllowedRef,
    configRef,
    setCacheSizeSaving: setValue(state, 'saving'),
    setCacheSizeError: setValue(state, 'errors'),
    ApiService: { setDatasourceCacheSize: requests.start },
    updateConfig,
    setCacheSizeDraft: setValue(state, 'draft'),
    onSuccess: (message) => state.successes.push(message),
    t: (key) => key,
    refreshConfig: async () => {
      state.refreshes += 1;
    },
    onDataRefresh: () => {
      state.dataRefreshes += 1;
    },
    getErrorMessage: (error) => error.message
  });

  const render = ({ nextConfig = state.config, allowed = true, mockMode = false } = {}) => {
    state.config = nextConfig;
    configRef.current = nextConfig;
    cacheSizeSaveAllowedRef.current = allowed && !mockMode;
    if (state.saving === null) return;
    bindLifted(liftHookCallback(DATASOURCES, 'useEffect', 'cacheSizeSaving.cachePath'), {
      cacheSizeSaving: state.saving,
      config: nextConfig,
      isAdmin: allowed,
      mockMode,
      cacheSizeSaveRequestRef,
      setCacheSizeSaving: setValue(state, 'saving')
    })();
  };

  return { state, requests, saveCacheSize, render, cacheSizeSaveRequestRef };
};

test('only the newest config response updates configuration and errors', async () => {
  const state = { configs: [], errors: [], zones: [] };
  const requests = heldReads();
  const loadConfig = bindLifted(liftHookCallback(CONFIG_CONTEXT, 'useCallback', '/system/config'), {
    configRequestRef: { current: 0 },
    setError: (value) => state.errors.push(value),
    window: { setTimeout: () => 1, clearTimeout: () => undefined },
    CONFIG_TIMEOUT_MS: 8000,
    API_BASE: '/api',
    fetch: requests.start,
    ApiService: {
      getFetchOptions: () => ({}),
      handleResponse: async (response) => response.body
    },
    setServerTimezone: (value) => state.zones.push(value),
    setConfig: (value) => state.configs.push(value),
    configRef: { current: null },
    t: (key) => key,
    getErrorMessage: (error) => error.message
  });

  const older = loadConfig();
  const newer = loadConfig();
  const newest = config([datasource('new')], 'new');
  requests.reads[1].resolve({ body: newest });
  await newer;
  requests.reads[0].resolve({ body: config([datasource('old')], 'old') });
  await older;

  assert.deepEqual(state.configs, [newest]);
  assert.deepEqual(state.zones, ['UTC']);

  const staleFailure = loadConfig();
  const current = loadConfig();
  requests.reads[3].resolve({ body: newest });
  await current;
  requests.reads[2].reject(new Error('stale config failure'));
  await staleFailure;
  assert.equal(
    state.errors.some((value) => value?.message === 'stale config failure'),
    false
  );
});

test('only the newest datasource count response controls rows and loading', async () => {
  const state = { counts: [], errors: [], loaded: 0, failed: 0, loads: [] };
  const requests = heldReads();
  const loadCounts = bindLifted(liftConstArrow(LOG_REMOVAL, 'loadData'), {
    mockMode: false,
    markLoaded: () => {
      state.loaded += 1;
    },
    countsRequestRef: { current: 0 },
    beginLoad: (force) => state.loads.push(force),
    setLoadError: (value) => state.errors.push(value),
    ApiService: { getServiceLogCountsByDatasource: requests.start },
    setDatasourceCounts: (value) => state.counts.push(value),
    getErrorMessage: (error) => error.message,
    markFailed: () => {
      state.failed += 1;
    }
  });

  const older = loadCounts();
  const newer = loadCounts(true);
  const newest = [{ datasource: 'beta', serviceCounts: { steam: 4 } }];
  requests.reads[1].resolve(newest);
  await newer;
  requests.reads[0].resolve([{ datasource: 'alpha', serviceCounts: { steam: 1 } }]);
  await older;
  assert.deepEqual(state.counts, [newest]);
  assert.equal(state.loaded, 1);

  const staleFailure = loadCounts();
  const current = loadCounts(true);
  requests.reads[3].resolve(newest);
  await current;
  requests.reads[2].reject(new Error('stale count failure'));
  await staleFailure;
  assert.equal(state.errors.includes('stale count failure'), false);
  assert.equal(state.failed, 0);
});

test('a failed log file delete still reloads the counts', async () => {
  const state = { loads: 0, errors: [] };
  const executeDeleteLogFile = bindLifted(liftConstArrow(LOG_REMOVAL, 'executeDeleteLogFile'), {
    authMode: 'authenticated',
    onError: (message) => state.errors.push(message),
    t: (key) => key,
    setPendingLogFileDeletion: () => undefined,
    setDeletingLogFile: () => undefined,
    ApiService: { deleteLogFile: () => Promise.reject(new Error('unlink failed')) },
    loadData: async () => {
      state.loads += 1;
    },
    getErrorMessage: (error) => error.message
  });

  await executeDeleteLogFile('alpha');
  assert.equal(state.loads, 1);
  assert.deepEqual(state.errors, ['management.logRemoval.errors.deleteFailed']);
});

test('the positions panel reloads when the log files change', () => {
  const subscribed = [];
  const unsubscribed = [];
  let refreshes = 0;
  const cleanup = bindLifted(liftHookCallback(DATASOURCES, 'useEffect', 'LogProcessingComplete'), {
    mockMode: false,
    signalR: {
      on: (name, handler) => subscribed.push({ name, handler }),
      off: (name, handler) => unsubscribed.push({ name, handler })
    },
    refreshPositions: async () => {
      refreshes += 1;
    }
  })();

  assert.deepEqual(
    subscribed.map(({ name }) => name),
    ['LogProcessingComplete', 'ServiceCountsChanged']
  );
  subscribed.find(({ name }) => name === 'ServiceCountsChanged').handler();
  assert.equal(refreshes, 1);
  cleanup();
  assert.deepEqual(unsubscribed, subscribed);
});

test('a deferred cache-size save merges three values into the current datasource', async () => {
  const initial = config([datasource('alpha'), datasource('beta')], 'initial');
  const harness = buildSaveHarness(initial);
  const saving = harness.saveCacheSize('alpha', '250G', 'save');
  const currentAlpha = datasource('alpha', {
    cacheWritable: false,
    logsWritable: false,
    sourceCount: 0,
    cacheSizeOverrideBytes: 20,
    resolvedCacheSizeBytes: 200,
    cacheSizeSource: 'docker'
  });
  harness.render({
    nextConfig: config([currentAlpha, datasource('gamma')], 'current')
  });
  harness.requests.reads[0].resolve({
    cacheSizeOverrideBytes: 250,
    resolvedCacheSizeBytes: 500,
    cacheSizeSource: 'manual'
  });
  await saving;

  assert.equal(harness.state.config.marker, 'current');
  assert.deepEqual(
    harness.state.config.dataSources.map((source) => source.name),
    ['alpha', 'gamma']
  );
  assert.deepEqual(harness.state.config.dataSources[0], {
    ...currentAlpha,
    cacheSizeOverrideBytes: 250,
    resolvedCacheSizeBytes: 500,
    cacheSizeSource: 'manual'
  });
  assert.equal(harness.state.successes.length, 1);
  assert.equal(harness.state.refreshes, 1);
  assert.equal(harness.state.dataRefreshes, 1);
  assert.equal(harness.state.draft.alpha, '');
  assert.equal(harness.state.saving, null);
});

test('removed, renamed, and rebound save targets are never restored', async () => {
  const changedConfigs = [
    config([datasource('gamma')], 'removed'),
    config([datasource('alpha-renamed'), datasource('gamma')], 'renamed'),
    config([datasource('alpha', { cachePath: '/replacement/alpha' })], 'rebound')
  ];

  for (const nextConfig of changedConfigs) {
    const harness = buildSaveHarness(config([datasource('alpha'), datasource('beta')], 'initial'));
    const saving = harness.saveCacheSize('alpha', '250G', 'save');
    harness.render({ nextConfig });
    harness.requests.reads[0].resolve({
      cacheSizeOverrideBytes: 250,
      resolvedCacheSizeBytes: 500,
      cacheSizeSource: 'manual'
    });
    await saving;
    assert.deepEqual(harness.state.config, nextConfig);
    assert.deepEqual(harness.state.successes, []);
    assert.equal(harness.state.refreshes, 0);
    assert.equal(harness.state.saving, null);
  }
});

test('current authorization loss and a newer save owner suppress stale effects', async () => {
  const denied = buildSaveHarness(config([datasource('alpha')], 'initial'));
  const deniedSave = denied.saveCacheSize('alpha', '250G', 'save');
  denied.render({ allowed: false });
  denied.requests.reads[0].reject(new Error('late denied failure'));
  await deniedSave;
  assert.deepEqual(denied.state.errors, { alpha: undefined });
  assert.deepEqual(denied.state.successes, []);
  assert.equal(denied.state.saving, null);

  const superseded = buildSaveHarness(config([datasource('alpha')], 'initial'));
  const olderSave = superseded.saveCacheSize('alpha', '250G', 'save');
  superseded.cacheSizeSaveRequestRef.current += 1;
  superseded.state.saving = {
    name: 'alpha',
    action: 'reset',
    cachePath: '/cache/alpha',
    logsPath: '/logs/alpha'
  };
  superseded.requests.reads[0].reject(new Error('stale save failure'));
  await olderSave;
  assert.equal(superseded.state.saving.action, 'reset');
  assert.equal(superseded.state.errors.alpha, undefined);
});

test('datasource rows and empty states preserve enabled and empty meanings', () => {
  const cacheSource = parseSource(CACHE_MANAGER).text;
  const logSource = parseSource(LOG_REMOVAL).text;
  const datasourceSource = parseSource(DATASOURCES).text;

  for (const [name, source] of [
    ['cache', cacheSource],
    ['log', logSource]
  ]) {
    const enabledValues = [
      ...source.matchAll(/<DatasourceListItem[\s\S]*?enabled=\{([^}]+)\}/g)
    ].map((match) => match[1].trim());
    assert.ok(enabledValues.length > 0, `${name} rendered no datasource row`);
    assert.deepEqual(
      enabledValues,
      enabledValues.map(() => 'ds.enabled')
    );
  }

  assert.equal(
    (cacheSource.match(/management\.datasources\.noActiveDatasources/g) ?? []).length,
    1
  );
  assert.equal(
    (datasourceSource.match(/management\.datasources\.noActiveDatasources/g) ?? []).length,
    1
  );
  assert.doesNotMatch(cacheSource, /['"]unknown['"]/);
});

test('a missing cache-clear target sends no request while an existing target does', async () => {
  const cacheSource = parseSource(CACHE_MANAGER).text;
  assert.doesNotMatch(cacheSource, /NginxReopenActionGate|getNginxReopenGate/);
  const calls = [];
  const startMissing = bindLifted(liftConstArrow(CACHE_MANAGER, 'startCacheClear'), {
    cacheOperationInProgressRef: { current: false },
    clearingTargetMissing: true,
    setActionLoading: () => undefined,
    setShowConfirmModal: () => undefined,
    clearingDatasource: 'removed',
    ApiService: {
      clearDatasourceCache: (name) => calls.push(name),
      clearAllCache: () => calls.push('all')
    },
    onError: () => undefined,
    t: (key) => key
  });
  await startMissing();
  assert.deepEqual(calls, []);

  const startExisting = bindLifted(liftConstArrow(CACHE_MANAGER, 'startCacheClear'), {
    cacheOperationInProgressRef: { current: false },
    clearingTargetMissing: false,
    setActionLoading: () => undefined,
    setShowConfirmModal: () => undefined,
    clearingDatasource: 'alpha',
    ApiService: {
      clearDatasourceCache: async (name) => calls.push(name),
      clearAllCache: async () => calls.push('all')
    },
    onError: () => undefined,
    t: (key) => key
  });
  await startExisting();
  assert.deepEqual(calls, ['alpha']);
});

test('new datasource strings exist with matching placeholders in both locales', async () => {
  const locales = await Promise.all(
    ['en', 'zh'].map(async (locale) =>
      JSON.parse(
        await readFile(new URL(`../src/i18n/locales/${locale}.json`, import.meta.url), 'utf8')
      )
    )
  );
  const paths = [
    ['management', 'nginxReopen', 'windowsDockerUnsupported'],
    ['management', 'nginxReopen', 'writerUnknown'],
    ['management', 'nginxReopen', 'checkOnAction'],
    ['management', 'cache', 'modal', 'datasourceGone'],
    ['management', 'gameDetection', 'removalScope', 'allSources'],
    ['management', 'gameDetection', 'removalScope', 'filtered'],
    ['management', 'datasources', 'noActiveDatasources'],
    ['signalr', 'cacheClear', 'forDatasource'],
    ['signalr', 'logProcessing', 'progressSource']
  ];

  for (const path of paths) {
    const messages = locales.map((locale) => path.reduce((value, key) => value[key], locale));
    assert.ok(messages.every((message) => typeof message === 'string' && message.length > 0));
    const placeholders = messages.map((message) =>
      [...message.matchAll(/{{(\w+)}}/g)].map((match) => match[1]).sort()
    );
    assert.deepEqual(placeholders[0], placeholders[1], `${path.join('.')} placeholders differ`);
  }
});
