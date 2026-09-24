import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const signalRTypes = readWebSource('src/contexts/SignalRContext/types.ts');
const registry = readWebSource('src/contexts/notifications/notificationRegistry.ts');
const registryEntries = readWebSource('src/contexts/notifications/registryEntries.ts');
const xboxAuthHook = readWebSource('src/hooks/useXboxMappingAuth.ts');
const cacheManager = readWebSource('src/components/features/management/cache/CacheManager.tsx');
const dashboardContext = readWebSource('src/contexts/DashboardDataContext/index.tsx');
const gameCacheDetector = readWebSource(
  'src/components/features/management/game-detection/GameCacheDetector.tsx'
);
const storageSection = readWebSource(
  'src/components/features/management/sections/StorageSection.tsx'
);
const corruptionManager = readWebSource(
  'src/components/features/management/cache/CorruptionManager.tsx'
);
const cacheSizeContext = readWebSource('src/contexts/CacheSizeContext.tsx');
const scanBlockedHook = readWebSource('src/hooks/useCacheScanBlocked.ts');
const errorUtils = readWebSource('src/utils/error.ts');
const en = JSON.parse(readWebSource('src/i18n/locales/en.json'));
const zh = JSON.parse(readWebSource('src/i18n/locales/zh.json'));

const readInitializer = (source, name) => {
  const sourceFile = ts.createSourceFile(
    `${name}.tsx`,
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX
  );
  let initializer;

  const visit = (node) => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.name.text === name) {
      initializer = node.initializer?.getText(sourceFile);
      return;
    }
    ts.forEachChild(node, visit);
  };

  visit(sourceFile);
  assert.ok(initializer, `missing ${name} initializer`);
  return new Function(
    'filteredGames',
    'filteredServices',
    'unmappedServices',
    'isLoadingInitialCache',
    `return ${initializer};`
  );
};

const parseTsx = (name, source) =>
  ts.createSourceFile(name, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);

const collectNodes = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

const soleNode = (sourceFile, label, matches) => {
  const found = collectNodes(sourceFile, matches);
  assert.equal(found.length, 1, `expected exactly one ${label}, found ${found.length}`);
  return found[0];
};

const evalExpression = (expression, bindings) => {
  const names = Object.keys(bindings);
  return new Function(...names, `return (${expression});`)(...names.map((name) => bindings[name]));
};

const elementOpening = (element) =>
  ts.isJsxSelfClosingElement(element) ? element : element.openingElement;

const jsxAttributeExpression = (sourceFile, element, attributeName) => {
  const attribute = elementOpening(element).attributes.properties.find(
    (property) => ts.isJsxAttribute(property) && property.name.getText(sourceFile) === attributeName
  );
  assert.ok(attribute, `missing ${attributeName}`);
  assert.ok(ts.isJsxExpression(attribute.initializer), `${attributeName} is not an expression`);
  return attribute.initializer.expression.getText(sourceFile);
};

const jsxGuardExpression = (element) => {
  let current = element.parent;
  while (current && !ts.isBinaryExpression(current)) current = current.parent;
  assert.ok(current, 'missing JSX guard');
  return current.left.getText(element.getSourceFile());
};

const loadOrphanedFetch = () => {
  const orphanedFetchIdRef = { current: 0 };
  const writes = [];
  const pending = [];
  const fetchOrphanedDownloads = bindLifted(
    liftHookCallback(
      'src/components/features/management/sections/StorageSection.tsx',
      'useCallback',
      'getOrphanedDownloads'
    ),
    {
      orphanedFetchIdRef,
      mockMode: false,
      setOrphanedLoading: (value) => writes.push(['loading', value]),
      setOrphanedGroups: (value) => writes.push(['groups', value]),
      setOrphanedLoadError: (value) => writes.push(['error', value]),
      getErrorMessage: (error) => error.message,
      isAbortError: (error) => error instanceof Error && error.name === 'AbortError',
      ApiService: {
        getOrphanedDownloads() {
          return new Promise((resolve, reject) => {
            pending.push({ resolve, reject });
          });
        }
      }
    }
  );
  return { orphanedFetchIdRef, writes, pending, fetchOrphanedDownloads };
};

const mappingPlatforms = [
  ['Depot', 'depotMapping'],
  ['Epic', 'epicMapping'],
  ['Xbox', 'xboxMapping'],
  ['BattleNet', 'battleNetMapping'],
  ['Riot', 'riotMapping']
];

test('all five mapping platforms expose a typed lifecycle triple and shared registry entry', () => {
  for (const [eventPrefix, serviceKey] of mappingPlatforms) {
    for (const suffix of ['Started', 'Progress', 'Complete']) {
      assert.match(signalRTypes, new RegExp(`['"]${eventPrefix}Mapping${suffix}['"]`));
    }

    assert.match(
      registry,
      new RegExp(
        `buildMappingOperationEntry[\\s\\S]*?serviceKey:\\s*'${serviceKey}'[\\s\\S]*?eventPrefix:\\s*'${eventPrefix}Mapping'`
      )
    );
  }
});

test('mapping wire types retain nullable context values and numeric remaining-app progress', () => {
  assert.match(
    signalRTypes,
    /MappingStageContext = Record<string, string \| number \| boolean \| null>/
  );
  assert.match(signalRTypes, /remainingApps\?: number;/);
  assert.doesNotMatch(signalRTypes, /remainingApps\?: number\[\];/);
});

test('mapping registry builder combines tracker recovery with server-operation cancellation', () => {
  assert.match(
    registryEntries,
    /export function buildMappingOperationEntry<[\s\S]*?>\s*\(options: MappingOperationEntryOptions\)/
  );
  assert.match(registryEntries, /\/api\/system\/schedules\/\$\{serviceKey\}\/run-status/);
  assert.match(registryEntries, /cancelKind:\s*'serverOp'/);
});

test('catalog updates are completion-only registry entries', () => {
  const catalogEntries = [
    ['epic_catalog_update', 'EpicGameMappingsUpdated'],
    ['xbox_catalog_update', 'XboxGameMappingsUpdated']
  ];

  for (const [type, event] of catalogEntries) {
    const at = registry.indexOf(`type: '${type}'`);
    assert.notEqual(at, -1, `${type} is missing from the registry`);
    // The window has to reach past the entry's own getters without running into its neighbour.
    // Both patterns below sit in the first ~200 characters, but the Epic entry already
    // overruns into the Xbox one by 4, so widening this is no longer free.
    const entry = registry.slice(at, at + 600);
    assert.match(entry, new RegExp(`events: \\{ complete: '${event}' \\}`));
    assert.match(entry, /succeeded: true/);
  }

  // The lifecycle events of the mapping RUNS keep their own entries and must not be folded into
  // the catalog-update cards, which report a finished merge rather than a run in progress.
  assert.match(registry, /eventPrefix: 'EpicMapping'/);
  assert.match(registry, /eventPrefix: 'XboxMapping'/);
});

test('Xbox mapping authentication uses its non-notification compatibility event', () => {
  assert.match(xboxAuthHook, /XboxMappingAuthStateChanged/);
  assert.doesNotMatch(xboxAuthHook, /XboxMappingProgress/);
});

test('cache-size action uses scan wording without changing the global Refresh label', () => {
  assert.doesNotMatch(cacheManager, /t\('common\.refresh'\)/);
  assert.match(cacheManager, /management\.cache\.refreshCacheSize/);
  assert.equal(en.common.refresh, 'Refresh');
  assert.equal(zh.common.refresh, '刷新');
  assert.equal(en.management.cache.refreshCacheSize, 'Scan cache size');
  assert.equal(zh.management.cache.refreshCacheSize, '扫描缓存大小');
  assert.match(en.management.cache.clickScanToCalculate, /scan/i);
  assert.match(zh.management.cache.clickScanToCalculate, /扫描/);
});

test('successful cache clearing forces all-range dashboard refresh while ordinary events stay live-only', () => {
  assert.match(
    dashboardContext,
    /handleCacheClearingComplete[\s\S]*?if \(!event\.success \|\| event\.cancelled\) return;[\s\S]*?handleForcedRefreshEvent\('CacheClearingComplete'\)/
  );
  assert.match(
    dashboardContext,
    /handleRefreshEvent[\s\S]*?if \(currentTimeRangeRef\.current !== 'live'\) return;/
  );
});

test('successful eviction scan and removal force all-range dashboard refresh like cache clear', () => {
  assert.match(
    dashboardContext,
    /handleEvictionScanComplete[\s\S]*?if \(!event\.success \|\| isSkippedRun\(event\)\) return;[\s\S]*?handleForcedRefreshEvent\('EvictionScanComplete'\)/
  );
  assert.match(
    dashboardContext,
    /handleEvictionRemovalComplete[\s\S]*?if \(!event\.success \|\| event\.cancelled\) return;[\s\S]*?clearDetectionState\(\);[\s\S]*?handleForcedRefreshEvent\('EvictionRemovalComplete'\)/
  );
});

test('records without log history refresh after every event that can change their backing logs', () => {
  const orphanedFetchIdRef = { current: 0 };
  const calls = [];
  const registered = [];
  const unregistered = [];
  const start = bindLifted(
    liftHookCallback(
      'src/components/features/management/sections/StorageSection.tsx',
      'useEffect',
      'DownloadsRefresh'
    ),
    {
      orphanedFetchIdRef,
      fetchOrphanedDownloads: () => {
        calls.push('fetch');
      },
      on: (event, handler) => registered.push([event, handler]),
      off: (event, handler) => unregistered.push([event, handler])
    }
  );
  const cleanup = start();
  for (const [, handler] of registered) handler();
  assert.deepEqual(
    registered.map(([event]) => event),
    ['EvictionScanComplete', 'LogRemovalComplete', 'LogProcessingComplete', 'DownloadsRefresh']
  );
  assert.equal(calls.length, 4);
  cleanup();
  assert.deepEqual(unregistered, registered);
});

test('an older orphan response cannot replace a newer one, fail after it, or publish after cleanup', async () => {
  const newer = loadOrphanedFetch();
  const first = newer.fetchOrphanedDownloads();
  const second = newer.fetchOrphanedDownloads();
  newer.pending[1].resolve({ groups: ['newer'] });
  await second;
  newer.pending[0].resolve({ groups: ['older'] });
  await first;
  assert.deepEqual(newer.writes, [
    ['groups', ['newer']],
    ['error', null],
    ['loading', false]
  ]);

  const staleFailure = loadOrphanedFetch();
  const older = staleFailure.fetchOrphanedDownloads();
  const newerRequest = staleFailure.fetchOrphanedDownloads();
  staleFailure.pending[1].resolve({ groups: ['current'] });
  await newerRequest;
  staleFailure.pending[0].reject(new Error('outdated'));
  await older;
  assert.deepEqual(
    staleFailure.writes.filter(([kind]) => kind === 'error'),
    [['error', null]]
  );
  assert.deepEqual(
    staleFailure.writes.filter(([kind]) => kind === 'groups'),
    [['groups', ['current']]]
  );

  const currentFailure = loadOrphanedFetch();
  const confirmed = currentFailure.fetchOrphanedDownloads();
  currentFailure.pending[0].resolve({ groups: ['kept'] });
  await confirmed;
  const failed = currentFailure.fetchOrphanedDownloads();
  currentFailure.pending[1].reject(new Error('load failed'));
  await failed;
  assert.deepEqual(
    currentFailure.writes.filter(([kind]) => kind === 'groups'),
    [['groups', ['kept']]]
  );
  assert.deepEqual(
    currentFailure.writes.filter(([kind]) => kind === 'error'),
    [
      ['error', null],
      ['error', 'load failed']
    ]
  );

  const cancelled = loadOrphanedFetch();
  const serverCancelled = cancelled.fetchOrphanedDownloads();
  const abortError = new Error('Request cancelled');
  abortError.name = 'AbortError';
  cancelled.pending[0].reject(abortError);
  await serverCancelled;
  const browserCancelled = cancelled.fetchOrphanedDownloads();
  cancelled.pending[1].reject(new DOMException('aborted', 'AbortError'));
  await browserCancelled;
  assert.deepEqual(
    cancelled.writes.filter(([kind]) => kind === 'error'),
    []
  );

  const afterSubscriptionCleanup = loadOrphanedFetch();
  const subscribed = [];
  const inflight = afterSubscriptionCleanup.fetchOrphanedDownloads();
  const stopSubscription = bindLifted(
    liftHookCallback(
      'src/components/features/management/sections/StorageSection.tsx',
      'useEffect',
      'DownloadsRefresh'
    ),
    {
      orphanedFetchIdRef: afterSubscriptionCleanup.orphanedFetchIdRef,
      fetchOrphanedDownloads: afterSubscriptionCleanup.fetchOrphanedDownloads,
      on: (event, handler) => {
        subscribed.push([event, handler]);
      },
      off: (event, handler) => {
        subscribed.push([event, handler]);
      }
    }
  );
  stopSubscription()();
  afterSubscriptionCleanup.pending[0].resolve({ groups: ['late'] });
  await inflight;
  assert.deepEqual(afterSubscriptionCleanup.writes, []);

  const afterInitialCleanup = loadOrphanedFetch();
  const started = [];
  const startInitial = bindLifted(
    liftHookCallback(
      'src/components/features/management/sections/StorageSection.tsx',
      'useEffect',
      'fetchOrphanedDownloads(controller.signal)'
    ),
    {
      orphanedFetchIdRef: afterInitialCleanup.orphanedFetchIdRef,
      fetchOrphanedDownloads: (...args) => {
        const request = afterInitialCleanup.fetchOrphanedDownloads(...args);
        started.push(request);
        return request;
      }
    }
  );
  startInitial()();
  afterInitialCleanup.pending[0].resolve({ groups: ['late'] });
  await started[0];
  assert.deepEqual(afterInitialCleanup.writes, []);
});

test('unmapped-only detection is a visible result without enabling mapped removal actions', () => {
  const hasResults = readInitializer(gameCacheDetector, 'hasResults');
  const actionsPending = readInitializer(gameCacheDetector, 'actionsPending');

  assert.equal(hasResults([], [], [{ service: 'wsus' }]), true);
  assert.equal(hasResults([], [], []), false);
  assert.equal(hasResults([{}], [], null), true);
  assert.equal(hasResults([], [{}], null), true);
  assert.equal(actionsPending([], [], [{ service: 'wsus' }], false), true);
  assert.equal(actionsPending([{}], [], null, false), false);
});

test('unmapped groups count, summarize, and stay visible without mapped removal or the datasource banner', () => {
  const sourceFile = parseTsx('GameCacheDetector.tsx', gameCacheDetector);
  const hasResults = readInitializer(gameCacheDetector, 'hasResults');
  const actionsPending = readInitializer(gameCacheDetector, 'actionsPending');
  const unmappedOnly = hasResults([], [], [{ total_bytes: 10 }]);
  const openingElement = (tagName, needle) => {
    const found = collectNodes(sourceFile, (node) => {
      if (!ts.isJsxElement(node) && !ts.isJsxSelfClosingElement(node)) return false;
      return (
        elementOpening(node).tagName.getText(sourceFile) === tagName &&
        node.getText(sourceFile).includes(needle)
      );
    });
    assert.ok(found.length > 0, `missing ${tagName} containing ${needle}`);
    found.sort((left, right) => left.getWidth() - right.getWidth());
    return found[0];
  };

  const outerCount = jsxAttributeExpression(
    sourceFile,
    openingElement('AccordionSection', "management.gameDetection.title')"),
    'count'
  );
  assert.equal(
    evalExpression(outerCount, {
      hasResults: true,
      filteredGames: [{}, {}],
      filteredServices: [{}],
      unmappedServices: [{}, {}, {}]
    }),
    6
  );
  assert.equal(
    evalExpression(outerCount, {
      hasResults: unmappedOnly,
      filteredGames: [],
      filteredServices: [],
      unmappedServices: [{ total_bytes: 10 }]
    }),
    1
  );
  assert.equal(
    evalExpression(outerCount, {
      hasResults: false,
      filteredGames: [],
      filteredServices: [],
      unmappedServices: []
    }),
    undefined
  );

  const summaryGuard = soleNode(
    sourceFile,
    'previous scan guard',
    (node) =>
      ts.isBinaryExpression(node) &&
      node.left.getText(sourceFile) === 'lastDetectionTime' &&
      node.right.getText(sourceFile) === 'hasResults'
  ).getText(sourceFile);
  assert.equal(
    Boolean(
      evalExpression(summaryGuard, {
        lastDetectionTime: '2026-09-21T00:00:00Z',
        hasResults: unmappedOnly
      })
    ),
    true
  );
  assert.equal(
    Boolean(evalExpression(summaryGuard, { lastDetectionTime: null, hasResults: unmappedOnly })),
    false
  );

  const unmappedStat = openingElement('div', 'unmappedSection');
  const unmappedCounts = collectNodes(
    sourceFile,
    (node) => ts.isJsxExpression(node) && node.getText(sourceFile) === '{unmappedServices.length}'
  );
  assert.equal(unmappedCounts.length, 2);
  for (const unmappedCount of unmappedCounts) {
    assert.equal(
      evalExpression(unmappedCount.expression.getText(sourceFile), {
        unmappedServices: [{ total_bytes: 10 }, { total_bytes: 25 }, { total_bytes: 5 }]
      }),
      3
    );
  }
  const byteTotal = soleNode(
    sourceFile,
    'unmapped byte total',
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile).endsWith('reduce') &&
      node.getText(sourceFile).includes('total_bytes')
  );
  assert.equal(
    evalExpression(byteTotal.getText(sourceFile), {
      unmappedServices: [{ total_bytes: 10 }, { total_bytes: 25 }]
    }),
    35
  );
  assert.equal(jsxGuardExpression(unmappedStat), 'unmappedServices !== null');
  assert.equal(
    evalExpression(jsxGuardExpression(openingElement('AccordionSection', 'unmappedSection')), {
      unmappedServices: []
    }),
    true
  );
  assert.equal(
    evalExpression(jsxGuardExpression(openingElement('AccordionSection', 'unmappedSection')), {
      unmappedServices: null
    }),
    false
  );

  const bannerGuard = jsxGuardExpression(
    openingElement('Alert', 'management.gameDetection.filteredBy')
  );
  assert.equal(
    Boolean(
      evalExpression(bannerGuard, {
        selectedDatasource: 'steam',
        filteredGames: [],
        filteredServices: []
      })
    ),
    false
  );
  assert.equal(
    Boolean(
      evalExpression(bannerGuard, {
        selectedDatasource: 'steam',
        filteredGames: [{}],
        filteredServices: []
      })
    ),
    true
  );

  const emptyGuard = jsxGuardExpression(openingElement('EmptyState', 'emptyState.noGamesServices'));
  assert.equal(
    evalExpression(emptyGuard, {
      hasResults: unmappedOnly,
      loading: false,
      loadError: null
    }),
    false
  );
  assert.equal(
    evalExpression(emptyGuard, {
      hasResults: hasResults([], [], []),
      loading: false,
      loadError: null
    }),
    true
  );
  assert.equal(
    evalExpression(emptyGuard, { hasResults: false, loading: true, loadError: null }),
    false
  );
  assert.equal(
    evalExpression(emptyGuard, { hasResults: false, loading: false, loadError: 'failed' }),
    false
  );

  const disclosureDisabled = jsxAttributeExpression(
    sourceFile,
    openingElement('ActionMenuItem', 'management.gameDetection.expandAll'),
    'disabled'
  );
  const removalDisabled = jsxAttributeExpression(
    sourceFile,
    openingElement('ActionMenuDangerItem', 'management.sections.data.gameCacheRemoveAll'),
    'disabled'
  );
  const openLoadedSection = {
    isLoadingInitialCache: false,
    hasResults: unmappedOnly,
    sectionExpanded: true,
    actionsPending: actionsPending([], [], [{ total_bytes: 10 }], false)
  };
  assert.equal(evalExpression(disclosureDisabled, openLoadedSection), false);
  assert.equal(
    evalExpression(disclosureDisabled, { ...openLoadedSection, isLoadingInitialCache: true }),
    true
  );
  assert.equal(
    evalExpression(disclosureDisabled, { ...openLoadedSection, hasResults: false }),
    true
  );
  assert.equal(
    evalExpression(disclosureDisabled, { ...openLoadedSection, sectionExpanded: false }),
    true
  );
  assert.equal(
    evalExpression(removalDisabled, {
      actionsPending: openLoadedSection.actionsPending,
      loading: false,
      mockMode: false,
      diskActionBlocked: false,
      checkingPermissions: false,
      isCacheRemovalActive: false,
      removeAllRunning: false,
      diskObjectsAvailable: true,
      allNginxReopenGate: { available: true }
    }),
    true
  );
  assert.equal(
    evalExpression(removalDisabled, {
      actionsPending: actionsPending([{}], [], null, false),
      loading: false,
      mockMode: false,
      diskActionBlocked: false,
      checkingPermissions: false,
      isCacheRemovalActive: false,
      removeAllRunning: false,
      diskObjectsAvailable: true,
      allNginxReopenGate: { available: true }
    }),
    false
  );
});

test('a declined run does not trigger a refetch in any completion listener', () => {
  // A declined run reports success:true, so a !success guard lets it through and every one of
  // these listeners would refetch for work that never happened.
  assert.match(signalRTypes, /export function isSkippedRun\([\s\S]*?status === 'skipped'/);
  assert.match(
    dashboardContext,
    /handleGameDetectionComplete[\s\S]*?if \(isSkippedRun\(event\)\) return;/
  );
  assert.match(
    gameCacheDetector,
    /handleDetectionComplete[\s\S]*?if \(isSkippedRun\(event\)\) return;/
  );
  assert.match(
    gameCacheDetector,
    /handleEvictionStateChanged[\s\S]*?if \(isSkippedRun\(event\)\) return;/
  );
  assert.match(storageSection, /handleScanDone[\s\S]*?if \(isSkippedRun\(event\) \|\|/);
});

test('a declined game detection releases the section instead of leaving it scanning', () => {
  // The server's run list owns the busy state, and a declined run leaves it like any other ending.
  // The page's own guard lasts only as long as its start request, so nothing is left holding.
  assert.match(
    gameCacheDetector,
    /finally \{\s*detectionInFlightRef\.current = false;\s*setIsStartingDetection\(false\);/
  );
  assert.doesNotMatch(gameCacheDetector, /scanButtonsHeld|heldOperationIdRef|followAdmittedScan/);
  assert.doesNotMatch(storageSection, /evictionAdmissionHeld|recoverScanHold|followAdmittedScan/);
});

test('a terminal card from an earlier scan cannot end the one just started', () => {
  // No card list drives the scan state, so an old scan's ending on screen cannot touch the new
  // scan's buttons: busy comes from the live run list alone.
  assert.match(gameCacheDetector, /const isDetectionFromNotification = useOperationBusy\(/);
  assert.doesNotMatch(gameCacheDetector, /notifications\.(filter|find|some)\(/);
  assert.doesNotMatch(gameCacheDetector, /gameDetectionEndedNotifs|raisedByThisScan/);
});

test('scan buttons gate on the unfiltered server answer, not the filtered snapshot', () => {
  // The speed snapshot drops hidden clients, but their bytes still reach the cache, so gating on
  // it left every scan button enabled during a hidden client's download.
  assert.match(scanBlockedHook, /ApiService\.getCacheScanBlocked\(\)/);
  assert.match(scanBlockedHook, /on\('CacheScanBlockedChanged'/);
  // Before the first answer the gate is neither open nor blocked, so no control is offered on a
  // claim nothing has made and none of them shows the download sentence.
  assert.match(scanBlockedHook, /useState<CacheScanAnswer>\('checking'\)/);
  assert.match(signalRTypes, /'CacheScanBlockedChanged'/);
  for (const source of [gameCacheDetector, corruptionManager, storageSection, cacheManager]) {
    assert.match(source, /const scanGate = useCacheScanBlocked\(\);/);
    assert.doesNotMatch(source, /hasActiveDownloads/);
  }
});

test('only a route whose sole 400 is the decline is allowed to soften one', () => {
  assert.match(errorUtils, /export function isRefusal\(error: unknown\): error is ApiError/);
  assert.match(errorUtils, /isRefusal[\s\S]*?error\.status === 400/);

  // The cache-size read is the one route whose only 400 is the download denial; its
  // authorization failures are 401 and 403.
  assert.match(cacheSizeContext, /if \(isRefusal\(err\)\)[\s\S]*?setDenialReason/);

  // The three scan starts each answer an identical 400 for a refusal and for a fleet whose
  // datasources disagree about their cache-key scheme. Softening one hides the other, and the
  // configuration failure is the one that must not be hidden.
  for (const source of [gameCacheDetector, storageSection, corruptionManager]) {
    assert.doesNotMatch(source, /isRefusal/);
  }
});
