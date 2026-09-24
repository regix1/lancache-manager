import assert from 'node:assert/strict';
import test from 'node:test';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  loadNotificationModules,
  operationRunRow,
  pushRun
} from './transpile-module.mjs';

/**
 * Exercises the real useBatchQueue compiled from product source.
 *
 * The behavior under test: cancelling a batch must not settle the in-flight item's wait
 * early. The batch card has to stay 'running' until that item's own terminal event has
 * arrived, because findBulkCardOwningType only suppresses a per-item card while the batch
 * card is running or waiting - a batch finalized before the event lands puts a second card
 * ("<game> removal cancelled") next to "Bulk removal cancelled after N items". The click
 * still has to act immediately: the server-side cancel fires at once (or as soon as the
 * operation id exists), and the card message flips to the cancelling line.
 *
 * The batch card also keeps every item operation id it owned, so an item's kept failure stays
 * folded inside the batch card after the batch has moved on or ended, canceled or not.
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

const storeModules = await loadNotificationModules(
  moduleUrl('export default { t: (key) => key, exists: () => true };')
);
const { createRunStoreState, deriveNotifications, settleBulkCards } = storeModules;

/**
 * Builds fresh stub modules and a freshly compiled hook per test - data URLs are cached by
 * content, so the nonce keeps one test's state out of the next.
 */
const loadQueueHook = async (nonce) => {
  const reactUrl = moduleUrl(`// ${nonce}
export const __effects = [];
export const useCallback = (fn) => fn;
export const useRef = (initial) => ({ current: initial });
export const useState = (initial) => [initial, () => undefined];
export const useEffect = (fn) => {
  __effects.push(fn);
};`);

  const apiUrl = moduleUrl(`// ${nonce}
export const cancelCalls = [];
const ApiService = {
  cancelOperation: (opId) => {
    cancelCalls.push(opId);
    return Promise.resolve({});
  }
};
export default ApiService;`);

  const notificationsUrl = moduleUrl(`// ${nonce}
export const notifications = [];
export const updateNotification = (id, updates) => {
  const found = notifications.find((n) => n.id === id);
  if (found) {
    Object.assign(found, typeof updates === 'function' ? updates(found) : updates);
  }
};
export const useNotifications = () => ({ notifications, updateNotification });`);

  const i18nUrl = moduleUrl(`// ${nonce}
export default { t: (key) => key };`);

  const errorHandlerUrl = moduleUrl(`// ${nonce}
export const useErrorHandler = () => ({ notifyError: () => undefined });`);

  const hookUrl = await compileToUrl('../src/hooks/useBatchQueue.ts', {
    react: reactUrl,
    '@services/api.service': apiUrl,
    '@contexts/notifications': notificationsUrl,
    '@/i18n': i18nUrl,
    './useErrorHandler': errorHandlerUrl
  });

  return {
    hook: await import(hookUrl),
    react: await import(reactUrl),
    api: await import(apiUrl),
    notificationsModule: await import(notificationsUrl)
  };
};

/** Starts a run whose current item settles only when the test says its terminal event landed. */
const startRun = (hook, notifications, items) => {
  const { run } = hook.useBatchQueue();
  const finalizeCalls = [];
  const itemContexts = [];
  let settleItem;

  const runPromise = run({
    items,
    openNotification: () => {
      notifications.push({
        id: 'bulk',
        type: 'bulk_removal',
        status: 'running',
        details: { itemTypes: ['game_removal'], cancelling: false }
      });
      return 'bulk';
    },
    processItem: (_item, ctx) => {
      itemContexts.push(ctx);
      return new Promise((resolve) => {
        settleItem = resolve;
        // Stand-in for the completion wait. If the hook ever hands the item a cancel
        // signal that settles it early again, this resolves on the click instead of on
        // the terminal event, and the finalize-ordering assertions below catch it.
        ctx.signal?.addEventListener?.('abort', () => resolve(), { once: true });
      });
    },
    finalize: (args) => {
      finalizeCalls.push(args);
    }
  });

  return { runPromise, finalizeCalls, itemContexts, settleCurrentItem: () => settleItem() };
};

const clickCancel = (notifications, effects) => {
  notifications.find((n) => n.id === 'bulk').details.cancelling = true;
  effects.forEach((effect) => effect());
};

test('cancel keeps the batch card live until the in-flight item settles', async () => {
  const { hook, react, api, notificationsModule } = await loadQueueHook('settle');
  const { notifications } = notificationsModule;

  const { runPromise, finalizeCalls, itemContexts, settleCurrentItem } = startRun(
    hook,
    notifications,
    ['a', 'b']
  );
  itemContexts[0].setOperationId('op-1');

  clickCancel(notifications, react.__effects);

  assert.deepEqual(api.cancelCalls, ['op-1'], 'the click must cancel the in-flight item at once');
  assert.equal(
    notifications.find((n) => n.id === 'bulk').message,
    'common.notifications.cancelling',
    'the click must be visible on the card while the item settles'
  );
  await Promise.resolve();
  assert.equal(
    finalizeCalls.length,
    0,
    'the batch card must still be running while its item has not reached its terminal event'
  );

  settleCurrentItem();
  await runPromise;

  assert.deepEqual(finalizeCalls, [
    { id: 'bulk', succeeded: 0, failed: 0, cancelled: true, total: 2 }
  ]);
  assert.equal(itemContexts.length, 1, 'the second item must never start after a cancel');
});

test('a cancel that lands before the operation id fires the cancel when the id arrives', async () => {
  const { hook, react, api, notificationsModule } = await loadQueueHook('deferred');
  const { notifications } = notificationsModule;

  const { runPromise, finalizeCalls, itemContexts, settleCurrentItem } = startRun(
    hook,
    notifications,
    ['a']
  );

  clickCancel(notifications, react.__effects);
  assert.deepEqual(api.cancelCalls, [], 'no id exists yet, so nothing can be cancelled');

  itemContexts[0].setOperationId('op-9');
  assert.deepEqual(
    api.cancelCalls,
    ['op-9'],
    'the id arriving after the click must cancel the item rather than let it run out'
  );

  settleCurrentItem();
  await runPromise;
  assert.equal(finalizeCalls[0].cancelled, true);
});

test('an uncancelled run still finalizes with its full tally', async () => {
  const { hook, api, notificationsModule } = await loadQueueHook('complete');
  const { notifications } = notificationsModule;
  const { run } = hook.useBatchQueue();
  const finalizeCalls = [];

  await run({
    items: ['a', 'b'],
    openNotification: () => {
      notifications.push({
        id: 'bulk',
        type: 'bulk_removal',
        status: 'running',
        details: { itemTypes: ['game_removal'], cancelling: false }
      });
      return 'bulk';
    },
    processItem: () => Promise.resolve(),
    finalize: (args) => {
      finalizeCalls.push(args);
    }
  });

  assert.deepEqual(finalizeCalls, [
    { id: 'bulk', succeeded: 2, failed: 0, cancelled: false, total: 2 }
  ]);
  assert.deepEqual(api.cancelCalls, []);
});

// The shipped finalize writer, run against the same card the queue writes.
const finalizeBulkRemovalNotification = bindLifted(
  liftConstArrow(
    'src/components/features/management/game-detection/cacheRemovalHelpers.ts',
    'finalizeBulkRemovalNotification'
  ),
  { FULL_PROGRESS_PERCENT: 100 }
);
const FINALIZE_TEXT = {
  completeKey: 'complete',
  completeDefaultValue: '',
  partialFailureKey: 'partial',
  partialFailureDefaultValue: '',
  cancelledKey: 'cancelled',
  cancelledDefaultValue: '',
  cancelledWithFailuresKey: 'cancelledWithFailures',
  cancelledWithFailuresDefaultValue: ''
};

const openBatchCard = (notifications) => () => {
  notifications.push({
    id: 'bulk',
    type: 'bulk_removal',
    status: 'running',
    message: '',
    details: { itemTypes: ['game_removal'], itemOperationIds: [] }
  });
  return 'bulk';
};

const finalizeWith =
  (updateNotification) =>
  ({ id, succeeded, failed, cancelled, total }) =>
    finalizeBulkRemovalNotification({
      id,
      succeeded,
      failed,
      total,
      cancelled,
      t: (key) => key,
      updateNotification,
      text: FINALIZE_TEXT
    });

test('a kept failure stays folded inside a batch that is then canceled', async () => {
  const { hook, react, notificationsModule } = await loadQueueHook('cancelled-keeps');
  const { notifications, updateNotification } = notificationsModule;
  const { run } = hook.useBatchQueue();
  let settleSecond;

  const runPromise = run({
    items: ['a', 'b', 'c'],
    openNotification: openBatchCard(notifications),
    processItem: (item, ctx) => {
      ctx.setOperationId(`op-${item}`);
      if (item === 'a') return Promise.reject(new Error('Game removal failed for a'));
      return new Promise((resolve) => {
        settleSecond = resolve;
      });
    },
    finalize: finalizeWith(updateNotification)
  });

  await new Promise((resolve) => setImmediate(resolve));
  clickCancel(notifications, react.__effects);
  settleSecond();
  await runPromise;

  const card = notifications.find((n) => n.id === 'bulk');
  assert.equal(card.status, 'cancelled', 'a canceled batch ends as canceled');
  assert.equal(card.details.cancelled, true);
  assert.deepEqual(card.details.itemOperationIds, ['op-a', 'op-b'], 'no owned id is dropped');
  assert.deepEqual(card.details.itemTypes, ['game_removal']);
  assert.equal(card.details.currentOperationId, undefined);

  const state = pushRun(
    storeModules,
    createRunStoreState(),
    operationRunRow('op-a', {
      operationType: 'gameRemoval',
      name: 'Game Removal',
      status: 'failed',
      error: 'disk full',
      retained: true
    }),
    notifications
  );
  const drawn = deriveNotifications(state, notifications);
  assert.deepEqual(
    drawn.map((n) => n.id),
    ['bulk'],
    'the kept failure draws no card beside the batch card'
  );
  assert.deepEqual(drawn[0].details.closeOperationIds, ['op-a']);
});

test('an item that failed before the server answered keeps its failed batch card', async () => {
  const { hook, notificationsModule } = await loadQueueHook('failed-without-run');
  const { notifications, updateNotification } = notificationsModule;
  const { run } = hook.useBatchQueue();

  await run({
    items: ['a', 'b'],
    openNotification: openBatchCard(notifications),
    processItem: (item, ctx) => {
      if (item === 'a') return Promise.reject(new Error('request refused'));
      ctx.setOperationId('op-b');
      return Promise.resolve();
    },
    finalize: finalizeWith(updateNotification)
  });

  const card = notifications.find((n) => n.id === 'bulk');
  assert.equal(card.status, 'failed');
  assert.equal(card.details.failedWithoutRun, true);
  assert.deepEqual(card.details.itemOperationIds, ['op-b']);

  const state = { ...createRunStoreState(), keptBatches: new Set(['bulk']) };
  assert.deepEqual(
    settleBulkCards(state, notifications).release,
    [],
    'no other screen can close that failure, so the batch card waits to be closed here'
  );
});
