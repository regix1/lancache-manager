import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import {
  MemoryStorage,
  bindLifted,
  collectNodes,
  compileToUrl,
  liftConstArrow,
  moduleUrl
} from './transpile-module.mjs';

/**
 * Regression test for the per-item removal card that should stay hidden while the
 * bulk card whose OWN items produce that notification type is running, but must
 * NOT be swallowed by an unrelated bulk card (a different batch, or the same batch
 * running a different item type). Compiles handlers.ts from real source so
 * the suppression check under test is the one that ships. Covers all four batch/
 * item-type pairs: the cache batch's game and service items, the evicted batch,
 * and the log batch.
 */

const I18N_STUB = moduleUrl(`export default { t: (key) => key };`);

const loadHandlerFactories = async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const storageUrl = await compileToUrl('../src/utils/storage.ts');
  const handlersUrl = await compileToUrl('../src/contexts/notifications/handlers.ts', {
    './constants': constantsUrl,
    './notificationStatus': statusUrl,
    '@utils/storage': storageUrl,
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
/** Runs waitingHandler for one queued event of `type` against a starting card list. */
const runWaitingHandler = async (type, startingCards) => {
  const { findBulkCardOwningOperation, eventTargetsCard } = await loadHandlerFactories();
  const { isTerminalNotificationStatus } = await import(
    await compileToUrl('../src/contexts/notifications/notificationStatus.ts')
  );

  const arrowSource = liftConstArrow(
    'src/contexts/notifications/useNotificationHandlers.ts',
    'waitingHandler'
  );

  let state = startingCards;
  const setNotifications = (updater) => {
    state = updater(state);
  };

  const waitingHandler = bindLifted(arrowSource, {
    registry: [],
    findEntryForWireType: () => ({ type, id: `${type}_card` }),
    cancelAutoDismissTimer: () => undefined,
    setNotifications,
    findBulkCardOwningOperation,
    eventTargetsCard,
    isTerminalNotificationStatus,
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
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      startedAt: new Date(),
      details: { itemTypes: ['eviction_removal'] }
    }
  ]);
  assert.ok(
    state.some((n) => n.id === 'game_removal_card' && n.status === 'waiting'),
    'a queued item should get its own waiting card when nothing suppresses it'
  );
});

test('the owning bulk card suppresses the queued waiting card too, not just the started card', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      startedAt: new Date(),
      // The batch's item request is still on the wire, which is exactly when the queue push for
      // that item arrives with no id on the card to compare it against.
      details: { itemTypes: ['service_removal', 'game_removal'], itemRequestPending: true }
    }
  ]);
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
  const { findBulkCardOwningOperation } = await loadHandlerFactories();
  // The batch card goes to 'waiting' while its current item is parked behind another operation.
  // It is still the owner: if this stopped matching, the queue's own card would reappear next to
  // it and the user would see the same sentence twice, which is the bug this whole pair prevents.
  const parkedBatch = [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'waiting',
      details: { itemTypes: ['service_removal', 'game_removal'], itemRequestPending: true }
    }
  ];
  assert.ok(
    findBulkCardOwningOperation('game_removal', 'op-1', parkedBatch),
    'a parked batch must still own the item types it declared'
  );
  assert.equal(
    findBulkCardOwningOperation('log_removal', 'op-1', parkedBatch),
    undefined,
    'it must not claim a type it never declared'
  );
});

test('a batch whose items are a different type keeps its own message', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['eviction_removal'] }
    }
  ]);
  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.message,
    'Removing 1 of 2 - Arma 3',
    'an unrelated batch must not have its message overwritten by another queue blocker'
  );
});

/**
 * Declaring an item type is not the same as having started the operation. Two batches can declare
 * one type, and a batch shares its types with every removal a user starts from elsewhere in the
 * app, so the type alone cannot decide whose queued operation this is. A batch publishes its
 * current item's operation id while that item is in flight, and that is what settles it.
 */
test('a queued operation the batch did not start gets its own card instead of relabelling the batch', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['game_removal'], currentOperationId: 'the-batch-own-item' }
    }
  ]);

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.status,
    'running',
    "a batch busy with its own item must not be relabelled by another operation's queue event"
  );
  assert.equal(bulk.message, 'Removing 1 of 2 - Arma 3', 'and it keeps its own item line');

  const queued = state.find((n) => n.id === 'game_removal_card');
  assert.ok(queued, 'the queued operation needs a card of its own or it cannot be cancelled');
  assert.equal(
    queued.details.operationId,
    'op-1',
    'that card carries the operation id the X button cancels'
  );
});

/**
 * An empty `currentOperationId` is only ever ambiguous for one request round trip: the queue push
 * arrives while the item's own request is still on the wire. Outside that window the field is empty
 * because the batch has nothing to publish - the queue answered 'alreadyRunning' and handed back a
 * live removal's id the batch refused - and that lasts as long as the other removal runs. Treating
 * it as ownership there hides an unrelated operation behind the batch card for minutes, with no card
 * of its own to cancel from.
 */
test('a batch with no request on the wire does not swallow a queued operation it never started', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['game_removal'] }
    }
  ]);

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(bulk.status, 'running', 'the batch is busy with its own item, not with this one');
  assert.equal(bulk.message, 'Removing 1 of 2 - Arma 3', 'and it keeps its own item line');

  const queued = state.find((n) => n.id === 'game_removal_card');
  assert.ok(queued, 'the queued operation needs a card of its own or it cannot be cancelled');
  assert.equal(queued.details.operationId, 'op-1');
});

test('the batch card takes the queue wording for the item the batch itself started', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['game_removal'], currentOperationId: 'op-1' }
    }
  ]);

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(bulk.status, 'waiting');
  assert.equal(bulk.message, 'waiting for Cache File Scan');
  assert.ok(
    !state.some((n) => n.id === 'game_removal_card'),
    'the batch card already reports this item, so no second card may appear beside it'
  );
});

test('a queued operation does not evict the running card of another operation in the same slot', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'game_removal_card',
      type: 'game_removal',
      status: 'running',
      message: 'Removing Arma 3',
      detailMessage: 'Deleting 1200 files',
      progress: 42,
      startedAt: new Date(),
      details: { operationId: 'B' }
    }
  ]);

  const running = state.find((n) => n.id === 'game_removal_card');
  assert.equal(running.status, 'running', 'the running operation keeps its card');
  assert.equal(running.progress, 42, 'and its progress');
  assert.equal(running.detailMessage, 'Deleting 1200 files', 'and its stage text');
  assert.equal(
    running.details.operationId,
    'B',
    'and its operation id, so the X still cancels the operation the card is showing'
  );
});

test('a queued operation still takes the slot from a card whose operation has finished', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'game_removal_card',
      type: 'game_removal',
      status: 'completed',
      message: 'Removed Arma 3',
      startedAt: new Date(),
      details: { operationId: 'B' }
    }
  ]);

  const card = state.find((n) => n.id === 'game_removal_card');
  assert.equal(card.status, 'waiting', 'a finished card is not live state worth protecting');
  assert.equal(card.details.operationId, 'op-1');
});

/** Runs waitingCompleteHandler for one wait-queue completion against a starting card list. */
const runWaitingCompleteHandler = async (type, startingCards, event) => {
  const arrowSource = liftConstArrow(
    'src/contexts/notifications/useNotificationHandlers.ts',
    'waitingCompleteHandler'
  );

  let state = startingCards;
  const dismissed = [];
  const setNotifications = (updater) => {
    state = updater(state);
  };

  const waitingCompleteHandler = bindLifted(arrowSource, {
    registry: [],
    findEntryForWireType: () => ({ type, id: `${type}_card` }),
    setNotifications,
    scheduleAutoDismiss: (id) => dismissed.push(id),
    i18n: { t: (key) => key },
    GENERIC_FAILURE_I18N_KEY: 'generic.failure'
  });

  waitingCompleteHandler(event);
  return { state, dismissed };
};

test('a wait-queue completion for an operation nothing is showing arms no dismiss timer', async () => {
  const { state, dismissed } = await runWaitingCompleteHandler(
    'game_removal',
    [
      {
        id: 'game_removal_card',
        type: 'game_removal',
        status: 'running',
        startedAt: new Date(),
        details: { operationId: 'B' }
      },
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'waiting',
        startedAt: new Date(),
        details: { itemTypes: ['game_removal'], currentOperationId: 'C' }
      }
    ],
    { operationType: 'gameRemoval', operationId: 'A', cancelled: true }
  );

  assert.deepEqual(dismissed, [], 'nothing matched, so nothing may be put on a timer to disappear');
  assert.equal(
    state.find((n) => n.id === 'bulk_removal_x').status,
    'waiting',
    "a batch parked behind a different operation is none of this event's business"
  );
});

test('a batch card relabelled while its item was queued goes back to running when that item is cancelled', async () => {
  const { state, dismissed } = await runWaitingCompleteHandler(
    'game_removal',
    [
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'waiting',
        message: 'waiting for Cache File Scan',
        startedAt: new Date(),
        details: { itemTypes: ['game_removal'], currentOperationId: 'A' }
      }
    ],
    { operationType: 'gameRemoval', operationId: 'A', cancelled: true }
  );

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.status,
    'running',
    'the batch run is not over just because one of its items left the queue'
  );
  assert.deepEqual(dismissed, [], 'the batch card is still live, so it must not be timed out');
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
