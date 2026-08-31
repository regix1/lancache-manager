import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { MemoryStorage, transpile } from './transpile-module.mjs';

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const signalRTypes = readWebSource('src/contexts/SignalRContext/types.ts');
const registry = readWebSource('src/contexts/notifications/notificationRegistry.ts');
const registryEntries = readWebSource('src/contexts/notifications/registryEntries.ts');
const handlers = readWebSource('src/contexts/notifications/handlers.ts');
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

const mappingPlatforms = [
  ['Depot', 'depotMapping'],
  ['Epic', 'epicMapping'],
  ['Xbox', 'xboxMapping'],
  ['BattleNet', 'battleNetMapping'],
  ['Riot', 'riotMapping']
];

const compileHandlerFactory = () => {
  const sourceFile = ts.createSourceFile(
    'handlers.ts',
    handlers,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS
  );
  const names = new Set([
    'eventOperationId',
    'eventTargetsCard',
    'clearPersistedNotificationIfTargeted',
    'createCompletionHandler'
  ]);
  const declarations = sourceFile.statements
    .filter(
      (statement) =>
        ts.isFunctionDeclaration(statement) && statement.name && names.has(statement.name.text)
    )
    .map((statement) => statement.getText(sourceFile));

  for (const name of names) {
    assert.ok(
      declarations.some((declaration) => declaration.includes(` ${name}`)),
      `missing ${name} from handler factory harness`
    );
  }

  const harness = `
    const FULL_PROGRESS_PERCENT = 100;
    const CANCELLED_NOTIFICATION_DELAY_MS = 5000;
    const GENERIC_COMPLETION_I18N_KEY = 'complete';
    const GENERIC_FAILURE_I18N_KEY = 'failed';
    const i18n = { t: (key) => key };
    const isTerminalNotificationStatus = (status) =>
      status === 'completed' || status === 'failed' || status === 'cancelled';
    ${declarations.join('\n')}
  `;
  const compiled = transpile(harness, ts.ModuleKind.CommonJS);
  const exports = {};
  // The lifted declarations read the storage wrapper as a free variable; MemoryStorage covers the
  // three methods they call.
  const storage = new MemoryStorage();
  const createCompletionHandler = new Function(
    'exports',
    'storage',
    `${compiled}; return exports.createCompletionHandler;`
  )(exports, storage);

  return { createCompletionHandler, localStorage: storage };
};

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
  assert.match(registryEntries, /silentRunGate:\s*true/);
});

test('catalog updates are completion-only registry entries carrying their own card identity', () => {
  const catalogEntries = [
    ['epic_catalog_update', 'EPIC_GAME_MAPPING_UPDATE', 'EpicGameMappingsUpdated'],
    ['xbox_catalog_update', 'XBOX_GAME_MAPPING_UPDATE', 'XboxGameMappingsUpdated']
  ];

  for (const [type, idConstant, event] of catalogEntries) {
    const at = registry.indexOf(`type: '${type}'`);
    assert.notEqual(at, -1, `${type} is missing from the registry`);
    // The window has to reach past the entry's own getters without running into its neighbour.
    // All three patterns below sit in the first ~200 characters, but the Epic entry already
    // overruns into the Xbox one by 4, so widening this is no longer free.
    const entry = registry.slice(at, at + 600);
    assert.match(entry, new RegExp(`id: NOTIFICATION_IDS\\.${idConstant}\\b`));
    assert.match(entry, new RegExp(`events: \\{ complete: '${event}' \\}`));
    assert.match(entry, /succeeded: true/);
  }

  // The lifecycle events of the mapping RUNS keep their own entries and must not be folded into
  // the catalog-update cards, which report a finished merge rather than a run in progress.
  assert.match(registry, /eventPrefix: 'EpicMapping'/);
  assert.match(registry, /eventPrefix: 'XboxMapping'/);
});

test('running progress updates persist the merged notification for reload recovery', () => {
  assert.match(
    handlers,
    /const updatedNotification[\s\S]*?storage\.setItem\(\s*config\.storageKey,\s*JSON\.stringify\(updatedNotification\)[\s\S]*?return updatedNotification/
  );
});

test('stale mapping Complete cannot mutate or clear a newer running operation', () => {
  for (const silent of [false, true]) {
    const { createCompletionHandler, localStorage } = compileHandlerFactory();
    const storageKey = `mapping-stale-${silent}`;
    const notificationId = 'mapping-card';
    const running = {
      id: notificationId,
      type: 'depot_mapping',
      status: 'running',
      message: 'Mapping B',
      progress: 25,
      startedAt: new Date('2026-07-25T00:00:00.000Z'),
      details: { operationId: 'operation-b' }
    };
    let notifications = [running];
    const scheduledDismissals = [];
    localStorage.setItem(storageKey, JSON.stringify(running));

    const handler = createCompletionHandler(
      {
        type: 'depot_mapping',
        getId: () => notificationId,
        storageKey,
        shouldDisplay: (event) => event.showNotification,
        getSuccessDetails: (event) => ({ operationId: event.operationId })
      },
      (update) => {
        notifications = update(notifications);
      },
      (...args) => scheduledDismissals.push(args)
    );

    handler({
      success: true,
      operationId: 'operation-a',
      showNotification: !silent
    });

    assert.deepEqual(notifications, [running]);
    assert.equal(localStorage.getItem(storageKey), JSON.stringify(running));
    assert.deepEqual(scheduledDismissals, []);

    handler({
      success: true,
      operationId: 'operation-b',
      showNotification: !silent
    });

    if (silent) {
      assert.deepEqual(notifications, []);
    } else {
      assert.equal(notifications[0].status, 'completed');
      assert.equal(notifications[0].details.operationId, 'operation-b');
    }
    assert.equal(localStorage.getItem(storageKey), null);
    assert.deepEqual(scheduledDismissals, silent ? [] : [[notificationId, undefined]]);
  }
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
  // The terminal filter that clears isStartingDetection and detectionInFlightRef must treat a
  // declined run as terminal. Without it the card says skipped while the section stays in its
  // loader with both scan buttons disabled, and only a reload frees it.
  assert.match(
    gameCacheDetector,
    /gameDetectionEndedNotifs = notifications\.filter\([\s\S]*?'skipped'[\s\S]*?\);[\s\S]*?detectionInFlightRef\.current = false;/
  );
});

test('a terminal card from an earlier scan cannot end the one just started', () => {
  // One card slot per type, so a leftover terminal card is still in the list when the next scan
  // starts. Both terminal filters must age it out, or it clears the in-flight guard mid-request
  // and a second click starts a second scan. This guards all four terminal states, not just one.
  assert.match(gameCacheDetector, /scanStartedAtRef\.current = Date\.now\(\);/);
  assert.match(
    gameCacheDetector,
    /raisedByThisScan = \(n: UnifiedNotification\) =>[\s\S]*?n\.startedAt\.getTime\(\) >= scanStartedAtRef\.current/
  );
  assert.match(
    gameCacheDetector,
    /gameDetectionNotifs = notifications\.filter\([\s\S]*?'completed'[\s\S]*?raisedByThisScan\(n\)/
  );
  assert.match(
    gameCacheDetector,
    /gameDetectionEndedNotifs = notifications\.filter\([\s\S]*?raisedByThisScan\(n\)/
  );
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
