import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * Exercises the real handleCancel compiled from product source.
 *
 * The server broadcasts the terminal canceled event before it answers the cancel request, so the
 * card already carries `details.cancelled` by the time the request settles. handleCancel's failure
 * branch used to patch `details` from the card it captured at click time, and updateNotification
 * merges at the top level, so that patch replaced the whole nested object and dropped the flag the
 * canceled card reads and colors itself from. The user saw a red "Failed to cancel operation"
 * card beside work that had in fact stopped.
 */

/**
 * The merge NotificationsContext performs, copied because a .tsx module cannot be imported here.
 * Every element is replaced rather than mutated, so a caller holding the old object keeps a stale
 * snapshot - which is the production behavior under test.
 */
const applyUpdate = (notifications, id, updates) => {
  const index = notifications.findIndex((n) => n.id === id);
  if (index === -1) {
    return;
  }
  const current = notifications[index];
  notifications[index] = {
    ...current,
    ...(typeof updates === 'function' ? updates(current) : updates)
  };
};

const loadCancel = async (nonce) => {
  const apiUrl = moduleUrl(`// ${nonce}
export let cancelImpl = () => Promise.resolve({});
export const setCancel = (fn) => {
  cancelImpl = fn;
};
const ApiService = {
  cancelOperation: (operationId) => cancelImpl(operationId),
  forceKillOperation: () => Promise.resolve({})
};
export default ApiService;`);

  const errorUrl = moduleUrl(`// ${nonce}
export const getErrorMessage = (err) => (err instanceof Error ? err.message : String(err));`);

  const i18nUrl = moduleUrl(`// ${nonce}
export default { t: (key) => key };`);

  // The real map, compiled from source rather than stubbed, so the color assertions below run the
  // whole path a card is drawn from: status -> badge variant -> color token.
  const statusVariantUrl = await compileToUrl('../src/utils/statusVariant.ts');

  const registryUrl = moduleUrl(`// ${nonce}
export const NOTIFICATION_REGISTRY = [
  { type: 'game_detection', cancelKind: 'serverOp', cancelTooltipKey: 'common.actions.cancel' }
];`);

  const constantsUrl = moduleUrl(`// ${nonce}
export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`);

  const cancelUrl = await compileToUrl('../src/components/common/notificationCancel.ts', {
    '@services/api.service': apiUrl,
    '@utils/error': errorUrl,
    '../../i18n': i18nUrl,
    '@utils/statusVariant': statusVariantUrl,
    '@contexts/notifications/notificationRegistry': registryUrl,
    '@utils/constants': constantsUrl
  });

  const toasts = [];
  globalThis.window = {
    dispatchEvent: (event) => {
      toasts.push({ type: event.type, detail: event.detail });
    }
  };

  return { cancel: await import(cancelUrl), api: await import(apiUrl), toasts };
};

/** A running game detection card holding the operation the click will cancel. */
const runningCard = () => ({
  id: 'n1',
  type: 'game_detection',
  status: 'running',
  message: 'Detecting games',
  details: { operationId: 'op-1' }
});

const driveCancel = async (cancel, notifications, card) => {
  const removed = [];
  await cancel.handleCancel(
    card,
    (id, updates) => applyUpdate(notifications, id, updates),
    (id) => removed.push(id),
    (id) => notifications.find((n) => n.id === id)
  );
  return removed;
};

test('a cancel failure keeps the canceled flag the terminal event already wrote', async () => {
  const { cancel, api, toasts } = await loadCancel('failure');
  const notifications = [runningCard()];
  const card = notifications[0];

  api.setCancel(
    () =>
      new Promise((_resolve, reject) => {
        // The terminal event lands first, exactly as the server orders them.
        queueMicrotask(() => {
          applyUpdate(notifications, 'n1', (n) => ({
            status: 'cancelled',
            message: 'Game detection cancelled',
            details: { ...n.details, cancelled: true }
          }));
          reject(new Error('An unexpected error occurred'));
        });
      })
  );

  await driveCancel(cancel, notifications, card);

  const after = notifications[0];
  assert.equal(
    after.details.cancelled,
    true,
    'the failure branch must not erase the canceled flag the card is drawn gray from'
  );
  assert.equal(after.status, 'cancelled');
  assert.equal(after.message, 'Game detection cancelled');
  assert.equal(after.details.cancelRequested, false, 'the reset the failure branch exists for');
  assert.deepEqual(
    toasts.map((t) => t.detail.message),
    ['common.notifications.cancelOperationFailed'],
    'a genuine cancel failure still has to reach the user'
  );
});

test('a cancel the server accepts raises no failure toast', async () => {
  const { cancel, api, toasts } = await loadCancel('accepted');
  const notifications = [runningCard()];

  api.setCancel(() => Promise.resolve({}));
  const removed = await driveCancel(cancel, notifications, notifications[0]);

  assert.deepEqual(toasts, [], 'a cancel that worked must not show a failure card');
  assert.deepEqual(removed, [], 'the card stays until its own terminal event arrives');
  assert.equal(notifications[0].details.cancelSent, true);
});

test('a canceled card draws gray and only a failure draws red', async () => {
  const { cancel } = await loadCancel('colors');

  // What the user asked for, at the one function both renderers color from: the full card reads it
  // directly and the condensed strip is handed the result by the bar.
  assert.equal(
    cancel.getNotificationColor({
      id: 'n1',
      type: 'game_detection',
      status: 'cancelled',
      details: { cancelled: true }
    }),
    'var(--theme-text-secondary)',
    'a run the user stopped is gray'
  );
  assert.equal(
    cancel.getNotificationColor({
      id: 'n1',
      type: 'game_detection',
      status: 'cancelled',
      details: {}
    }),
    'var(--theme-text-secondary)',
    'the status alone is enough, even with the details flag erased'
  );
  // The red the user was seeing came from a second card, not from the canceled one: the failure
  // branch raises a generic toast carrying an error type, which is the only thing here drawn red.
  assert.equal(
    cancel.getNotificationColor({
      id: 'generic_x',
      type: 'generic',
      status: 'completed',
      details: { notificationType: 'error' }
    }),
    'var(--theme-error)',
    'a genuine failure still reads red'
  );
});

test('a cancel for an operation that is already gone drops the card', async () => {
  const { cancel, api, toasts } = await loadCancel('gone');
  const notifications = [runningCard()];

  api.setCancel(() => Promise.reject(new Error('Operation not found')));
  const removed = await driveCancel(cancel, notifications, notifications[0]);

  assert.deepEqual(removed, ['n1']);
  assert.deepEqual(toasts, []);
});
