import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  bulkRemovalCard,
  collectNodes,
  liftConstArrow,
  liftHookCallback,
  loadNotificationModules,
  moduleUrl,
  operationRunRow,
  pushRun
} from './transpile-module.mjs';

/**
 * A bulk removal shows one card that owns its items: while the batch card owns an item's run, that
 * run draws no card of its own, and while the item waits the batch card turns purple and names the
 * blocker. A batch must NOT swallow a run it does not own (a different batch, the same batch running
 * a different item type, or a removal started elsewhere). These cases feed run rows to the real run
 * store beside a batch card and read what it draws, for all four batch/item-type pairs: the cache
 * batch's game and service items, the evicted batch, and the log batch.
 */

const I18N_STUB = moduleUrl(
  'export default { t: (key, values) => (values ? key + " " + JSON.stringify(values) : key), exists: () => true };'
);

const notifications = await loadNotificationModules(I18N_STUB);
const {
  createRunStoreState,
  deriveNotifications,
  waitingCardMessage,
  OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE
} = notifications;

/** The run-row fields of one item of each type a batch can run. */
const ITEM_RUN = {
  game_removal: { operationType: 'gameRemoval', name: 'Game Removal' },
  service_removal: { operationType: 'serviceRemoval', name: 'Service Removal' },
  eviction_removal: { operationType: 'evictionRemoval', name: 'Eviction Removal' },
  log_removal: { operationType: 'logRemoval', name: 'Log Removal' }
};

const itemCards = (drawn) => drawn.filter((card) => card.type !== 'bulk_removal');
const batchOf = (drawn) => drawn.find((card) => card.id === 'bulk');

const QUEUED_BEHIND_SCAN = { status: 'waiting', blockedByName: 'Cache File Scan' };

test('cancel requests serialize clicks and protect replacement operations', async () => {
  const errors = [];
  let release;
  let calls = 0;
  let state = {
    id: 'slot',
    type: 'game_detection',
    status: 'running',
    controlOnly: true,
    details: { operationId: 'first', operationIds: ['first'] }
  };
  const cancel = bindLifted(
    liftConstArrow('src/components/common/notificationCancel.ts', 'handleCancel'),
    {
      CANCEL_CONFIG_BY_TYPE: { game_detection: { cancelKind: 'serverOp' } },
      pendingCancels: new Set(),
      notifyToastError: (message) => errors.push(message),
      i18n: { t: (key) => key },
      isTerminalNotificationStatus: (status) =>
        ['completed', 'failed', 'cancelled', 'skipped'].includes(status),
      isAbortError: (error) => error?.name === 'AbortError',
      ApiError: class extends Error {},
      ApiService: {
        cancelOperation: () => {
          calls++;
          return new Promise((resolve) => {
            release = resolve;
          });
        },
        forceKillOperation: () => {
          calls++;
          return Promise.reject(new Error('Connection closed'));
        }
      }
    }
  );
  const update = (_id, change) => {
    state = { ...state, ...(typeof change === 'function' ? change(state) : change) };
  };
  const remove = () => {
    state = undefined;
  };
  const getNotifications = () => (state ? [state] : []);

  const first = cancel(state, update, remove, getNotifications);
  await cancel(state, update, remove, getNotifications);
  assert.equal(calls, 1, 'a second click while the first cancel is out sends nothing');
  assert.equal(state.details.cancelPending, true);
  release({});
  await first;
  assert.equal(state.status, 'cancelling');

  // The second click force-kills; a failed force-kill says so and re-arms the X.
  await cancel(state, update, remove, getNotifications);
  assert.equal(errors.length, 1);
  assert.equal(state.controlOnly, true);
  assert.equal(state.details.cancelRequested, false);

  // The card is replaced by another operation while a cancel is out: its answer, even
  // "already finished", must leave the replacement alone.
  const pending = cancel(state, update, remove, getNotifications);
  const replacement = {
    id: 'slot',
    type: 'game_detection',
    status: 'running',
    details: { operationId: 'second', operationIds: ['second'] }
  };
  state = replacement;
  release({ alreadyFinished: true });
  await pending;
  assert.equal(state, replacement);
  assert.equal(calls, 3);
});

test('schedule HTTP responses leave retained and hidden acknowledgment to the waiting event', async () => {
  const source = liftHookCallback(
    'src/components/features/management/schedules/SchedulesSection.tsx',
    'useCallback',
    'ApiService.triggerSchedule(key)'
  );
  for (const eventFirst of [true, false]) {
    for (const response of [
      { status: 'skipped', skippedReason: 'held' },
      { status: 'alreadyRunning', alreadyRunning: true },
      { status: 'started' }
    ]) {
      const notices = [];
      const handler = bindLifted(source, {
        ApiService: { triggerSchedule: async () => response },
        t: (key) => key,
        markStarting: () => undefined,
        clearPending: () => undefined,
        setCompletedKeys: () => undefined,
        setTimeout: () => undefined,
        addNotification: (notice) => notices.push(notice),
        getErrorMessage: String
      });
      const yellow = {
        type: 'generic',
        status: 'skipped',
        details: { notificationType: 'warning' }
      };
      if (eventFirst && response.status !== 'started') notices.push(yellow);
      await handler('cacheReconciliation');
      if (!eventFirst && response.status !== 'started') notices.push(yellow);
      assert.deepEqual(notices, response.status === 'started' ? [] : [yellow]);
    }
  }
});

test('a Storage eviction scan opens no card of its own and is busy only while its request is out', async () => {
  const source = liftConstArrow(
    'src/components/features/management/sections/StorageSection.tsx',
    'handleStartEvictionScan'
  );
  for (const answer of [
    { operationId: 'scan' },
    { operationId: 'scan', queued: true },
    { operationId: 'scan', alreadyRunning: true }
  ]) {
    const starting = [];
    const errors = [];
    const inFlight = { current: false };
    let requests = 0;
    // No card-writing name is bound: the server's run row is the only thing that opens a card,
    // and a handler that still wrote one would throw a ReferenceError here.
    const handler = bindLifted(source, {
      evictionScanInFlightRef: inFlight,
      setIsStartingEvictionScan: (value) => starting.push(value),
      ApiService: {
        startEvictionScan: async () => {
          requests += 1;
          return answer;
        }
      },
      onError: (message) => errors.push(message),
      getErrorMessage: String,
      isAbortError: () => false
    });

    const first = handler();
    await handler();
    await first;
    assert.equal(requests, 1, 'a second click while the request is out is ignored');
    assert.deepEqual(starting, [true, false]);
    assert.equal(inFlight.current, false);
    assert.deepEqual(errors, []);
  }
});

test('all maintenance runs map to their card type and registry cancel contract', () => {
  const expected = {
    logRotation: ['log_rotation', 'none'],
    gameImageFetch: ['game_image_fetch', 'serverOp'],
    cacheSnapshot: ['cache_snapshot', 'serverOp'],
    operationHistoryCleanup: ['operation_history_cleanup', 'serverOp'],
    dashboardCacheWarmer: ['dashboard_cache_warmer', 'serverOp']
  };

  for (const [wireType, [notificationType, cancelKind]] of Object.entries(expected)) {
    assert.equal(OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[wireType], notificationType);
    const entry = notifications.NOTIFICATION_REGISTRY.find(
      (candidate) => candidate.type === notificationType
    );
    assert.equal(entry?.cancelKind, cancelKind, wireType);
  }

  assert.equal(OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE.cacheFileCount, undefined);
});

const CASES = [
  ['game_removal', 'cache batch, game items'],
  ['service_removal', 'cache batch, service items'],
  ['eviction_removal', 'evicted batch'],
  ['log_removal', 'log batch']
];

for (const [type, label] of CASES) {
  test(`${label}: an unrelated running batch does not fold the ${type} run`, () => {
    const localCards = [bulkRemovalCard({ itemTypes: [], currentOperationId: 'op-1' })];
    const drawn = deriveNotifications(
      pushRun(
        notifications,
        createRunStoreState(),
        operationRunRow('op-1', ITEM_RUN[type]),
        localCards
      ),
      localCards
    );
    assert.deepEqual(
      itemCards(drawn).map((card) => card.type),
      [type],
      `${type} card should open when no running batch owns it`
    );
  });

  test(`${label}: the owning bulk card folds the ${type} run`, () => {
    for (const details of [
      { itemTypes: [type], currentOperationId: 'op-1' },
      { itemTypes: [type], itemOperationIds: ['op-1'] }
    ]) {
      const localCards = [bulkRemovalCard(details)];
      const drawn = deriveNotifications(
        pushRun(
          notifications,
          createRunStoreState(),
          operationRunRow('op-1', ITEM_RUN[type]),
          localCards
        ),
        localCards
      );
      assert.deepEqual(itemCards(drawn), [], `${type} run should stay inside its batch card`);
    }
  });
}

test('a batch declaring two item types folds both and leaves other types alone', () => {
  const localCards = [
    bulkRemovalCard({ itemTypes: ['game_removal', 'service_removal'], itemRequestPending: true })
  ];
  const itemsDrawnFor = (type) =>
    itemCards(
      deriveNotifications(
        pushRun(
          notifications,
          createRunStoreState(),
          operationRunRow('op-1', ITEM_RUN[type]),
          localCards
        ),
        localCards
      )
    );
  assert.deepEqual(itemsDrawnFor('game_removal'), []);
  assert.deepEqual(itemsDrawnFor('service_removal'), []);
  assert.equal(itemsDrawnFor('eviction_removal').length, 1);
  assert.equal(itemsDrawnFor('log_removal').length, 1);
});

test('a queued item opens its own waiting card when no owning bulk card is running', () => {
  const batch = bulkRemovalCard({ itemTypes: ['eviction_removal'] });
  const drawn = deriveNotifications(
    pushRun(
      notifications,
      createRunStoreState(),
      operationRunRow('op-1', { ...ITEM_RUN.game_removal, ...QUEUED_BEHIND_SCAN }),
      [batch]
    ),
    [batch]
  );
  assert.deepEqual(
    itemCards(drawn).map((card) => card.status),
    ['waiting'],
    'a queued item should get its own waiting card when nothing folds it'
  );
  assert.equal(batchOf(drawn).message, batch.message, 'an unrelated batch keeps its own line');
  assert.equal(batchOf(drawn).status, 'running');
});

test('the owning bulk card turns purple and names the blocker while its item waits', () => {
  const row = operationRunRow('op-1', { ...ITEM_RUN.game_removal, ...QUEUED_BEHIND_SCAN });
  // The batch's item request is still on the wire, which is exactly when the queue row for that
  // item arrives with no id on the card to compare it against.
  const localCards = [
    bulkRemovalCard({ itemTypes: ['service_removal', 'game_removal'], itemRequestPending: true })
  ];
  const drawn = deriveNotifications(
    pushRun(notifications, createRunStoreState(), row, localCards),
    localCards
  );
  assert.deepEqual(itemCards(drawn), [], 'the waiting run stays inside the batch card');
  assert.equal(batchOf(drawn).status, 'waiting');
  assert.equal(
    batchOf(drawn).message,
    waitingCardMessage(row),
    'the batch card must name the blocking operation while its item is parked'
  );
  assert.match(batchOf(drawn).message, /Cache File Scan/);
});

/**
 * Declaring an item type is not the same as having started the operation. Two batches can declare
 * one type, and a batch shares its types with every removal a user starts from elsewhere in the
 * app, so the type alone cannot decide whose queued operation this is. A batch publishes its
 * current item's operation id while that item is in flight, and that is what settles it.
 */
test('a queued operation the batch did not start gets its own card instead of relabelling the batch', () => {
  const batch = bulkRemovalCard({
    itemTypes: ['game_removal'],
    currentOperationId: 'the-batch-own-item'
  });
  const drawn = deriveNotifications(
    pushRun(
      notifications,
      createRunStoreState(),
      operationRunRow('op-1', { ...ITEM_RUN.game_removal, ...QUEUED_BEHIND_SCAN }),
      [batch]
    ),
    [batch]
  );
  assert.equal(batchOf(drawn).status, 'running');
  assert.equal(batchOf(drawn).message, batch.message, 'and it keeps its own item line');
  const [queued] = itemCards(drawn);
  assert.ok(queued, 'the queued operation needs a card of its own or it cannot be cancelled');
  assert.equal(queued.details.operationId, 'op-1', 'that card carries the id the X cancels');
});

/**
 * An empty `currentOperationId` is only ever ambiguous for one request round trip: the queue row
 * arrives while the item's own request is still on the wire. Outside that window the field is empty
 * because the batch has nothing to publish - the queue answered 'alreadyRunning' and handed back a
 * live removal's id the batch refused - and that lasts as long as the other removal runs.
 */
test('a batch with no request on the wire does not swallow a queued operation it never started', () => {
  const batch = bulkRemovalCard({ itemTypes: ['game_removal'] });
  const drawn = deriveNotifications(
    pushRun(
      notifications,
      createRunStoreState(),
      operationRunRow('op-1', { ...ITEM_RUN.game_removal, ...QUEUED_BEHIND_SCAN }),
      [batch]
    ),
    [batch]
  );
  assert.equal(batchOf(drawn).status, 'running');
  assert.equal(batchOf(drawn).message, batch.message);
  assert.equal(itemCards(drawn)[0]?.details.operationId, 'op-1');
});

test('the batch card takes the queue wording for the item the batch itself started', () => {
  const row = operationRunRow('op-1', { ...ITEM_RUN.game_removal, ...QUEUED_BEHIND_SCAN });
  const localCards = [bulkRemovalCard({ itemTypes: ['game_removal'], currentOperationId: 'op-1' })];
  const drawn = deriveNotifications(
    pushRun(notifications, createRunStoreState(), row, localCards),
    localCards
  );
  assert.equal(batchOf(drawn).status, 'waiting');
  assert.equal(batchOf(drawn).message, waitingCardMessage(row));
  assert.deepEqual(itemCards(drawn), [], 'no second card may appear beside the batch card');
});

test('a batch card goes back to its own line when its queued item is canceled', () => {
  const batch = bulkRemovalCard({ itemTypes: ['game_removal'], itemOperationIds: ['op-1'] });
  const waiting = pushRun(
    notifications,
    createRunStoreState(),
    operationRunRow('op-1', { ...ITEM_RUN.game_removal, ...QUEUED_BEHIND_SCAN }),
    [batch]
  );
  const drawn = deriveNotifications(
    pushRun(
      notifications,
      waiting,
      operationRunRow('op-1', { ...ITEM_RUN.game_removal, status: 'cancelled' }),
      [batch]
    ),
    [batch]
  );
  assert.equal(batchOf(drawn).status, 'running', 'the batch run is not over');
  assert.equal(batchOf(drawn).message, batch.message);
  assert.deepEqual(itemCards(drawn), [], 'the canceled item draws nothing beside it');
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
