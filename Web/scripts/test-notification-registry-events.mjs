import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import {
  MemoryStorage,
  bindLifted,
  compileToUrl,
  findSoleNode,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * The notification registry is the only place a SignalR lifecycle event reaches a card, and an
 * event nobody subscribes fails silently: no error, no exception, no failing render, just a card
 * that never appears. So these tests read the real NOTIFICATION_REGISTRY and drive the real
 * handler factories over it.
 *
 * Most entries do NOT name their events as literals - the builders derive the triple from an
 * `eventPrefix` - so the walk below expands the prefix rather than searching the source text. A
 * grep for `DatabaseResetStarted` finds nothing in the registry and proves nothing either way.
 */

const REGISTRY_PATH = 'src/contexts/notifications/notificationRegistry.ts';
const HANDLERS_PATH = 'src/contexts/notifications/useNotificationHandlers.ts';

/**
 * The events whose subscription moved when the hand-built handlers were folded into the registry.
 * Each must be subscribed exactly once: missing means a silent card, twice means two cards.
 */
const CLOSED_EVENT_LIST = [
  'DatabaseResetStarted',
  'DatabaseResetProgress',
  'DatabaseResetComplete',
  'EpicGameMappingsUpdated',
  'XboxGameMappingsUpdated',
  'SteamSessionError'
];

const I18N_STUB = moduleUrl(`export default { t: (key) => key };`);
const i18nStub = { t: (key) => key };

// ── Reading the real registry ───────────────────────────────────────────────

const registryArray = (sourceFile) =>
  findSoleNode(
    sourceFile,
    'NOTIFICATION_REGISTRY declaration',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'NOTIFICATION_REGISTRY' &&
      node.initializer !== undefined &&
      ts.isArrayLiteralExpression(node.initializer)
  ).initializer;

/** The object literal an entry is written as, whether it is a bare literal or a builder call. */
const entryObject = (element) => (ts.isCallExpression(element) ? element.arguments[0] : element);

const propertyOf = (node, name, sourceFile) =>
  node && ts.isObjectLiteralExpression(node)
    ? node.properties.find(
        (property) =>
          ts.isPropertyAssignment(property) && property.name.getText(sourceFile) === name
      )
    : undefined;

const stringOf = (node, name, sourceFile) => {
  const property = propertyOf(node, name, sourceFile);
  return property && ts.isStringLiteral(property.initializer)
    ? property.initializer.text
    : undefined;
};

/** Every SignalR event name an entry subscribes, with `eventPrefix` expanded into its triple. */
const declaredEvents = (element, sourceFile) => {
  const object = entryObject(element);
  const events = propertyOf(object, 'events', sourceFile);
  if (events) {
    return ['started', 'progress', 'complete']
      .map((phase) => stringOf(events.initializer, phase, sourceFile))
      .filter((name) => name !== undefined);
  }

  // No events block and no prefix at all: a metadata-only entry, which subscribes nothing by design.
  if (!propertyOf(object, 'eventPrefix', sourceFile)) return [];

  const prefix = stringOf(object, 'eventPrefix', sourceFile);
  // A prefix written as anything but a literal - a const reference, a template - would be read as
  // zero events, and zero events is exactly what a lost subscription looks like. Stop instead.
  assert.ok(
    prefix,
    `eventPrefix is not a string literal in the ${stringOf(object, 'type', sourceFile)} entry`
  );
  return [
    `${prefix}Started`,
    `${prefix}Progress`,
    stringOf(object, 'completeEvent', sourceFile) ?? `${prefix}Complete`
  ];
};

/**
 * The registry's own entry for `type`, evaluated with its free variables supplied by name. A
 * missing or renamed name throws a ReferenceError, which is the only warning these scripts get.
 */
const liftRegistryEntry = (type, bindings) => {
  const sourceFile = parseSource(REGISTRY_PATH);
  const element = registryArray(sourceFile).elements.find(
    (candidate) => stringOf(entryObject(candidate), 'type', sourceFile) === type
  );
  assert.ok(element, `${type} is not in NOTIFICATION_REGISTRY`);
  return bindLifted(`() => (${element.getText(sourceFile)})`, bindings)();
};

// ── Loading the real handlers and the real wiring ───────────────────────────

const loadHandlers = async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const handlersUrl = await compileToUrl('../src/contexts/notifications/handlers.ts', {
    './constants': constantsUrl,
    './notificationStatus': statusUrl,
    '@/i18n': I18N_STUB
  });
  return await import(handlersUrl);
};

const loadRegistryEntries = async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const stageKeyUrl = await compileToUrl('../src/utils/stageKeyMessage.ts', {
    '@/i18n': I18N_STUB
  });
  const entriesUrl = await compileToUrl('../src/contexts/notifications/registryEntries.ts', {
    './constants': constantsUrl,
    '@utils/stageKeyMessage': stageKeyUrl,
    '@/i18n': I18N_STUB
  });
  return await import(entriesUrl);
};

const loadConstants = async () =>
  await import(await compileToUrl('../src/contexts/notifications/constants.ts'));

/**
 * One of the per-phase handler builders in useNotificationHandlers.ts. They are module-local, so
 * the test lifts the shipped function rather than repeating how a registry entry is wired up.
 */
const liftHandlerBuilder = (name, bindings) => {
  const sourceFile = parseSource(HANDLERS_PATH);
  const declaration = findSoleNode(
    sourceFile,
    `${name} declaration`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
  );
  return bindLifted(`(${declaration.getText(sourceFile)})`, bindings);
};

/**
 * The subscribe loop itself, ready to call. It is the only place a declared event becomes a live
 * subscription, and it has no name of its own - it is the body of the hook's single effect - so a
 * test that lifts the phase builders instead still proves nothing about what is subscribed.
 */
const liftSubscribeEffect = (bindings) => {
  const sourceFile = parseSource(HANDLERS_PATH);
  const effect = findSoleNode(
    sourceFile,
    'useEffect call',
    (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'useEffect'
  );
  return bindLifted(effect.arguments[0].getText(sourceFile), bindings);
};

/** A card list plus the setState and dismiss hooks the handlers are called with. */
const newCardList = () => {
  const cards = { state: [], dismissals: [] };
  cards.setNotifications = (updater) => {
    cards.state = updater(cards.state);
  };
  cards.scheduleAutoDismiss = (id, delayMs) => cards.dismissals.push([id, delayMs]);
  cards.cancelAutoDismissTimer = () => undefined;
  cards.removeNotification = () => undefined;
  return cards;
};

// ── The four entries that own the closed list ───────────────────────────────

/** The only closed-list entry with all three lifecycle phases. */
const liftDatabaseResetEntry = async () => {
  const registryEntries = await loadRegistryEntries();
  const constants = await loadConstants();
  return liftRegistryEntry('database_reset', {
    buildStandardOperationEntry: registryEntries.buildStandardOperationEntry,
    stageKeyMessage: registryEntries.stageKeyMessage,
    cappedProgress: registryEntries.cappedProgress,
    operationIdDetails: registryEntries.operationIdDetails,
    NOTIFICATION_IDS: constants.NOTIFICATION_IDS,
    NOTIFICATION_STORAGE_KEYS: constants.NOTIFICATION_STORAGE_KEYS,
    GENERIC_FAILURE_I18N_KEY: constants.GENERIC_FAILURE_I18N_KEY,
    CANCEL_TOOLTIP: { databaseReset: 'common.notifications.cancel.databaseReset' },
    translateRecoveryStage: (stageKey, context, fallbackKey) => stageKey ?? fallbackKey,
    formatDatabaseResetProgressMessage: () => undefined,
    formatDatabaseResetCompleteMessage: () => undefined,
    i18n: i18nStub
  });
};

/** The three announcement entries, whose single event is already terminal. */
const liftEpicCatalogEntry = async () => {
  const { NOTIFICATION_IDS } = await loadConstants();
  return liftRegistryEntry('epic_catalog_update', {
    NOTIFICATION_IDS,
    i18n: i18nStub,
    formatEpicGameMappingsUpdatedMessage: (event) => `total:${event.totalGames}`
  });
};

const liftXboxCatalogEntry = async () => {
  const { NOTIFICATION_IDS } = await loadConstants();
  return liftRegistryEntry('xbox_catalog_update', {
    NOTIFICATION_IDS,
    i18n: i18nStub,
    formatXboxGameMappingsUpdatedMessage: (event) => `new:${event.newMappings ?? 0}`
  });
};

const liftSteamSessionErrorEntry = async () => {
  const { NOTIFICATION_IDS, STEAM_ERROR_DISMISS_DELAY_MS } = await loadConstants();
  return liftRegistryEntry('steam_session_error', {
    NOTIFICATION_IDS,
    STEAM_ERROR_DISMISS_DELAY_MS,
    i18n: i18nStub
  });
};

// ── The closed list ─────────────────────────────────────────────────────────

test('no two registry entries claim the same notification type', () => {
  const sourceFile = parseSource(REGISTRY_PATH);
  const types = registryArray(sourceFile).elements.map((element) =>
    stringOf(entryObject(element), 'type', sourceFile)
  );

  assert.ok(
    types.every((type) => typeof type === 'string'),
    'a registry entry declares no type'
  );
  const duplicated = types.filter((type, index) => types.indexOf(type) !== index);
  assert.deepEqual(duplicated, [], `types claimed twice: ${duplicated.join(', ')}`);
});

test('each event in the closed list is subscribed exactly once', () => {
  const sourceFile = parseSource(REGISTRY_PATH);
  const counts = new Map();
  for (const element of registryArray(sourceFile).elements) {
    for (const event of declaredEvents(element, sourceFile)) {
      counts.set(event, (counts.get(event) ?? 0) + 1);
    }
  }

  const duplicated = [...counts]
    .filter(([, count]) => count > 1)
    .map(([event, count]) => `${event} (${count})`);
  assert.deepEqual(duplicated, [], `subscribed more than once: ${duplicated.join(', ')}`);

  for (const event of CLOSED_EVENT_LIST) {
    assert.equal(
      counts.get(event) ?? 0,
      1,
      `${event} is subscribed ${counts.get(event) ?? 0} times`
    );
  }
});

test('the loop subscribes exactly the events its entries declare', async () => {
  globalThis.localStorage = new MemoryStorage();
  const { createStartedHandler, createStatusAwareProgressHandler, createCompletionHandler } =
    await loadHandlers();
  const registry = [
    await liftDatabaseResetEntry(),
    await liftEpicCatalogEntry(),
    await liftXboxCatalogEntry(),
    await liftSteamSessionErrorEntry()
  ];

  const subscribed = [];
  const cards = newCardList();
  liftSubscribeEffect({
    signalR: { on: (eventName) => subscribed.push(eventName) },
    registry,
    setNotifications: cards.setNotifications,
    scheduleAutoDismiss: cards.scheduleAutoDismiss,
    cancelAutoDismissTimer: cards.cancelAutoDismissTimer,
    removeNotification: cards.removeNotification,
    buildStartedHandler: liftHandlerBuilder('buildStartedHandler', { createStartedHandler }),
    buildProgressHandler: liftHandlerBuilder('buildProgressHandler', {
      createStatusAwareProgressHandler
    }),
    buildCompleteHandler: liftHandlerBuilder('buildCompleteHandler', { createCompletionHandler })
  })();

  // The wait-queue pair is subscribed once per mount rather than per entry, so it belongs in the
  // expected set: an exact comparison is what catches a phase that quietly stopped subscribing.
  assert.deepEqual(
    [...subscribed].sort(),
    [...CLOSED_EVENT_LIST, 'OperationWaiting', 'OperationWaitingComplete'].sort()
  );
});

test('the Steam auth refetch keeps its own SteamSessionError subscription', () => {
  const source = readFileSync(
    new URL('../src/contexts/SteamAuthContext.tsx', import.meta.url),
    'utf8'
  );
  assert.match(source, /signalR\.on\('SteamSessionError', handleSteamSessionError\)/);
  assert.match(source, /signalR\.off\('SteamSessionError', handleSteamSessionError\)/);
});

// ── The completion-only cards ───────────────────────────────────────────────

test('a Steam session error raises a typed card, not a generic toast', async () => {
  globalThis.localStorage = new MemoryStorage();
  const { createCompletionHandler } = await loadHandlers();
  const { NOTIFICATION_IDS, STEAM_ERROR_DISMISS_DELAY_MS } = await loadConstants();
  const buildCompleteHandler = liftHandlerBuilder('buildCompleteHandler', {
    createCompletionHandler
  });

  const cards = newCardList();
  const handleError = buildCompleteHandler(
    await liftSteamSessionErrorEntry(),
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.removeNotification
  );

  handleError({
    errorType: 'RateLimited',
    titleStageKey: 'signalr.steamSession.errorTitle.rateLimited',
    stageKey: 'signalr.steamSession.rateLimited'
  });

  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].type, 'steam_session_error');
  assert.equal(cards.state[0].status, 'failed');
  assert.equal(cards.state[0].id, NOTIFICATION_IDS.STEAM_SESSION_ERROR);
  assert.equal(cards.state[0].message, 'signalr.steamSession.errorTitle.rateLimited');
  assert.equal(cards.state[0].detailMessage, 'signalr.steamSession.rateLimited');
  assert.deepEqual(cards.dismissals, [
    [NOTIFICATION_IDS.STEAM_SESSION_ERROR, STEAM_ERROR_DISMISS_DELAY_MS]
  ]);

  handleError({
    errorType: 'AutoLogout',
    titleStageKey: 'signalr.steamSession.errorTitle.autoLogout',
    stageKey: 'signalr.steamSession.autoLogout'
  });

  assert.equal(cards.state.length, 1, 'a newer error replaces the card instead of stacking');
  assert.equal(cards.state[0].message, 'signalr.steamSession.errorTitle.autoLogout');
});

test('a Steam error with no title key of its own still gets a title', async () => {
  globalThis.localStorage = new MemoryStorage();
  const { createCompletionHandler } = await loadHandlers();
  const buildCompleteHandler = liftHandlerBuilder('buildCompleteHandler', {
    createCompletionHandler
  });

  const cards = newCardList();
  const handleError = buildCompleteHandler(
    await liftSteamSessionErrorEntry(),
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.removeNotification
  );

  handleError({ errorType: 'AnErrorTypeNobodyHasAddedYet' });
  assert.equal(cards.state[0].message, 'signalr.steamSession.errorTitle.generic');
});

test('an Xbox catalog update carrying no counts raises no card', async () => {
  globalThis.localStorage = new MemoryStorage();
  const { createCompletionHandler } = await loadHandlers();
  const buildCompleteHandler = liftHandlerBuilder('buildCompleteHandler', {
    createCompletionHandler
  });

  const cards = newCardList();
  const handleUpdate = buildCompleteHandler(
    await liftXboxCatalogEntry(),
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.removeNotification
  );

  // The gate reads the two counts and nothing else, so this is what a download resolution looks
  // like to it: the source and the resolved count are along for the ride.
  handleUpdate({ source: 'xbox-download-resolution', resolvedCount: 3 });
  assert.deepEqual(cards.state, [], 'an update with neither count must not raise a card');
  assert.deepEqual(cards.dismissals, []);

  handleUpdate({ source: 'xbox-mapping', newMappings: 2, newPatterns: 0 });
  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].type, 'xbox_catalog_update');
  assert.equal(cards.state[0].status, 'completed');
  assert.equal(cards.state[0].message, 'notifications.xboxGameMappingsUpdated.title');
});

test('an Epic catalog merge that changed nothing raises no card', async () => {
  globalThis.localStorage = new MemoryStorage();
  const { createCompletionHandler } = await loadHandlers();
  const buildCompleteHandler = liftHandlerBuilder('buildCompleteHandler', {
    createCompletionHandler
  });

  const cards = newCardList();
  const handleUpdate = buildCompleteHandler(
    await liftEpicCatalogEntry(),
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.removeNotification
  );

  handleUpdate({ totalGames: 900, newGames: 0, updatedGames: 0 });
  assert.deepEqual(cards.state, []);

  handleUpdate({ totalGames: 900, newGames: 4, updatedGames: 1 });
  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].type, 'epic_catalog_update');
  assert.equal(cards.state[0].status, 'completed');
  assert.equal(cards.state[0].message, 'notifications.epicGameMappingsUpdated.title');
  assert.equal(cards.state[0].detailMessage, 'total:900');
});

// ── The database reset lifecycle ────────────────────────────────────────────

test('database reset reports progress and its terminal event never completes the card twice', async () => {
  globalThis.localStorage = new MemoryStorage();
  const { createStartedHandler, createStatusAwareProgressHandler, createCompletionHandler } =
    await loadHandlers();
  const entry = await liftDatabaseResetEntry();

  assert.deepEqual(entry.events, {
    started: 'DatabaseResetStarted',
    progress: 'DatabaseResetProgress',
    complete: 'DatabaseResetComplete'
  });

  const cards = newCardList();
  const handleStarted = liftHandlerBuilder('buildStartedHandler', { createStartedHandler })(
    entry,
    entry.started,
    cards.setNotifications,
    cards.cancelAutoDismissTimer
  );
  const handleProgress = liftHandlerBuilder('buildProgressHandler', {
    createStatusAwareProgressHandler
  })(
    entry,
    entry.progress,
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.cancelAutoDismissTimer
  );
  const handleComplete = liftHandlerBuilder('buildCompleteHandler', { createCompletionHandler })(
    entry,
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.removeNotification
  );

  handleStarted({ operationId: 'reset-1' });
  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].status, 'running');

  handleProgress({ operationId: 'reset-1', status: 'running', percentComplete: 40 });
  assert.equal(cards.state[0].status, 'running');
  assert.equal(cards.state[0].progress, 40);

  handleProgress({ operationId: 'reset-1', status: 'completed', percentComplete: 100 });
  assert.equal(cards.state[0].status, 'completed');
  assert.equal(cards.dismissals.length, 1);

  handleComplete({ operationId: 'reset-1', success: true });
  assert.equal(cards.state.length, 1, 'the terminal event must not add a second card');
  assert.equal(cards.state[0].status, 'completed');
  assert.equal(cards.dismissals.length, 1, 'the terminal event must not re-arm the dismiss timer');
});
