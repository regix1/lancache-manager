import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * Recovery rebuilds purple waiting cards from `GET /api/operations/waiting`, and it runs far more
 * often than a page load: every SignalR reconnect and every time the tab becomes visible again.
 *
 * That path creates cards independently of the live SignalR handler, so it needs the same rule
 * about a batch owning its per-item cards. It did not have it. The live handler was fixed first
 * and the duplicate came straight back through here on the next reconnect, which is exactly the
 * bug this file exists to stop happening a third time.
 *
 * Drives the real exported `createRecoveryRunner` with a stub fetch, so the code under test is the
 * code that ships.
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

// Echoes the interpolation values as well as the key, so a test can prove the blocker's name
// actually reached the message rather than only that some message was produced.
const I18N_STUB = moduleUrl(
  `export default { t: (key, vars) => key + ' ' + JSON.stringify(vars ?? {}) };`
);

/**
 * Only the wait-queue recovery is under test. A registry of one entry whose recovery kind is
 * 'none' means `createRecoveryRunner` builds nothing else, so the run is that one function.
 */
const REGISTRY_STUB = moduleUrl(`
  export const NOTIFICATION_REGISTRY = [
    {
      type: 'game_removal',
      id: 'game_removal',
      storageKey: 'test-game-removal',
      recovery: { kind: 'none' }
    }
  ];
`);

const loadRecovery = async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const handlersUrl = await compileToUrl('../src/contexts/notifications/handlers.ts', {
    './constants': constantsUrl,
    './notificationStatus': statusUrl,
    '@/i18n': I18N_STUB
  });
  const removalKindUrl = await compileToUrl('../src/contexts/notifications/removalKind.ts', {
    '@/i18n': I18N_STUB
  });
  const recoveryUrl = await compileToUrl('../src/contexts/notifications/recovery.ts', {
    './constants': constantsUrl,
    './handlers': handlersUrl,
    './notificationRegistry': REGISTRY_STUB,
    './removalKind': removalKindUrl,
    '@/i18n': I18N_STUB
  });
  return await import(recoveryUrl);
};

/** One queued game removal on the wire; every other recovery endpoint answers not-ok. */
const stubFetch = async (url) =>
  url === '/api/operations/waiting'
    ? {
        ok: true,
        json: async () => [
          {
            operationId: 'queued-op-1',
            operationType: 'gameRemoval',
            name: 'Game Removal (Arma 3)',
            blockedByName: 'Cache File Scan'
          }
        ]
      }
    : { ok: false, json: async () => ({}) };

/** Runs recovery against a starting card list and returns the resulting list. */
const runRecovery = async (startingCards) => {
  const { createRecoveryRunner } = await loadRecovery();
  let state = startingCards;
  const setNotifications = (update) => {
    state = typeof update === 'function' ? update(state) : update;
  };
  await createRecoveryRunner(stubFetch, setNotifications, () => undefined)();
  return state;
};

test('recovery opens a waiting card when no batch owns that item type', async () => {
  const state = await runRecovery([]);

  const card = state.find((n) => n.id === 'game_removal');
  assert.ok(card, 'a queued operation with no batch running should get its own card back');
  assert.equal(card.status, 'waiting');
  assert.equal(card.details.operationId, 'queued-op-1');
});

test('recovery does not add a second card when a batch already owns that item type', async () => {
  const state = await runRecovery([
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['service_removal', 'game_removal'] }
    }
  ]);

  assert.ok(
    !state.some((n) => n.id === 'game_removal'),
    'the batch card already reports this item, so recovery must not open a second card for it'
  );

  // The batch card is where the queue's wording goes instead - losing it would leave the batch
  // sitting at its item line with no sign that the item is parked behind another operation.
  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(bulk.status, 'waiting');
  assert.match(bulk.message, /Cache File Scan/);
});

test('recovery leaves a running batch card alone when nothing is queued', async () => {
  const { createRecoveryRunner } = await loadRecovery();
  let state = [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'waiting',
      message: 'waiting for something',
      startedAt: new Date(),
      details: { itemTypes: ['game_removal'] }
    }
  ];
  const setNotifications = (update) => {
    state = typeof update === 'function' ? update(state) : update;
  };

  // An empty queue: recovery drops waiting cards whose operation is gone. A batch card is
  // client-owned and never appears in those rows, so filtering on the rows alone would delete
  // a live batch's card the moment it went purple.
  const emptyQueue = async (url) =>
    url === '/api/operations/waiting'
      ? { ok: true, json: async () => [] }
      : { ok: false, json: async () => ({}) };

  await createRecoveryRunner(emptyQueue, setNotifications, () => undefined)();

  assert.ok(
    state.some((n) => n.id === 'bulk_removal_x'),
    'a batch card must survive recovery even while it is showing a waiting state'
  );
});
