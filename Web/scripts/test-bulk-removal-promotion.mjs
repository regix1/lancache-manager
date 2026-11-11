import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  loadNotificationModules,
  nextRunRevision,
  operationRunRow,
  parseSource,
  pushRun
} from './transpile-module.mjs';

/**
 * Bulk removal items under the operation wait-queue. Each item sends its request, keeps the id the
 * server answers with (unless that id belongs to a removal the batch did not start), and waits for
 * that run's end through the notification store, which follows a queue promotion by itself. These
 * cases drive the item code lifted from the provider and the batch queue as shipped.
 */

const BULK_CONTEXT = 'src/contexts/BulkRemovalContext/BulkRemovalContext.tsx';
const BATCH_QUEUE = 'src/hooks/useBatchQueue.ts';

const gameRemovalEntity = await import(
  await compileToUrl('../src/components/features/management/game-detection/gameRemovalEntity.ts')
);

const scriptKindOf = (relativePath) =>
  relativePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS;

/** Source text of the arrow assigned to `<propertyName>:` in an object literal. */
const arrowPropertyOf = (sourceFile, objectLiteral, propertyName) => {
  const property = objectLiteral.properties.find(
    (node) => ts.isPropertyAssignment(node) && node.name.getText(sourceFile) === propertyName
  );
  assert.ok(property, `expected a ${propertyName} property in ${sourceFile.fileName}`);
  return property.initializer.getText(sourceFile);
};

/** Source text of the `processItem` a provider run hands to its batch queue. */
const liftProcessItem = (queueName) => {
  const sourceFile = parseSource(BULK_CONTEXT, ts.ScriptKind.TSX);
  const call = findSoleNode(
    sourceFile,
    `${queueName} call`,
    (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === queueName
  );
  return arrowPropertyOf(sourceFile, call.arguments[0], 'processItem');
};

/** Source text of a `ctx` member the batch queue hands to each item. */
const liftItemContextMember = (propertyName) => {
  const sourceFile = parseSource(BATCH_QUEUE, ts.ScriptKind.TS);
  const declaration = findSoleNode(
    sourceFile,
    'ctx declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === 'ctx'
  );
  return arrowPropertyOf(sourceFile, declaration.initializer, propertyName);
};

/** Source text of the arrow inside a `const <constName> = useCallback(<arrow>, [...])`. */
const liftCallbackArrow = (relativePath, constName) => {
  const sourceFile = parseSource(relativePath, scriptKindOf(relativePath));
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

/** Source text of a top-level `function <name>(...) {...}`. */
const liftFunction = (relativePath, name) => {
  const sourceFile = parseSource(relativePath, scriptKindOf(relativePath));
  return findSoleNode(
    sourceFile,
    `${name} function`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.getText(sourceFile) === name
  ).getText(sourceFile);
};

const settleBatchItem = bindLifted(liftFunction(BULK_CONTEXT, 'settleBatchItem'), {});

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

const GAME_FIXTURES = [
  { label: 'steam', game: { game_app_id: 570, game_name: 'Dota 2', service: 'steam' } },
  {
    label: 'epic',
    game: { game_app_id: 0, game_name: 'Fortnite', service: 'epicgames', epic_app_id: 'cat-fn' }
  },
  {
    label: 'named blizzard',
    game: { game_app_id: 0, game_name: 'Diablo IV', service: 'blizzard' }
  }
];

const createSignalR = () => {
  const handlers = new Map();
  return {
    on(event, handler) {
      if (!handlers.has(event)) handlers.set(event, new Set());
      handlers.get(event).add(handler);
    },
    off(event, handler) {
      handlers.get(event)?.delete(handler);
    },
    emit(event, message) {
      for (const handler of handlers.get(event) ?? []) handler(message);
    },
    listenerCount(event) {
      return handlers.get(event)?.size ?? 0;
    }
  };
};

/**
 * Drives one item through the real `processItem`, `followBatchItem`, per-item `setOperationId`,
 * `cancelRun` and `triggerCancel`. `waitForRunEnd` is the one stand-in: the test ends each run.
 */
const createBatchHarness = (queueName = 'runCacheQueue') => {
  const signalR = createSignalR();
  const canceledOperations = [];
  const reportedFailures = [];
  const capturedOperationIds = [];
  const awaitedOperationIds = [];
  const percents = [];
  const runEnds = [];
  let cancelRunCalls = 0;
  let nextResponse = null;

  const bulkNotifIdRef = { current: 'bulk_card' };
  const currentItemOperationIdRef = { current: null };
  const cancelRequestedRef = { current: false };
  const runsRef = { current: [] };
  const notificationsRef = {
    current: [
      {
        id: 'bulk_card',
        type: 'bulk_removal',
        status: 'running',
        details: { itemTypes: ['service_removal', 'game_removal'], itemOperationIds: [] }
      }
    ]
  };

  // The top-level merge NotificationsContext performs, including the function form it accepts for
  // a read-modify-write of `details`. A bare `details` write replaces the whole object here exactly
  // as it would in the app, which is what makes the details assertions real.
  const updateNotification = (id, updates) => {
    notificationsRef.current = notificationsRef.current.map((n) =>
      n.id === id ? { ...n, ...(typeof updates === 'function' ? updates(n) : updates) } : n
    );
  };

  const cancelItemOperation = (opId) => {
    canceledOperations.push(opId);
  };

  const setCardOperationId = bindLifted(liftCallbackArrow(BATCH_QUEUE, 'setCardOperationId'), {
    bulkNotifIdRef,
    updateNotification
  });

  const triggerCancel = bindLifted(liftCallbackArrow(BATCH_QUEUE, 'triggerCancel'), {
    cancelRequestedRef,
    bulkNotifIdRef,
    updateNotification,
    i18n: { t: (key) => key },
    currentItemOperationIdRef,
    cancelItemOperation,
    notifyError: (message) => {
      reportedFailures.push(message);
    }
  });

  const setOperationId = bindLifted(liftItemContextMember('setOperationId'), {
    currentItemOperationIdRef,
    setCardOperationId,
    cancelRequestedRef,
    cancelItemOperation
  });

  const cancelRun = bindLifted(liftItemContextMember('cancelRun'), {
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
    // As useBatchQueue.ts reads its cancel latch.
    wasCanceled: () => cancelRequestedRef.current
  };

  // The real run loop, so a click landing between `setOperationId` calls is exercised through the
  // same break/finalize path the app takes rather than a copy of it written here.
  const runQueue = bindLifted(liftCallbackArrow(BATCH_QUEUE, 'run'), {
    runActiveRef: { current: false },
    cancelRequestedRef,
    currentItemOperationIdRef,
    currentItemRef: { current: null },
    bulkNotifIdRef,
    setCardOperationId,
    cancelItemOperation,
    triggerCancel,
    setState: () => undefined,
    onSettled: undefined
  });

  const waitForRunEnd = (operationId) => {
    awaitedOperationIds.push(operationId);
    return new Promise((resolve) => runEnds.push(resolve));
  };

  const followBatchItem = bindLifted(liftCallbackArrow(BULK_CONTEXT, 'followBatchItem'), {
    on: signalR.on,
    off: signalR.off,
    runsRef,
    waitForRunEnd
  });

  const removal = async () => nextResponse;
  const processItem = bindLifted(liftProcessItem(queueName), {
    followBatchItem,
    settleBatchItem,
    onPercent: (inner) => percents.push(inner),
    ApiService: {
      removeServiceFromCache: removal,
      removeGameFromCache: removal,
      removeEpicGameFromCache: removal,
      removeNamedGameFromCache: removal,
      removeServiceFromDatasourceLogs: removal
    },
    classifyGameFromCacheInfo: gameRemovalEntity.classifyGameFromCacheInfo
  });

  return {
    signalR,
    ctx,
    triggerCancel,
    runQueue,
    runsRef,
    notificationsRef,
    canceledOperations,
    reportedFailures,
    capturedOperationIds,
    awaitedOperationIds,
    percents,
    cancelRunCount: () => cancelRunCalls,
    /** Ends the run the item is waiting on. */
    end(terminal) {
      runEnds.shift()(terminal);
    },
    start(entry, response) {
      nextResponse = response;
      return processItem(entry, ctx);
    }
  };
};

const SERVICE_ENTRY = { kind: 'service', service: { service_name: 'steam' } };

test('a service item parked behind another operation is cancelable while it waits', async () => {
  const harness = createBatchHarness();
  // The queue answers waiting with the waiter's id when it deduplicates the request onto a waiter
  // that is already parked. That waiter IS this item, and its id is the only thing the X can cancel.
  const settled = harness.start(SERVICE_ENTRY, {
    operationId: 'wait-dup',
    status: 'waiting'
  });
  await flush();

  assert.deepEqual(harness.capturedOperationIds, ['wait-dup'], 'the parked waiter id must be kept');
  assert.deepEqual(harness.awaitedOperationIds, ['wait-dup']);

  harness.triggerCancel();
  assert.deepEqual(harness.canceledOperations, ['wait-dup'], 'the X must cancel that operation');
  assert.deepEqual(harness.reportedFailures, [], 'a cancel that was sent is not a failure');

  // The removal never ran, so its end is the waiter's own canceled end.
  harness.end({ operationId: 'wait-dup', status: 'canceled' });
  await settled;

  assert.equal(harness.cancelRunCount(), 1, 'a canceled waiting item ends the run as canceled');
});

test('a game item parked behind another operation is cancelable while it waits', async () => {
  const harness = createBatchHarness();
  const settled = harness.start(
    { kind: 'game', game: GAME_FIXTURES[0].game },
    { operationId: 'wait-game', status: 'waiting' }
  );
  await flush();

  assert.deepEqual(harness.capturedOperationIds, ['wait-game']);

  harness.triggerCancel();
  assert.deepEqual(harness.canceledOperations, ['wait-game']);

  harness.end({ operationId: 'wait-game', status: 'canceled' });
  await settled;

  assert.equal(harness.cancelRunCount(), 1);
});

test('the id of a removal this batch did not start stays out of reach of the X', async () => {
  const harness = createBatchHarness();
  const settled = harness.start(SERVICE_ENTRY, {
    operationId: 'other-op',
    status: 'alreadyRunning'
  });
  await flush();

  assert.deepEqual(
    harness.capturedOperationIds,
    [null],
    'the request is answered, but with no id this batch owns - that removal has its own card'
  );
  assert.deepEqual(harness.awaitedOperationIds, ['other-op'], 'the item still waits for it');
  assert.deepEqual(harness.notificationsRef.current[0].details.itemOperationIds, []);

  harness.triggerCancel();
  assert.deepEqual(harness.canceledOperations, [], 'the X must not reach across and end it');

  harness.end({ operationId: 'other-op', status: 'completed' });
  await settled;
});

test('a fresh park and an immediate start both keep the id and wait on it', async () => {
  for (const [response, id] of [
    [{ operationId: 'wait-fresh', status: 'waiting' }, 'wait-fresh'],
    [{ operationId: 'run-now', status: 'running' }, 'run-now']
  ]) {
    const harness = createBatchHarness();
    const settled = harness.start(SERVICE_ENTRY, response);
    await flush();
    assert.deepEqual(harness.capturedOperationIds, [id]);
    assert.deepEqual(harness.awaitedOperationIds, [id]);
    assert.deepEqual(harness.notificationsRef.current[0].details.itemOperationIds, [id]);
    harness.end({ operationId: id, status: 'completed' });
    await settled;
    assert.equal(harness.cancelRunCount(), 0);
  }
});

test('waiting items of every platform settle on the end of the run they were promoted to', async () => {
  for (const fixture of GAME_FIXTURES) {
    const harness = createBatchHarness();
    const waitingId = `wait-${fixture.label}`;
    const settled = harness.start(
      { kind: 'game', game: fixture.game },
      { operationId: waitingId, status: 'waiting' }
    );
    await flush();

    // The store's waiter follows the promotion and answers with the operation that did the work.
    harness.end({ operationId: `run-${fixture.label}`, status: 'completed' });
    await settled;

    assert.deepEqual(harness.capturedOperationIds, [waitingId], `${fixture.label}: one id kept`);
    assert.deepEqual(harness.awaitedOperationIds, [waitingId]);
    assert.equal(harness.cancelRunCount(), 0);
  }
});

test('progress follows the item through its promotion to the operation doing the work', async () => {
  const harness = createBatchHarness();
  const settled = harness.start(
    { kind: 'game', game: GAME_FIXTURES[0].game },
    { operationId: 'wait-1111', status: 'waiting' }
  );
  await flush();
  assert.equal(harness.signalR.listenerCount('GameRemovalProgress'), 1);

  harness.runsRef.current = [
    { id: 'wait-1111', details: { operationId: 'wait-1111', operationIds: ['wait-1111'] } }
  ];
  harness.signalR.emit('GameRemovalProgress', { operationId: 'wait-1111', percentComplete: 5 });

  // Promoted onto an operation that was already running: the card keeps the OLDER id, so only
  // the merged ids lead from this item's id to the one its progress now carries.
  harness.runsRef.current = [
    {
      id: 'older-card',
      details: { operationId: 'run-2222', operationIds: ['older-card', 'wait-1111', 'run-2222'] }
    }
  ];
  harness.signalR.emit('GameRemovalProgress', { operationId: 'run-2222', percentComplete: 40 });
  harness.signalR.emit('GameRemovalProgress', { operationId: 'someone-else', percentComplete: 90 });
  harness.signalR.emit('GameRemovalProgress', { operationId: 'wait-1111', percentComplete: 7 });

  assert.deepEqual(harness.percents, [5, 40], 'only the live operation of this item moves the bar');

  harness.end({ operationId: 'run-2222', status: 'completed' });
  await settled;
  assert.equal(harness.signalR.listenerCount('GameRemovalProgress'), 0, 'the listener is removed');
});

test('progress sent before the request is answered is not claimed', async () => {
  const harness = createBatchHarness();
  const settled = harness.start(SERVICE_ENTRY, {
    operationId: 'run-a',
    status: 'running'
  });
  assert.equal(harness.signalR.listenerCount('ServiceRemovalProgress'), 1, 'listening first');
  harness.signalR.emit('ServiceRemovalProgress', { operationId: 'run-a', percentComplete: 10 });
  await flush();
  harness.signalR.emit('ServiceRemovalProgress', { operationId: 'run-a', percentComplete: 20 });
  assert.deepEqual(harness.percents, [20]);
  harness.end({ operationId: 'run-a', status: 'completed' });
  await settled;
});

test('each way a run ends settles the item the way the batch counts it', async () => {
  const outcomes = [
    [{ status: 'failed', error: 'disk full' }, 'disk full'],
    [{ status: 'failed' }, ''],
    [{ status: 'skipped' }, ''],
    [{ status: 'skipped', error: 'nothing to remove' }, 'nothing to remove']
  ];
  for (const [terminal, message] of outcomes) {
    const harness = createBatchHarness();
    const settled = harness.start(SERVICE_ENTRY, {
      operationId: 'run-x',
      status: 'running'
    });
    await flush();
    harness.end({ operationId: 'run-x', ...terminal });
    await assert.rejects(settled, { message }, `${terminal.status} fails the item`);
    assert.equal(harness.cancelRunCount(), 0);
  }
});

test('an item whose run the browser lost after X ends canceled, not failed', async () => {
  const harness = createBatchHarness();
  const settled = harness.start(SERVICE_ENTRY, {
    operationId: 'run-x',
    status: 'running'
  });
  await flush();
  harness.triggerCancel();
  harness.end({ operationId: 'run-x', status: 'gone' });
  await settled;
  assert.equal(harness.cancelRunCount(), 1);
});

test('an item whose run the browser lost without X counts as done', async () => {
  const harness = createBatchHarness();
  const settled = harness.start(SERVICE_ENTRY, {
    operationId: 'run-x',
    status: 'running'
  });
  await flush();
  harness.end({ operationId: 'run-x', status: 'gone' });
  await settled;
  assert.equal(harness.cancelRunCount(), 0);
});

test('a cancel with no operation id yet ends the run cleanly and reports nothing', async () => {
  const harness = createBatchHarness();
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

  assert.deepEqual(harness.canceledOperations, [], 'there is no operation to cancel yet');
  assert.deepEqual(harness.reportedFailures, [], 'a cancel that stopped the run is not a failure');
  assert.equal(finalized.length, 1, 'the run settles exactly once');
  assert.equal(finalized[0].canceled, true, 'and it settles as canceled');
  assert.equal(finalized[0].succeeded, 0, 'with the item that was in flight counted as neither');
  assert.equal(finalized[0].failed, 0);
});

test('publishing the item operation id keeps the item types and every owned id', () => {
  const harness = createBatchHarness();

  harness.ctx.setOperationId('wait-dup');
  const withId = harness.notificationsRef.current[0];
  assert.equal(withId.details.currentOperationId, 'wait-dup');
  assert.deepEqual(withId.details.itemTypes, ['service_removal', 'game_removal']);
  assert.deepEqual(withId.details.itemOperationIds, ['wait-dup']);

  harness.ctx.setOperationId(null);
  const cleared = harness.notificationsRef.current[0];
  assert.equal(cleared.details.currentOperationId, undefined);
  assert.deepEqual(cleared.details.itemTypes, ['service_removal', 'game_removal']);
  assert.deepEqual(cleared.details.itemOperationIds, ['wait-dup'], 'an owned id is never dropped');

  harness.ctx.setOperationId('run-next');
  assert.deepEqual(harness.notificationsRef.current[0].details.itemOperationIds, [
    'wait-dup',
    'run-next'
  ]);
});

test('a log batch item waits on the id its removal request answers with', async () => {
  const harness = createBatchHarness('runLogQueue');
  const settled = harness.start(
    { datasource: 'default', service: 'steam' },
    { operationId: 'log-1', status: 'running' }
  );
  await flush();
  assert.deepEqual(harness.capturedOperationIds, ['log-1']);
  assert.deepEqual(harness.awaitedOperationIds, ['log-1']);
  assert.equal(harness.signalR.listenerCount('LogRemovalProgress'), 1);
  harness.end({ operationId: 'log-1', status: 'completed' });
  await settled;
  assert.equal(harness.signalR.listenerCount('LogRemovalProgress'), 0);
});

const CONTEXT_PATH = 'src/contexts/notifications/NotificationsContext.tsx';
const modules = await loadNotificationModules();

/**
 * One item's wait on a run this browser then misses while disconnected. The waiter, the snapshot
 * apply and the follow-up question are the shipped provider code; the server's answers to that
 * question are the stub, one per question in order, with no null keys as the REST answers omit
 * them. An answer that is an Error fails that question (a network error). No push comes for a run
 * that already ended, so the waiter has to ask again by itself; timers are held here so the test
 * fires the retry instead of sleeping.
 */
const createReconnectHarness = (answers) => {
  class ApiError extends Error {}
  const storeRef = { current: modules.createRunStoreState() };
  const waitersRef = { current: new Set() };
  // One ref for the probe and the unmount effect, so running the effect flips what the probe reads.
  const mountedRef = { current: true };
  const timers = new Map();
  const cleared = [];
  let nextTimer = 0;
  const setTimeout = (callback, delay) => {
    nextTimer += 1;
    timers.set(nextTimer, { callback, delay });
    return nextTimer;
  };
  const clearTimeout = (timer) => {
    if (timer === undefined) return;
    cleared.push(timer);
    timers.delete(timer);
  };
  const asked = [];
  const followWaiter = bindLifted(liftHookCallback(CONTEXT_PATH, 'useMemo', 'probeRun'), {
    storeRef,
    waitersRef,
    mountedRef,
    locateRun: modules.locateRun,
    ApiService: {
      getTrackedOperation: async (operationId) => {
        asked.push(operationId);
        const answer = answers.shift();
        if (answer instanceof Error) throw answer;
        return answer;
      }
    },
    ApiError,
    isTerminalNotificationStatus: modules.isTerminalNotificationStatus,
    endStatus: modules.endStatus,
    setTimeout,
    clearTimeout,
    isAdminRef: { current: true },
    isConnectedRef: { current: true },
    RUN_LIST_RETRY_DELAY_MS: 5000
  })();
  const commitApply = bindLifted(liftHookCallback(CONTEXT_PATH, 'useCallback', 'result.ended'), {
    storeRef,
    waitersRef,
    followWaiter,
    settleBulk: () => undefined,
    fadeLeavingRuns: () => undefined,
    commit: () => undefined
  });
  const waitForRunEnd = bindLifted(liftHookCallback(CONTEXT_PATH, 'useCallback', 'RunWaiter'), {
    waitersRef,
    followWaiter,
    clearTimeout
  });
  const unmount = bindLifted(
    liftHookCallback(CONTEXT_PATH, 'useEffect', 'mountedRef.current = false'),
    {
      autoDismissTimersRef: { current: new Map() },
      snapshotRetryRef: { current: undefined },
      waitersRef,
      mountedRef,
      clearTimeout
    }
  );
  return {
    asked,
    timers,
    cleared,
    unmount,
    /** The item starts waiting on a live run, then a reconnect snapshot no longer lists it. */
    async waitThenMissIt(signal) {
      storeRef.current = pushRun(modules, storeRef.current, operationRunRow('run-x'));
      const waiting = waitForRunEnd('run-x', signal);
      commitApply(
        modules.applySnapshot(
          storeRef.current,
          { runs: [], revision: nextRunRevision() },
          {
            keepSuccessVisible: false,
            localCards: [],
            requestSeq: 1,
            issuedSeq: 1,
            sessionId: null
          }
        ),
        true
      );
      await flush();
      return waiting;
    },
    /** Fires the one pending retry. */
    async fireRetry() {
      assert.equal(timers.size, 1, 'one retry is pending');
      const [[timer, { callback, delay }]] = [...timers];
      assert.equal(delay, 5000);
      timers.delete(timer);
      callback();
      await flush();
    }
  };
};

/** The batch's own settle step for one ended item, counting how often it cancels the batch. */
const settleItem = (end) => {
  let cancelCalls = 0;
  settleBatchItem({
    end,
    ctx: {
      cancelRun: () => {
        cancelCalls += 1;
      },
      wasCanceled: () => false
    }
  });
  return cancelCalls;
};

test('a run a reconnect snapshot drops is asked about once and its kept ending cancels the batch', async () => {
  const harness = createReconnectHarness([{ status: 'canceled' }]);
  const end = await harness.waitThenMissIt();
  assert.deepEqual(harness.asked, ['run-x']);
  assert.equal(end.status, 'canceled');
  assert.equal(settleItem(end), 1);
});

test('a run a reconnect snapshot drops that the server no longer keeps counts as done', async () => {
  const harness = createReconnectHarness([{}]);
  const end = await harness.waitThenMissIt();
  assert.equal(end.status, 'gone');
  assert.equal(settleItem(end), 0);
});

test('a failed question about a missed run is asked again, and its ending lets the batch go on', async () => {
  const harness = createReconnectHarness([
    new TypeError('Failed to fetch'),
    { status: 'completed' }
  ]);
  let end = null;
  const waiting = harness.waitThenMissIt().then((value) => {
    end = value;
  });
  await flush();
  assert.deepEqual(harness.asked, ['run-x']);
  assert.equal(end, null, 'a failed question settles nothing');

  await harness.fireRetry();
  await waiting;
  assert.deepEqual(harness.asked, ['run-x', 'run-x']);
  assert.equal(end.status, 'completed');
  assert.equal(settleItem(end), 0);
});

test('a waiter that ends while its retry waits, or a provider that unmounts, leaves no retry behind', async () => {
  const aborted = createReconnectHarness([new TypeError('Failed to fetch')]);
  const controller = new AbortController();
  const waiting = aborted.waitThenMissIt(controller.signal);
  await flush();
  const [pending] = [...aborted.timers.keys()];
  assert.ok(pending, 'the failed question scheduled a retry');
  controller.abort();
  assert.equal((await waiting).status, 'gone');
  assert.ok(aborted.cleared.includes(pending), 'settling the waiter clears its retry');
  assert.equal(aborted.timers.size, 0);

  const unmounted = createReconnectHarness([new TypeError('Failed to fetch')]);
  void unmounted.waitThenMissIt();
  await flush();
  const [held] = [...unmounted.timers.keys()];
  assert.ok(held, 'the failed question scheduled a retry');
  unmounted.unmount()();
  assert.ok(unmounted.cleared.includes(held), 'unmounting clears every waiter retry');
  assert.equal(unmounted.timers.size, 0);
});

test('a question that fails after the provider unmounted schedules no retry', async () => {
  const answer = Promise.withResolvers();
  const harness = createReconnectHarness([answer.promise]);
  void harness.waitThenMissIt();
  await flush();
  assert.deepEqual(harness.asked, ['run-x'], 'the question is on the wire');
  harness.unmount()();
  answer.reject(new TypeError('Failed to fetch'));
  await flush();
  assert.equal(harness.timers.size, 0, 'no retry outlives the provider');
});

test('a question that fails after its waiter was aborted schedules no retry', async () => {
  const answer = Promise.withResolvers();
  const harness = createReconnectHarness([answer.promise]);
  const controller = new AbortController();
  const waiting = harness.waitThenMissIt(controller.signal);
  await flush();
  assert.deepEqual(harness.asked, ['run-x'], 'the question is on the wire');
  controller.abort();
  assert.equal((await waiting).status, 'gone');
  answer.reject(new TypeError('Failed to fetch'));
  await flush();
  assert.equal(harness.timers.size, 0, 'no retry for a waiter that already ended');
});
