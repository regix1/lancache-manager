import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const signalRTypes = readWebSource('src/contexts/SignalRContext/types.ts');
const registry = readWebSource('src/contexts/notifications/notificationRegistry.ts');
const registryBuilders = readWebSource('src/contexts/notifications/registryBuilders.ts');
const specialContracts = readWebSource(
  'src/contexts/notifications/specialNotificationContracts.ts'
);
const handlerFactories = readWebSource('src/contexts/notifications/handlerFactories.ts');
const xboxAuthHook = readWebSource('src/hooks/useXboxMappingAuth.ts');
const cacheManager = readWebSource('src/components/features/management/cache/CacheManager.tsx');
const dashboardContext = readWebSource('src/contexts/DashboardDataContext/index.tsx');
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
    'handlerFactories.ts',
    handlerFactories,
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
  const localStorage = new MemoryStorage();
  const createCompletionHandler = new Function(
    'exports',
    'localStorage',
    `${compiled}; return exports.createCompletionHandler;`
  )(exports, localStorage);

  return { createCompletionHandler, localStorage };
};

class MemoryStorage {
  #values = new Map();

  getItem(key) {
    return this.#values.get(key) ?? null;
  }

  setItem(key, value) {
    this.#values.set(key, String(value));
  }

  removeItem(key) {
    this.#values.delete(key);
  }
}

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
    registryBuilders,
    /export function buildMappingOperationEntry<[\s\S]*?>\s*\(options: MappingOperationEntryOptions\)/
  );
  assert.match(registryBuilders, /\/api\/system\/schedules\/\$\{serviceKey\}\/run-status/);
  assert.match(registryBuilders, /cancelKind:\s*'serverOp'/);
  assert.match(registryBuilders, /silentRunGate:\s*true/);
});

test('special handlers retain data-update events but no mapping lifecycle reducers', () => {
  assert.match(specialContracts, /EpicGameMappingsUpdated/);
  assert.match(specialContracts, /XboxGameMappingsUpdated/);
  assert.doesNotMatch(
    specialContracts,
    /DepotMapping(?:Started|Progress|Complete)|EpicMappingProgress|XboxMappingProgress/
  );
  assert.match(specialContracts, /EpicGameMappingsUpdated/);
  assert.match(specialContracts, /XboxGameMappingsUpdated/);
  assert.match(
    readWebSource('src/contexts/notifications/specialCaseHandlers.ts'),
    /EPIC_GAME_MAPPING_UPDATE/
  );
  assert.match(
    readWebSource('src/contexts/notifications/specialCaseHandlers.ts'),
    /XBOX_GAME_MAPPING_UPDATE/
  );
});

test('running progress updates persist the merged notification for reload recovery', () => {
  assert.match(
    handlerFactories,
    /const updatedNotification[\s\S]*?localStorage\.setItem\(\s*config\.storageKey,\s*JSON\.stringify\(updatedNotification\)[\s\S]*?return updatedNotification/
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
    /handleEvictionScanComplete[\s\S]*?if \(!event\.success\) return;[\s\S]*?handleForcedRefreshEvent\('EvictionScanComplete'\)/
  );
  assert.match(
    dashboardContext,
    /handleEvictionRemovalComplete[\s\S]*?if \(!event\.success \|\| event\.cancelled\) return;[\s\S]*?clearDetectionState\(\);[\s\S]*?handleForcedRefreshEvent\('EvictionRemovalComplete'\)/
  );
});
