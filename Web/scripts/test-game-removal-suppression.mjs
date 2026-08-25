import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, transpile } from './transpile-module.mjs';

/**
 * Regression test for the per-item removal card that should stay hidden while the
 * bulk card whose OWN items produce that notification type is running, but must
 * NOT be swallowed by an unrelated bulk card (a different batch, or the same batch
 * running a different item type). Compiles handlers.ts from real source so
 * the suppression check under test is the one that ships. Covers all four batch/
 * item-type pairs: the cache batch's game and service items, the evicted batch,
 * and the log batch.
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

const I18N_STUB = moduleUrl(`export default { t: (key) => key };`);

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

const loadHandlerFactories = async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const handlersUrl = await compileToUrl('../src/contexts/notifications/handlers.ts', {
    './constants': constantsUrl,
    './notificationStatus': statusUrl,
    '@/i18n': I18N_STUB
  });
  return await import(handlersUrl);
};

/** Runs a Started event for `type` against a fresh card list holding one running bulk card. */
const runStartedForType = async (type, bulkCardItemTypes) => {
  globalThis.localStorage = new MemoryStorage();
  const { createStartedHandler } = await loadHandlerFactories();

  let state = [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      startedAt: new Date(),
      details: { itemTypes: bulkCardItemTypes }
    }
  ];
  const setNotifications = (update) => {
    state = update(state);
  };

  const handler = createStartedHandler(
    {
      type,
      getId: () => `${type}_card`,
      storageKey: `test-${type}`,
      defaultMessage: 'Removing...'
    },
    setNotifications
  );

  handler({});
  return state;
};

const hasCard = (state, type) => state.some((n) => n.type === type);

const CASES = [
  ['game_removal', 'cache batch, game items'],
  ['service_removal', 'cache batch, service items'],
  ['eviction_removal', 'evicted batch'],
  ['log_removal', 'log batch']
];

for (const [type, label] of CASES) {
  test(`${label}: an unrelated running batch does not suppress the ${type} card`, async () => {
    const state = await runStartedForType(type, []);
    assert.ok(hasCard(state, type), `${type} card should open when no running batch owns it`);
  });

  test(`${label}: the owning bulk card suppresses the ${type} card`, async () => {
    const state = await runStartedForType(type, [type]);
    assert.ok(!hasCard(state, type), `${type} card should stay suppressed while its batch runs`);
  });
}

test('a batch declaring two item types suppresses both and leaves other types alone', async () => {
  const bulkCardItemTypes = ['game_removal', 'service_removal'];
  assert.ok(
    !hasCard(await runStartedForType('game_removal', bulkCardItemTypes), 'game_removal'),
    'game_removal is one of the batch item types, so it should stay suppressed'
  );
  assert.ok(
    !hasCard(await runStartedForType('service_removal', bulkCardItemTypes), 'service_removal'),
    'service_removal is the other batch item type, so it should also stay suppressed'
  );
  assert.ok(
    hasCard(await runStartedForType('eviction_removal', bulkCardItemTypes), 'eviction_removal'),
    "eviction_removal is not one of this batch's item types, so its card should still open"
  );
  assert.ok(
    hasCard(await runStartedForType('log_removal', bulkCardItemTypes), 'log_removal'),
    "log_removal is not one of this batch's item types, so its card should still open"
  );
});

/**
 * The Started handler is not the only place the marker gates a card: a queued item that has
 * not started yet renders a purple waiting card through a separate handler in
 * useNotificationHandlers.ts, and that handler needs the same guard or a queued item would
 * flash a waiting card beside the batch card. Lift the handler straight out of its source (it
 * lives inside a hook's useEffect and is not exported) and run it with its free variables
 * supplied directly, the same way test-context-resync-wiring.mjs runs a recovery callback
 * lifted out of a hook call.
 */
const parseTs = (relativePath) => {
  const source = readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');
  return ts.createSourceFile(relativePath, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
};

const collectNodes = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

/** Source text of the arrow function assigned to a top-level `const <constName> = (...) => {...}`. */
const liftConstArrow = (relativePath, constName) => {
  const sourceFile = parseTs(relativePath);
  const found = collectNodes(
    sourceFile,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === constName &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  );
  assert.equal(found.length, 1, `expected exactly one ${constName} declaration in ${relativePath}`);
  return found[0].initializer.getText(sourceFile);
};

/** Compiles a lifted arrow function and binds its free variables by name. */
const bindLifted = (arrowSource, bindings) => {
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${arrowSource});`, ts.ModuleKind.CommonJS);
  return new Function(...names, `${compiled}\nreturn lifted;`)(
    ...names.map((name) => bindings[name])
  );
};

/** Runs waitingHandler for one queued event of `type` against a list holding one bulk card. */
const runWaitingHandler = async (type, bulkNotification) => {
  const { findBulkCardOwningType } = await loadHandlerFactories();

  const arrowSource = liftConstArrow(
    'src/contexts/notifications/useNotificationHandlers.ts',
    'waitingHandler'
  );

  let state = [bulkNotification];
  const setNotifications = (updater) => {
    state = updater(state);
  };

  const waitingHandler = bindLifted(arrowSource, {
    registry: [],
    findEntryForWireType: () => ({ type, id: `${type}_card` }),
    cancelAutoDismissTimer: () => undefined,
    setNotifications,
    findBulkCardOwningType,
    waitingCardMessage: (source) =>
      source.blockedByName ? `waiting for ${source.blockedByName}` : 'waiting'
  });

  waitingHandler({
    operationType: 'irrelevant-for-this-test',
    operationId: 'op-1',
    blockedByName: 'Cache File Scan'
  });
  return state;
};

test('a queued item opens its waiting card when no owning bulk card is running', async () => {
  const state = await runWaitingHandler('game_removal', {
    id: 'bulk_removal_x',
    type: 'bulk_removal',
    status: 'running',
    startedAt: new Date(),
    details: { itemTypes: ['eviction_removal'] }
  });
  assert.ok(
    state.some((n) => n.id === 'game_removal_card' && n.status === 'waiting'),
    'a queued item should get its own waiting card when nothing suppresses it'
  );
});

test('the owning bulk card suppresses the queued waiting card too, not just the started card', async () => {
  const state = await runWaitingHandler('game_removal', {
    id: 'bulk_removal_x',
    type: 'bulk_removal',
    status: 'running',
    startedAt: new Date(),
    details: { itemTypes: ['service_removal', 'game_removal'] }
  });
  assert.ok(
    !state.some((n) => n.id === 'game_removal_card' && n.status === 'waiting'),
    'the waiting card must stay suppressed while the owning bulk card is running'
  );
  // Suppressing the card must not also swallow the blocker's name: it is the only thing that
  // explains why the batch is sitting still, and the batch card cannot work it out on its own.
  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.message,
    'waiting for Cache File Scan',
    'the batch card must name the blocking operation while its item is parked'
  );
});

test('a batch still owns its item cards after it turns purple while queued', async () => {
  const { findBulkCardOwningType } = await loadHandlerFactories();
  // The batch card goes to 'waiting' while its current item is parked behind another operation.
  // It is still the owner: if this stopped matching, the queue's own card would reappear next to
  // it and the user would see the same sentence twice, which is the bug this whole pair prevents.
  const parkedBatch = [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'waiting',
      details: { itemTypes: ['service_removal', 'game_removal'] }
    }
  ];
  assert.ok(
    findBulkCardOwningType('game_removal', parkedBatch),
    'a parked batch must still own the item types it declared'
  );
  assert.equal(
    findBulkCardOwningType('log_removal', parkedBatch),
    undefined,
    'it must not claim a type it never declared'
  );
});

test('a batch whose items are a different type keeps its own message', async () => {
  const state = await runWaitingHandler('game_removal', {
    id: 'bulk_removal_x',
    type: 'bulk_removal',
    status: 'running',
    message: 'Removing 1 of 2 - Arma 3',
    startedAt: new Date(),
    details: { itemTypes: ['eviction_removal'] }
  });
  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.message,
    'Removing 1 of 2 - Arma 3',
    'an unrelated batch must not have its message overwritten by another queue blocker'
  );
});

/**
 * A batch card is only quiet because it declares which per-item notification types its own items
 * produce. Nothing in the type system requires that: a fourth batch added later without
 * `details.itemTypes` duplicates every per-item card and every behaviour test above still passes,
 * because those tests supply the declaration themselves rather than reading it from source.
 *
 * So read it from source. Every `addNotification({ type: 'bulk_removal', ... })` in the app must
 * carry a literal `itemTypes`, and the set of types declared across all batches must match the
 * CASES the behaviour tests exercise - a new batch type with no behaviour test fails here, and a
 * behaviour test for a type no batch declares fails here too.
 */
const sourceFilesUnder = (dirUrl) => {
  const found = [];
  const walk = (url) => {
    for (const entry of readdirSync(url, { withFileTypes: true })) {
      const child = new URL(`${entry.name}${entry.isDirectory() ? '/' : ''}`, url);
      if (entry.isDirectory()) {
        walk(child);
      } else if (/\.tsx?$/.test(entry.name)) {
        found.push(child);
      }
    }
  };
  walk(dirUrl);
  return found;
};

/** The object literal passed to an `addNotification({ type: 'bulk_removal', ... })` call. */
const bulkCardLiterals = (sourceFile) =>
  collectNodes(
    sourceFile,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile).endsWith('addNotification') &&
      node.arguments.length > 0 &&
      ts.isObjectLiteralExpression(node.arguments[0]) &&
      node.arguments[0].properties.some(
        (prop) =>
          ts.isPropertyAssignment(prop) &&
          prop.name.getText(sourceFile) === 'type' &&
          ts.isStringLiteral(prop.initializer) &&
          prop.initializer.text === 'bulk_removal'
      )
  ).map((call) => call.arguments[0]);

/** Value of `property` on an object literal, or undefined when absent. */
const literalProperty = (objectLiteral, sourceFile, property) =>
  objectLiteral.properties.find(
    (prop) => ts.isPropertyAssignment(prop) && prop.name.getText(sourceFile) === property
  )?.initializer;

test('every batch card in the app declares the item types it owns', () => {
  const declared = new Set();
  let batchCardsFound = 0;

  for (const fileUrl of sourceFilesUnder(new URL('../src/', import.meta.url))) {
    const source = readFileSync(fileUrl, 'utf8');
    if (!source.includes('bulk_removal')) continue;

    const relative = fileUrl.href.slice(new URL('../', import.meta.url).href.length);
    const sourceFile = ts.createSourceFile(relative, source, ts.ScriptTarget.Latest, true);

    for (const card of bulkCardLiterals(sourceFile)) {
      batchCardsFound += 1;
      const details = literalProperty(card, sourceFile, 'details');
      assert.ok(
        details && ts.isObjectLiteralExpression(details),
        `${relative}: a bulk_removal card needs a literal details object declaring itemTypes`
      );

      const itemTypes = literalProperty(details, sourceFile, 'itemTypes');
      assert.ok(
        itemTypes && ts.isArrayLiteralExpression(itemTypes) && itemTypes.elements.length > 0,
        `${relative}: this batch card declares no itemTypes, so every item it runs will open a ` +
          'second card beside it. Add the per-item notification types this batch produces.'
      );

      for (const element of itemTypes.elements) {
        assert.ok(
          ts.isStringLiteral(element),
          `${relative}: itemTypes must be string literals so this check can read them`
        );
        declared.add(element.text);
      }
    }
  }

  assert.ok(batchCardsFound > 0, 'found no bulk_removal cards at all; this check has gone blind');
  assert.deepEqual(
    [...declared].sort(),
    CASES.map(([type]) => type).sort(),
    'the types batches declare and the types the behaviour tests above cover have drifted apart'
  );
});
