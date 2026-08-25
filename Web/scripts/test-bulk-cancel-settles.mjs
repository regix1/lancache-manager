import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

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
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

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
export const dismissCalls = [];
export const useNotifications = () => ({
  notifications,
  scheduleAutoDismiss: (id) => {
    dismissCalls.push(id);
  },
  updateNotification: (id, updates) => {
    const found = notifications.find((n) => n.id === id);
    if (found) {
      Object.assign(found, updates);
    }
  }
});`);

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
  const { notifications, dismissCalls } = notificationsModule;

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
  assert.ok(dismissCalls.includes('bulk'), 'the finalized batch card must still auto-dismiss');
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
  const { notifications, dismissCalls } = notificationsModule;
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
  assert.ok(dismissCalls.includes('bulk'));
});
