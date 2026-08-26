import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, compileToUrl, findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * Regression tests for bulk cache game removal under the operation wait-queue.
 * Uses the real gameRemovalEntity matchers compiled from product source.
 */

const createFakeSignalR = () => {
  const handlers = new Map();
  return {
    on(event, handler) {
      if (!handlers.has(event)) {
        handlers.set(event, new Set());
      }
      handlers.get(event).add(handler);
    },
    off(event, handler) {
      handlers.get(event)?.delete(handler);
    },
    emit(event, payload) {
      for (const handler of handlers.get(event) ?? []) {
        handler(payload);
      }
    }
  };
};

const loadWaitHelper = async () => {
  const moduleUrl = await compileToUrl('../src/contexts/notifications/waitForSignalRCompletion.ts');
  return import(moduleUrl);
};

const loadEntity = async () => {
  const moduleUrl = await compileToUrl(
    '../src/components/features/management/game-detection/gameRemovalEntity.ts'
  );
  return import(moduleUrl);
};

/**
 * The gate that decides whether a batch keeps the operation id its enqueue response carries lives
 * inside a React component, and the cancel it feeds lives inside a hook. Neither can be imported
 * here, so lift the exact source of the pieces under test and run them with their free variables
 * supplied directly, the same way test-game-removal-suppression.mjs runs the lifted waiting handler.
 */

/** Source text of the arrow assigned to `<propertyName>:` in an object literal. */
const arrowPropertyOf = (sourceFile, objectLiteral, propertyName) => {
  const property = objectLiteral.properties.find(
    (node) => ts.isPropertyAssignment(node) && node.name.getText(sourceFile) === propertyName
  );
  assert.ok(property, `expected a ${propertyName} property in ${sourceFile.fileName}`);
  return property.initializer.getText(sourceFile);
};

/** Source text of the `processItem` the cache run hands to its batch queue. */
const liftCacheProcessItem = () => {
  const sourceFile = parseSource(
    'src/contexts/BulkRemovalContext/BulkRemovalContext.tsx',
    ts.ScriptKind.TSX
  );
  const call = findSoleNode(
    sourceFile,
    'runCacheQueue call',
    (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'runCacheQueue'
  );
  return arrowPropertyOf(sourceFile, call.arguments[0], 'processItem');
};

/** Source text of the `setOperationId` the batch queue hands to each item. */
const liftItemSetOperationId = () => {
  const sourceFile = parseSource('src/hooks/useBatchQueue.ts', ts.ScriptKind.TS);
  const declaration = findSoleNode(
    sourceFile,
    'ctx declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === 'ctx'
  );
  return arrowPropertyOf(sourceFile, declaration.initializer, 'setOperationId');
};

/** Source text of the `cancelRun` the batch queue hands to each item. */
const liftItemCancelRun = () => {
  const sourceFile = parseSource('src/hooks/useBatchQueue.ts', ts.ScriptKind.TS);
  const declaration = findSoleNode(
    sourceFile,
    'ctx declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === 'ctx'
  );
  return arrowPropertyOf(sourceFile, declaration.initializer, 'cancelRun');
};

/** Source text of the arrow inside a `const <constName> = useCallback(<arrow>, [...])`. */
const liftCallbackArrow = (relativePath, constName) => {
  const sourceFile = parseSource(relativePath, ts.ScriptKind.TS);
  const declaration = findSoleNode(
    sourceFile,
    `${constName} useCallback`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === constName &&
      node.initializer !== undefined &&
      ts.isCallExpression(node.initializer) &&
      node.initializer.expression.getText(sourceFile) === 'useCallback'
  );
  return declaration.initializer.arguments[0].getText(sourceFile);
};

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

const GAME_FIXTURES = [
  {
    label: 'steam',
    game: { game_app_id: 570, game_name: 'Dota 2', service: 'steam' },
    startedPayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: game.game_app_id,
        epicAppId: null,
        gameName: game.game_name
      };
    },
    completePayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: game.game_app_id,
        epicAppId: null,
        gameName: game.game_name,
        success: true
      };
    }
  },
  {
    label: 'epic',
    game: {
      game_app_id: 0,
      game_name: 'Fortnite',
      service: 'epicgames',
      epic_app_id: 'cat-fortnite'
    },
    startedPayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: game.epic_app_id,
        gameName: game.game_name
      };
    },
    completePayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: game.epic_app_id,
        gameName: game.game_name,
        success: true
      };
    }
  },
  {
    label: 'named blizzard',
    game: { game_app_id: 0, game_name: 'Diablo IV', service: 'blizzard' },
    startedPayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: null,
        gameName: game.game_name
      };
    },
    completePayload(game, runningId) {
      return {
        operationId: runningId,
        gameAppId: null,
        epicAppId: null,
        gameName: game.game_name,
        success: true
      };
    }
  }
];

/**
 * Drives one cache-run item through the real `processItem`, the real per-item `setOperationId` and
 * the real `triggerCancel`, so the enqueue-response gate and the cancel it feeds are exercised as
 * shipped rather than as a copy written here.
 */
const createBatchHarness = async () => {
  const { settleBatchItem, waitForSignalRCompletion } = await loadWaitHelper();
  const entityHelper = await loadEntity();
  const signalR = createFakeSignalR();

  const cancelledOperations = [];
  const reportedFailures = [];
  const capturedOperationIds = [];
  let cancelRunCalls = 0;
  let nextResponse = null;

  const bulkNotifIdRef = { current: 'bulk_card' };
  const currentItemOperationIdRef = { current: null };
  const cancelRequestedRef = { current: false };
  const notificationsRef = {
    current: [
      {
        id: 'bulk_card',
        type: 'bulk_removal',
        status: 'running',
        details: { itemTypes: ['service_removal', 'game_removal'] }
      }
    ]
  };

  // The top-level merge NotificationsContext performs, including the function form it accepts for
  // a read-modify-write of `details`. A bare `details` write replaces the whole object here exactly
  // as it would in the app, which is what makes the itemTypes assertion real.
  const updateNotification = (id, updates) => {
    notificationsRef.current = notificationsRef.current.map((n) =>
      n.id === id ? { ...n, ...(typeof updates === 'function' ? updates(n) : updates) } : n
    );
  };

  const cancelItemOperation = (opId) => {
    cancelledOperations.push(opId);
  };

  const setCardOperationId = bindLifted(
    liftCallbackArrow('src/hooks/useBatchQueue.ts', 'setCardOperationId'),
    { bulkNotifIdRef, updateNotification }
  );

  const triggerCancel = bindLifted(
    liftCallbackArrow('src/hooks/useBatchQueue.ts', 'triggerCancel'),
    {
      cancelRequestedRef,
      bulkNotifIdRef,
      updateNotification,
      i18n: { t: (key) => key },
      currentItemOperationIdRef,
      cancelItemOperation,
      notifyError: (message) => {
        reportedFailures.push(message);
      }
    }
  );

  const setOperationId = bindLifted(liftItemSetOperationId(), {
    currentItemOperationIdRef,
    setCardOperationId,
    cancelRequestedRef,
    cancelItemOperation
  });

  const cancelRun = bindLifted(liftItemCancelRun(), {
    currentItemOperationIdRef,
    triggerCancel
  });

  const ctx = {
    setOperationId: (opId) => {
      capturedOperationIds.push(opId);
      setOperationId(opId);
    },
    cancelRun: () => {
      cancelRunCalls += 1;
      cancelRun();
    },
    requestId: 'req-1'
  };

  // The real run loop, so a click landing between `setOperationId` calls is exercised through the
  // same break/finalize path the app takes rather than a copy of it written here.
  const runActiveRef = { current: false };
  const currentItemRef = { current: null };
  const scheduledDismissals = [];
  const runQueue = bindLifted(liftCallbackArrow('src/hooks/useBatchQueue.ts', 'run'), {
    runActiveRef,
    cancelRequestedRef,
    currentItemOperationIdRef,
    currentItemRef,
    bulkNotifIdRef,
    setCardOperationId,
    cancelItemOperation,
    triggerCancel,
    scheduleAutoDismiss: (id) => scheduledDismissals.push(id),
    setState: () => undefined,
    onSettled: undefined
  });

  const removal = async () => nextResponse;
  const processItem = bindLifted(liftCacheProcessItem(), {
    waitForSignalRCompletion,
    settleBatchItem,
    on: signalR.on,
    off: signalR.off,
    ApiService: {
      removeServiceFromCache: removal,
      removeGameFromCache: removal,
      removeEpicGameFromCache: removal,
      removeNamedGameFromCache: removal
    },
    updateBulkProgress: () => undefined,
    bulkNotifId: 'bulk_card',
    currentIndex: 1,
    total: 1,
    updateNotification,
    restoreItemMessage: () => undefined,
    classifyGameFromCacheInfo: entityHelper.classifyGameFromCacheInfo,
    matchesGameRemovalComplete: entityHelper.matchesGameRemovalComplete,
    matchesGameRemovalIdentity: entityHelper.matchesGameRemovalIdentity,
    shouldPinOperationIdFromResponse: entityHelper.shouldPinOperationIdFromResponse
  });

  return {
    signalR,
    ctx,
    triggerCancel,
    runQueue,
    notificationsRef,
    cancelledOperations,
    reportedFailures,
    capturedOperationIds,
    cancelRunCount: () => cancelRunCalls,
    start(entry, response) {
      nextResponse = response;
      return processItem(entry, ctx);
    }
  };
};

const SERVICE_ENTRY = { kind: 'service', service: { service_name: 'steam' } };

test('a service item parked behind another operation is cancellable while it waits', async () => {
  const harness = await createBatchHarness();
  // The queue answers alreadyRunning when it deduplicates the request onto a waiter that is
  // already parked. That waiter IS this item, and its id is the only thing the X can cancel.
  const settled = harness.start(SERVICE_ENTRY, {
    operationId: 'wait-dup',
    queued: true,
    alreadyRunning: true,
    status: 'waiting'
  });
  await flush();

  assert.deepEqual(harness.capturedOperationIds, ['wait-dup'], 'the parked waiter id must be kept');

  harness.triggerCancel();
  assert.deepEqual(harness.cancelledOperations, ['wait-dup'], 'the X must cancel that operation');
  assert.deepEqual(harness.reportedFailures, [], 'a cancel that was sent is not a failure');

  // No ServiceRemovalComplete is ever emitted here: the removal never ran, so the wait has to
  // settle on the queue push alone instead of sitting until the blocking operation finishes.
  harness.signalR.emit('OperationWaitingComplete', {
    operationId: 'wait-dup',
    operationType: 'serviceRemoval',
    cancelled: true,
    promoted: false
  });
  await settled;

  assert.equal(harness.cancelRunCount(), 1, 'a cancelled queued item ends the run as cancelled');
});

test('a game item parked behind another operation is cancellable while it waits', async () => {
  const harness = await createBatchHarness();
  const settled = harness.start(
    { kind: 'game', game: GAME_FIXTURES[0].game },
    { operationId: 'wait-game', queued: true, alreadyRunning: true, status: 'waiting' }
  );
  await flush();

  assert.deepEqual(harness.capturedOperationIds, ['wait-game']);

  harness.triggerCancel();
  assert.deepEqual(harness.cancelledOperations, ['wait-game']);

  harness.signalR.emit('OperationWaitingComplete', {
    operationId: 'wait-game',
    operationType: 'gameRemoval',
    cancelled: true,
    promoted: false
  });
  await settled;

  assert.equal(harness.cancelRunCount(), 1);
});

test('the id of a removal this batch did not start stays out of reach of the X', async () => {
  const harness = await createBatchHarness();
  const settled = harness.start(SERVICE_ENTRY, {
    operationId: 'other-op',
    queued: false,
    alreadyRunning: true,
    status: 'alreadyRunning'
  });
  await flush();

  assert.deepEqual(
    harness.capturedOperationIds,
    [null],
    'the request is answered, but with no id this batch owns - that removal has its own card'
  );

  harness.triggerCancel();
  assert.deepEqual(harness.cancelledOperations, [], 'the X must not reach across and end it');

  harness.signalR.emit('ServiceRemovalComplete', { serviceName: 'steam' });
  await settled;
});

test('a fresh park and an immediate start both still keep the id', async () => {
  const parked = await createBatchHarness();
  const parkedSettled = parked.start(SERVICE_ENTRY, {
    operationId: 'wait-fresh',
    queued: true,
    alreadyRunning: false,
    status: 'waiting'
  });
  await flush();
  assert.deepEqual(parked.capturedOperationIds, ['wait-fresh']);
  parked.signalR.emit('ServiceRemovalComplete', { serviceName: 'steam' });
  await parkedSettled;

  const started = await createBatchHarness();
  const startedSettled = started.start(SERVICE_ENTRY, {
    operationId: 'run-now',
    queued: false,
    alreadyRunning: false,
    status: 'started'
  });
  await flush();
  assert.deepEqual(started.capturedOperationIds, ['run-now']);
  started.signalR.emit('ServiceRemovalComplete', { serviceName: 'steam' });
  await startedSettled;
});

test('a cancel with no operation id yet ends the run cleanly and reports nothing', async () => {
  const harness = await createBatchHarness();
  let releaseItem;
  const itemInFlight = new Promise((resolve) => {
    releaseItem = resolve;
  });
  const finalized = [];

  // The item's request has not come back, so nothing has called setOperationId yet. This window
  // opens once before the first item and once between every pair of them, and the click still has
  // to end the run: the flag stops the loop and finalize is where the outcome is decided.
  const settled = harness.runQueue({
    items: [SERVICE_ENTRY],
    openNotification: () => 'bulk_card',
    processItem: async () => {
      await itemInFlight;
    },
    finalize: (args) => finalized.push(args)
  });
  await flush();

  harness.triggerCancel();
  releaseItem();
  await settled;

  assert.deepEqual(harness.cancelledOperations, [], 'there is no operation to cancel yet');
  assert.deepEqual(harness.reportedFailures, [], 'a cancel that stopped the run is not a failure');
  assert.equal(finalized.length, 1, 'the run settles exactly once');
  assert.equal(finalized[0].cancelled, true, 'and it settles as cancelled');
  assert.equal(finalized[0].succeeded, 0, 'with the item that was in flight counted as neither');
  assert.equal(finalized[0].failed, 0);
});

test('publishing the item operation id leaves the batch card item types intact', async () => {
  const harness = await createBatchHarness();

  harness.ctx.setOperationId('wait-dup');
  const withId = harness.notificationsRef.current[0];
  assert.equal(withId.details.currentOperationId, 'wait-dup');
  assert.deepEqual(withId.details.itemTypes, ['service_removal', 'game_removal']);

  harness.ctx.setOperationId(null);
  const cleared = harness.notificationsRef.current[0];
  assert.equal(cleared.details.currentOperationId, undefined);
  assert.deepEqual(cleared.details.itemTypes, ['service_removal', 'game_removal']);
});

/** Drives one game item from its enqueue response to the completion that settles it. */
const runGameItem = async (fixture, response, runningId) => {
  const harness = await createBatchHarness();
  const settled = harness.start({ kind: 'game', game: fixture.game }, response);
  await flush();

  harness.signalR.emit('GameRemovalStarted', fixture.startedPayload(fixture.game, runningId));
  harness.signalR.emit('GameRemovalComplete', fixture.completePayload(fixture.game, runningId));
  await settled;
  return harness;
};

test('identity-first correlation resolves queued promotion for all platforms', async () => {
  for (const fixture of GAME_FIXTURES) {
    const harness = await runGameItem(
      fixture,
      {
        operationId: `wait-${fixture.label}`,
        queued: true,
        alreadyRunning: false,
        status: 'waiting'
      },
      `run-${fixture.label}`
    );

    assert.deepEqual(
      harness.capturedOperationIds,
      [`wait-${fixture.label}`, `run-${fixture.label}`],
      `${fixture.label}: the queued id is kept for the X, then rebound to the promoted operation`
    );
  }
});

test('bulk cache correlation still works for immediate start (no queue)', async () => {
  const harness = await runGameItem(
    GAME_FIXTURES[0],
    { operationId: 'run-immediate', queued: false, alreadyRunning: false, status: 'started' },
    'run-immediate'
  );

  assert.deepEqual(
    harness.capturedOperationIds,
    ['run-immediate', 'run-immediate'],
    'an immediate start uses the same id in the response and on the wire'
  );
});

test('regression gate — bulk cache resolves after queue promotion', async () => {
  const harness = await runGameItem(
    GAME_FIXTURES[0],
    { operationId: 'wait-1111', queued: true, alreadyRunning: false, status: 'waiting' },
    'run-2222'
  );

  assert.deepEqual(
    harness.capturedOperationIds,
    ['wait-1111', 'run-2222'],
    'the promoted operation completes under a new id and the batch follows it there'
  );
});

test('cancelling a queued item settles the wait instead of running to timeout', async () => {
  const harness = await createBatchHarness();
  // A queued item never started, so no GameRemovalComplete is coming for it. Before the wait
  // watched OperationWaitingComplete the batch sat here until its timeout while the card read
  // "Cancelling...".
  const settled = harness.start(
    { kind: 'game', game: GAME_FIXTURES[0].game },
    { operationId: 'wait-cancelled', queued: true, alreadyRunning: false, status: 'waiting' }
  );
  await flush();

  harness.signalR.emit('OperationWaitingComplete', {
    operationId: 'wait-cancelled',
    operationType: 'gameRemoval',
    cancelled: true,
    promoted: false
  });
  await settled;

  assert.equal(harness.cancelRunCount(), 1, 'a cancelled queued item ends the run as cancelled');
});
