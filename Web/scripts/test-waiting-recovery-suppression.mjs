import assert from 'node:assert/strict';
import test from 'node:test';
import { MemoryStorage, compileToUrl } from './transpile-module.mjs';

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
  // handlers.ts persists cards through the storage wrapper, which probes availability once when
  // its module is first imported, so the backing store has to exist before that import.
  globalThis.localStorage = new MemoryStorage();
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const storageUrl = await compileToUrl('../src/utils/storage.ts');
  const handlersUrl = await compileToUrl('../src/contexts/notifications/handlers.ts', {
    './constants': constantsUrl,
    './notificationStatus': statusUrl,
    '@utils/storage': storageUrl,
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
    '@utils/storage': storageUrl,
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

/** Two queued game removals at once: one started by a batch, one started on its own. */
const stubFetchTwoWaiters = async (url) =>
  url === '/api/operations/waiting'
    ? {
        ok: true,
        json: async () => [
          {
            operationId: 'batch-item',
            operationType: 'gameRemoval',
            name: 'Game Removal (Arma 3)',
            blockedByName: 'Cache File Scan'
          },
          {
            operationId: 'standalone',
            operationType: 'gameRemoval',
            name: 'Game Removal (Dota 2)',
            blockedByName: 'Depot Mapping'
          }
        ]
      }
    : { ok: false, json: async () => ({}) };

/** An empty queue: every waiting operation is gone by the time recovery runs. */
const stubFetchEmptyQueue = async (url) =>
  url === '/api/operations/waiting'
    ? { ok: true, json: async () => [] }
    : { ok: false, json: async () => ({}) };

/** Runs recovery against a starting card list and returns the resulting list. */
const runRecovery = async (startingCards, fetchWaiting) => {
  const { createRecoveryRunner } = await loadRecovery();
  let state = startingCards;
  const setNotifications = (update) => {
    state = typeof update === 'function' ? update(state) : update;
  };
  await createRecoveryRunner(fetchWaiting, setNotifications, () => undefined)();
  return state;
};

test('recovery opens a waiting card when no batch owns that item type', async () => {
  const state = await runRecovery([], stubFetch);

  const card = state.find((n) => n.id === 'game_removal');
  assert.ok(card, 'a queued operation with no batch running should get its own card back');
  assert.equal(card.status, 'waiting');
  assert.equal(card.details.operationId, 'queued-op-1');
});

test('recovery does not add a second card when a batch already owns that item type', async () => {
  const state = await runRecovery(
    [
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'running',
        message: 'Removing 1 of 2 - Arma 3',
        startedAt: new Date(),
        // The batch's item request is still on the wire, which is when a queue row of that type
        // can be assumed to be its own.
        details: { itemTypes: ['service_removal', 'game_removal'], itemRequestPending: true }
      }
    ],
    stubFetch
  );

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
  // Recovery drops waiting cards whose operation is gone. A batch card is client-owned and never
  // appears in those rows, so filtering on the rows alone would delete a live batch's card the
  // moment it went purple.
  const state = await runRecovery(
    [
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'waiting',
        message: 'waiting for something',
        startedAt: new Date(),
        details: { itemTypes: ['game_removal'] }
      }
    ],
    stubFetchEmptyQueue
  );

  assert.ok(
    state.some((n) => n.id === 'bulk_removal_x'),
    'a batch card must survive recovery even while it is showing a waiting state'
  );
});

/**
 * One card slot per type means two queued operations of one type cannot both have a card, but they
 * must not be confused for each other either: keying the rows by type alone let the last row win,
 * so a batch card ended up naming the blocker of an operation it never started while its own item
 * went unreported.
 */
test('recovery gives each queued operation to whoever started it when two of one type are parked', async () => {
  const state = await runRecovery(
    [
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'running',
        message: 'Removing 1 of 2 - Arma 3',
        startedAt: new Date(),
        details: { itemTypes: ['game_removal'], currentOperationId: 'batch-item' }
      }
    ],
    stubFetchTwoWaiters
  );

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(bulk.status, 'waiting');
  assert.match(
    bulk.message,
    /Cache File Scan/,
    "the batch names the blocker of its OWN item, not the other waiter's"
  );

  const standalone = state.find((n) => n.id === 'game_removal');
  assert.ok(standalone, 'the operation the batch did not start needs its own card');
  assert.equal(standalone.details.operationId, 'standalone');
});

/**
 * A batch whose item request is still on the wire has no operation id to compare against, so it may
 * claim a row of its type. It may claim only ONE: two rows are two operations and the batch started
 * at most one of them. Letting both land on the batch card left the second row's blocker overwriting
 * the first row's wording and neither operation with a card the X can reach.
 */
test('a batch with a request on the wire claims one queued row, not both', async () => {
  const state = await runRecovery(
    [
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'running',
        message: 'Removing 1 of 2 - Arma 3',
        startedAt: new Date(),
        details: { itemTypes: ['game_removal'], itemRequestPending: true }
      }
    ],
    stubFetchTwoWaiters
  );

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.match(
    bulk.message,
    /Cache File Scan/,
    'the batch keeps the first row it claimed instead of being overwritten by the second'
  );

  const standalone = state.find((n) => n.id === 'game_removal');
  assert.ok(standalone, 'the row the batch could not claim still needs a card of its own');
  assert.equal(standalone.details.operationId, 'standalone');
});
