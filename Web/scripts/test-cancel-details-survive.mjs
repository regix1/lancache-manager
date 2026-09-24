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
export const sent = [];
const ApiService = {
  cancelOperation: (operationId) => {
    sent.push(operationId);
    return cancelImpl(operationId);
  },
  forceKillOperation: () => Promise.resolve({})
};
export default ApiService;`);

  const errorUrl = moduleUrl(`// ${nonce}
export const isAbortError = (err) => err instanceof Error && err.name === 'AbortError';
export const getErrorMessage = (err) => err.message;`);

  const i18nUrl = moduleUrl(`// ${nonce}
export default { t: (key) => key };`);

  // The real map, compiled from source rather than stubbed, so the variant assertions below run
  // the whole path a card is drawn from: status -> badge variant.
  const statusVariantUrl = await compileToUrl('../src/utils/statusVariant.ts');

  const registryUrl = moduleUrl(`// ${nonce}
export const NOTIFICATION_REGISTRY = [
  { type: 'game_detection', cancelKind: 'serverOp', cancelTooltipKey: 'common.actions.cancel' },
  { type: 'prefill_login', cancelKind: 'serverOp', cancelTooltipKey: 'common.actions.cancel' }
];`);

  const constantsUrl = moduleUrl(`// ${nonce}
export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`);

  const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
    '@utils/constants': constantsUrl
  });
  const cancelUrl = await compileToUrl('../src/components/common/notificationCancel.ts', {
    '@services/api.service': apiUrl,
    '@services/apiError': apiErrorUrl,
    '@contexts/notifications/notificationStatus': await compileToUrl(
      '../src/contexts/notifications/notificationStatus.ts'
    ),
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

  return {
    cancel: await import(cancelUrl),
    api: await import(apiUrl),
    ApiError: (await import(apiErrorUrl)).ApiError,
    toasts
  };
};

/** A running game detection run card holding the operation the click will cancel. */
const runningCard = () => ({
  id: 'n1',
  type: 'game_detection',
  status: 'running',
  message: 'Detecting games',
  details: { operationId: 'op-1', operationIds: ['op-1'] }
});

const driveCancel = async (cancel, notifications, card) => {
  const removed = [];
  await cancel.handleCancel(
    card,
    (id, updates) => applyUpdate(notifications, id, updates),
    (id) => removed.push(id),
    () => notifications
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
  assert.equal(
    after.details.cancelRequested,
    true,
    'a late response leaves the terminal flags unchanged'
  );
  assert.deepEqual(
    toasts.map((t) => t.detail.message),
    [],
    'the authoritative cancelled outcome makes the late transport failure irrelevant'
  );
});

test('a cancel the server accepts raises no failure toast', async () => {
  const { cancel, api, toasts } = await loadCancel('accepted');
  const notifications = [runningCard()];

  api.setCancel(() => Promise.resolve({}));
  const removed = await driveCancel(cancel, notifications, notifications[0]);

  assert.deepEqual(toasts, [], 'a cancel that worked must not show a failure card');
  assert.deepEqual(removed, [], 'the card stays until its own run row ends it');
  assert.equal(notifications[0].details.cancelSent, true);
  assert.equal(notifications[0].status, 'cancelling');
});

test('a cancel answered after its card was merged into a newer run still settles that card', async () => {
  const { cancel, api, toasts } = await loadCancel('promotion');
  const notifications = [runningCard()];
  const clicked = notifications[0];

  api.setCancel(
    () =>
      new Promise((resolve) => {
        // Before the answer, the waiting run W (op-1) is promoted onto N (op-2), which was
        // already running: the card becomes N's card, with N's OLDER card id, and lists both ids.
        queueMicrotask(() => {
          const merged = notifications[0];
          notifications[0] = {
            ...merged,
            id: 'n0',
            details: { ...merged.details, operationId: 'op-2', operationIds: ['op-2', 'op-1'] }
          };
          resolve({});
        });
      })
  );
  const removed = await driveCancel(cancel, notifications, clicked);

  const card = notifications[0];
  assert.equal(card.id, 'n0');
  assert.equal(card.details.cancelPending, false, 'the answer clears the pending cancel');
  assert.equal(card.status, 'cancelling');
  // What the background row's Force stop reads: requested and sent, no longer loading.
  assert.equal(card.details.cancelRequested, true);
  assert.equal(card.details.cancelSent, true);
  assert.deepEqual(removed, []);
  assert.deepEqual(toasts, []);
});

test('an answer for an id no card lists changes nothing', async () => {
  const { cancel, api, toasts } = await loadCancel('unlisted');
  const notifications = [runningCard()];
  const clicked = notifications[0];
  let other;

  api.setCancel(
    () =>
      new Promise((resolve) => {
        queueMicrotask(() => {
          // The run's card left, and another run's card took the slot.
          other = {
            ...runningCard(),
            id: 'other',
            details: { operationId: 'op-9', operationIds: ['op-9'] }
          };
          notifications[0] = other;
          resolve({ alreadyFinished: true });
        });
      })
  );
  const removed = await driveCancel(cancel, notifications, clicked);

  assert.deepEqual(removed, []);
  assert.deepEqual(toasts, []);
  assert.equal(notifications[0], other, 'no update reached the other run card');
});

test('a sign-in card is found by its own id when the answer lands', async () => {
  // Steam and Xbox sign-in cards are local cards: only `details.operationId`, no merged ids.
  const signIn = (service) => ({
    id: `login-${service}`,
    type: 'prefill_login',
    status: 'running',
    message: `${service} sign-in`,
    details: { operationId: `login-op-${service}`, service }
  });

  for (const service of ['steam', 'xbox']) {
    const accepted = await loadCancel(`login-accepted-${service}`);
    const acceptedCards = [signIn(service)];
    accepted.api.setCancel(() => Promise.resolve({ message: 'Cancellation requested' }));
    await driveCancel(accepted.cancel, acceptedCards, acceptedCards[0]);
    assert.equal(acceptedCards[0].details.cancelPending, false, `${service}: pending cleared`);
    assert.equal(acceptedCards[0].status, 'cancelling', `${service}: shows cancelling`);
    assert.deepEqual(accepted.toasts, []);

    const failed = await loadCancel(`login-failed-${service}`);
    const failedCards = [signIn(service)];
    failed.api.setCancel(() => Promise.reject(new Error('Server unreachable')));
    await driveCancel(failed.cancel, failedCards, failedCards[0]);
    assert.deepEqual(
      failed.toasts.map((toast) => toast.detail),
      [
        {
          type: 'error',
          message: 'common.notifications.cancelOperationFailed',
          error: 'Server unreachable'
        }
      ],
      `${service}: the failure raises the cancel error toast with its reason`
    );
    assert.equal(failedCards[0].details.cancelPending, false);
    assert.equal(failedCards[0].details.cancelRequested, false);
    assert.equal(failedCards[0].details.cancelSent, false);

    const finished = await loadCancel(`login-finished-${service}`);
    const finishedCards = [signIn(service)];
    finished.api.setCancel(() => Promise.resolve({ message: 'done', alreadyFinished: true }));
    const removed = await driveCancel(finished.cancel, finishedCards, finishedCards[0]);
    assert.deepEqual(removed, [`login-${service}`], `${service}: a finished sign-in leaves`);
  }
});

test('a card without an operation id sends no cancel', async () => {
  const { cancel, api } = await loadCancel('no-id');
  const notifications = [{ ...runningCard(), details: {} }];
  await driveCancel(cancel, notifications, notifications[0]);
  assert.deepEqual(api.sent, []);
  assert.deepEqual(notifications[0].details, {});
});

test('a canceled card draws gray, a warning amber, and only a failure red', async () => {
  const { cancel } = await loadCancel('variants');

  // What the user asked for, at the one function both renderers take their status class from:
  // the full card reads it directly and the condensed strip is handed the result by the bar.
  assert.equal(
    cancel.getNotificationVariant({
      id: 'n1',
      type: 'game_detection',
      status: 'cancelled',
      details: { cancelled: true }
    }),
    'neutral',
    'a run the user stopped is gray'
  );
  assert.equal(
    cancel.getNotificationVariant({
      id: 'n1',
      type: 'game_detection',
      status: 'cancelled',
      details: {}
    }),
    'neutral',
    'the status alone is enough, even with the details flag erased'
  );
  // The red the user was seeing came from a second card, not from the canceled one: the failure
  // branch raises a generic toast carrying an error type, which is the only thing here drawn red.
  assert.equal(
    cancel.getNotificationVariant({
      id: 'generic_x',
      type: 'generic',
      status: 'completed',
      details: { notificationType: 'error' }
    }),
    'error',
    'a genuine failure still reads red'
  );
  assert.equal(
    cancel.getNotificationVariant({
      id: 'scan',
      type: 'eviction_scan',
      status: 'completed',
      details: { notificationType: 'warning' }
    }),
    'warning',
    'a run that succeeded with a warning reads amber'
  );
  assert.equal(
    cancel.getNotificationVariant({ id: 'n1', type: 'game_detection', status: 'cancelling' }),
    'info',
    'a status with no row of its own reads as running'
  );
});

test('a cancel for an operation that is already gone drops the card', async () => {
  const { cancel, api, ApiError, toasts } = await loadCancel('gone');
  const notifications = [runningCard()];

  api.setCancel(() =>
    Promise.reject(
      new ApiError({ status: 404, kind: 'http', body: null, message: 'Operation not found' })
    )
  );
  const removed = await driveCancel(cancel, notifications, notifications[0]);

  assert.deepEqual(removed, ['n1']);
  assert.deepEqual(toasts, []);
});
