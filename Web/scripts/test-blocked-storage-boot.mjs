import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  liftHookCallback,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * A browser with site data blocked for the origin throws on the `localStorage` property access
 * itself, not on a missing key. Every raw reader in a render path therefore throws mid-render and
 * the app-wide ErrorBoundary replaces the page, which is why boot, the Management tab and the
 * notification handlers all read through the storage wrapper instead.
 *
 * The throwing getter installed below stands in for that browser: the wrapper probes availability
 * once inside a try when its module is first imported, so it has to be imported after the getter is
 * in place for the fallback path to be the one under test.
 */

Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  get() {
    throw new DOMException('Access to storage is not allowed from this context.', 'SecurityError');
  }
});

const { storage } = await import(await compileToUrl('../src/utils/storage.ts'));

/** The keys module the section initializers read, compiled from real source. */
const { MANAGEMENT_STORAGE_KEYS } = await import(
  await compileToUrl('../src/components/features/management/sections/managementStorageKeys.ts')
);

// ── The two bespoke initializers ─────────────────────────────────────────────
//
// Boot renders the Dashboard, and the Management tab sits above the per-section error boundary, so
// a throw in either takes out the whole app rather than one card. Neither reads a plain on/off flag,
// which is why they are not on the shared hook below.

const BESPOKE = [
  {
    label: 'Dashboard card layout',
    path: 'src/components/features/dashboard/Dashboard.tsx',
    keyText: "'dashboard-card-layout'",
    fallback: 'balanced',
    stored: ['dashboard-card-layout', '3-column'],
    storedResult: '3-column'
  },
  {
    label: 'Management active section',
    path: 'src/components/features/management/ManagementTab.tsx',
    keyText: "'management-active-section'",
    fallback: 'settings',
    stored: ['management-active-section', 'schedules'],
    storedResult: 'schedules'
  }
];

for (const site of BESPOKE) {
  test(`${site.label} falls back to its default when storage is blocked`, () => {
    const initializer = bindLifted(liftHookCallback(site.path, 'useState', site.keyText), {
      storage
    });
    assert.equal(initializer(), site.fallback);
  });

  test(`${site.label} still reads its stored value when storage works`, () => {
    const values = new Map([site.stored]);
    const initializer = bindLifted(liftHookCallback(site.path, 'useState', site.keyText), {
      storage: { getItem: (key) => values.get(key) ?? null }
    });
    assert.equal(initializer(), site.storedResult);
  });
}

// ── The eight sections, on one shared initializer ────────────────────────────
//
// Each section used to carry its own copy of this. They now call useSectionExpanded, so the
// initializer is checked once and each section is checked for the pair it hands over. The defaults
// genuinely differ, so a shared assertion would prove nothing about any single section.

const sectionInitializer = liftHookCallback(
  'src/hooks/useSectionExpanded.ts',
  'useState',
  'storage.getItem(key)'
);

const SECTIONS = [
  {
    label: 'disk cache',
    path: 'src/components/features/management/cache/CacheManager.tsx',
    keyText: "'management-disk-cache-expanded'",
    key: 'management-disk-cache-expanded',
    defaultExpanded: false
  },
  {
    label: 'corruption',
    path: 'src/components/features/management/cache/CorruptionManager.tsx',
    keyText: "'management-corruption-expanded'",
    key: 'management-corruption-expanded',
    defaultExpanded: false
  },
  {
    label: 'evicted data',
    path: 'src/components/features/management/sections/StorageSection.tsx',
    keyText: 'MANAGEMENT_STORAGE_KEYS.EVICTED_DATA_EXPANDED',
    key: MANAGEMENT_STORAGE_KEYS.EVICTED_DATA_EXPANDED,
    defaultExpanded: false
  },
  {
    label: 'eviction settings',
    path: 'src/components/features/management/sections/StorageSection.tsx',
    keyText: 'MANAGEMENT_STORAGE_KEYS.EVICTION_SETTINGS_EXPANDED',
    key: MANAGEMENT_STORAGE_KEYS.EVICTION_SETTINGS_EXPANDED,
    defaultExpanded: true
  },
  {
    label: 'evicted items',
    path: 'src/components/features/management/sections/StorageSection.tsx',
    keyText: 'MANAGEMENT_STORAGE_KEYS.EVICTED_ITEMS_EXPANDED',
    key: MANAGEMENT_STORAGE_KEYS.EVICTED_ITEMS_EXPANDED,
    defaultExpanded: true
  },
  {
    label: 'game cache',
    path: 'src/components/features/management/game-detection/GameCacheDetector.tsx',
    keyText: 'MANAGEMENT_STORAGE_KEYS.GAME_CACHE_EXPANDED',
    key: MANAGEMENT_STORAGE_KEYS.GAME_CACHE_EXPANDED,
    defaultExpanded: false
  },
  {
    label: 'log removal',
    path: 'src/components/features/management/log-processing/LogRemovalManager.tsx',
    keyText: "'management-log-removal-expanded'",
    key: 'management-log-removal-expanded',
    defaultExpanded: false
  },
  {
    label: 'datasources',
    path: 'src/components/features/management/datasources/DatasourcesInfo.tsx',
    keyText: "'management-datasources-expanded-v2'",
    key: 'management-datasources-expanded-v2',
    defaultExpanded: false
  }
];

/** The two arguments a section hands the shared hook, as they are written in its source. */
const readSectionCall = (path, keyText) => {
  const sourceFile = parseSource(path, ts.ScriptKind.TSX);
  const calls = collectNodes(
    sourceFile,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useSectionExpanded' &&
      node.arguments.length === 2 &&
      node.arguments[0].getText(sourceFile) === keyText
  );
  assert.equal(calls.length, 1, `expected exactly one useSectionExpanded(${keyText}) in ${path}`);
  return calls[0].arguments.map((argument) => argument.getText(sourceFile));
};

for (const section of SECTIONS) {
  test(`the ${section.label} section falls back to its own default when storage is blocked`, () => {
    const initializer = bindLifted(sectionInitializer, {
      storage,
      key: section.key,
      defaultExpanded: section.defaultExpanded
    });
    assert.equal(initializer(), section.defaultExpanded);
  });

  test(`the ${section.label} section reads its stored value when storage works`, () => {
    // Store the opposite of the default, so a hook that ignored storage would still fail here.
    const stored = !section.defaultExpanded;
    const initializer = bindLifted(sectionInitializer, {
      storage: { getItem: (key) => (key === section.key ? String(stored) : null) },
      key: section.key,
      defaultExpanded: section.defaultExpanded
    });
    assert.equal(initializer(), stored);
  });

  test(`the ${section.label} section asks for its own key and default`, () => {
    assert.deepEqual(readSectionCall(section.path, section.keyText), [
      section.keyText,
      String(section.defaultExpanded)
    ]);
  });
}

test('a started notification still opens its card when storage is blocked', async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const storageUrl = await compileToUrl('../src/utils/storage.ts');
  const { createStartedHandler } = await import(
    await compileToUrl('../src/contexts/notifications/handlers.ts', {
      './constants': constantsUrl,
      './notificationStatus': statusUrl,
      '@utils/storage': storageUrl,
      '@/i18n': moduleUrl(`export default { t: (key) => key };`)
    })
  );

  let state = [];
  const handler = createStartedHandler(
    {
      type: 'cache_clear',
      getId: () => 'cache_clear_card',
      storageKey: 'test-cache-clear',
      defaultMessage: 'Clearing...'
    },
    (update) => {
      state = update(state);
    }
  );

  handler({ operationId: 'op-1' });

  assert.equal(state.length, 1);
  assert.equal(state[0].status, 'running');
});
