import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test, { mock } from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  bulkRemovalCard,
  compileToUrl,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  moduleUrl,
  loadNotificationModules,
  operationRunRow,
  parseSource,
  pushRun
} from './transpile-module.mjs';

/**
 * The notification registry is the only place a SignalR lifecycle event reaches a card's text, and
 * an event nobody subscribes fails silently: no error, no exception, no failing render, just a card
 * that never says anything. So these tests read the real NOTIFICATION_REGISTRY and drive the real
 * handler builders over it.
 *
 * Most entries do NOT name their events as literals - the builders derive the triple from an
 * `eventPrefix` - so the walk below expands the prefix rather than searching the source text. A
 * grep for `DatabaseResetStarted` finds nothing in the registry and proves nothing either way.
 */

const REGISTRY_PATH = 'src/contexts/notifications/notificationRegistry.ts';
const HANDLERS_PATH = 'src/contexts/notifications/useNotificationHandlers.ts';
const CONTEXT_PATH = 'src/contexts/notifications/NotificationsContext.tsx';

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

const I18N_STUB = moduleUrl(`export default { t: (key) => key, exists: () => false };`);
const i18nStub = { t: (key) => key };
const modules = await loadNotificationModules(I18N_STUB);
const {
  AUTO_DISMISS_DELAY_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  buildAnnouncementHandler,
  buildCompleteHandler,
  buildProgressHandler,
  buildStartedHandler,
  isTerminalNotificationStatus
} = modules;

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

/**
 * The subscribe loop itself, ready to call. It is the only place a declared event becomes a live
 * subscription, and it has no name of its own - it is the body of the hook's single effect - so a
 * test that drives the phase builders instead still proves nothing about what is subscribed.
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

/** Records every detail patch a handler dispatches, built against `existing`. */
const recordPatches = (existing) => {
  const patches = [];
  const dispatchDetail = (operationId, build, source) =>
    patches.push({ operationId, source, patch: build(existing) });
  return { patches, dispatchDetail };
};

// ── The four entries that own the closed list ───────────────────────────────

/** The only closed-list entry with all three lifecycle phases. */
const liftDatabaseResetEntry = async () => {
  const registryEntries = await loadRegistryEntries();
  return liftRegistryEntry('database_reset', {
    buildStandardOperationEntry: registryEntries.buildStandardOperationEntry,
    stageKeyMessage: registryEntries.stageKeyMessage,
    cappedProgress: registryEntries.cappedProgress,
    operationIdDetails: registryEntries.operationIdDetails,
    GENERIC_FAILURE_I18N_KEY: modules.GENERIC_FAILURE_I18N_KEY,
    CANCEL_TOOLTIP: { databaseReset: 'common.notifications.cancel.databaseReset' },
    translateRecoveryStage: (stageKey, context, fallbackKey) => stageKey ?? fallbackKey,
    formatDatabaseResetProgressMessage: (event) => `resetting:${event.percentComplete}`,
    formatDatabaseResetCompleteMessage: () => 'reset complete',
    i18n: i18nStub
  });
};

/** The three announcement entries, whose single event is already terminal. */
const liftEpicCatalogEntry = () =>
  liftRegistryEntry('epic_catalog_update', {
    i18n: i18nStub,
    formatEpicGameMappingsUpdatedMessage: (event) => `total:${event.totalGames}`
  });

const liftXboxCatalogEntry = () =>
  liftRegistryEntry('xbox_catalog_update', {
    i18n: i18nStub,
    formatXboxGameMappingsUpdatedMessage: (event) => `new:${event.newMappings ?? 0}`
  });

const liftSteamSessionErrorEntry = () =>
  liftRegistryEntry('steam_session_error', { i18n: i18nStub });

// ── The browser's own cards, lifted from the provider ───────────────────────

const APP_EVENTS = {
  NOTIFICATION_REMOVING: 'removing',
  NOTIFICATION_VISIBILITY_CHANGE: 'visibility-change',
  SHOW_TOAST: 'show-toast'
};

/**
 * The provider's own-card functions bound to one set of refs: the announcement path, the toast
 * bridge, the popup timer and the exit fade, as they ship.
 */
const liftLocalCards = ({ keepVisible = false } = {}) => {
  const localRef = { current: [] };
  const removing = [];
  const listeners = new Map();
  const window = {
    dispatchEvent: (event) => removing.push(event.detail.notificationId),
    addEventListener: (name, handler) => listeners.set(name, handler),
    removeEventListener: () => undefined
  };
  const commit = () => undefined;
  const shouldAutoDismiss = () => !keepVisible;
  const lift = (hook, marker, bindings) =>
    bindLifted(liftHookCallback(CONTEXT_PATH, hook, marker), bindings);
  const fadeLocal = lift('useCallback', 'fadingRef.current.add(id)', {
    localRef,
    fadingRef: { current: new Set() },
    window,
    APP_EVENTS,
    NOTIFICATION_ANIMATION_DURATION_MS,
    commit
  });
  const scheduleAutoDismiss = lift('useCallback', 'A bulk card ends by the ending rules', {
    shouldAutoDismiss,
    autoDismissTimersRef: { current: new Map() },
    localRef,
    isTerminalNotificationStatus,
    fadeLocal,
    AUTO_DISMISS_DELAY_MS
  });
  const showAnnouncement = lift('useCallback', 'One card per announcement type', {
    localRef,
    scheduleAutoDismiss,
    commit
  });
  const addNotification = lift('useCallback', "A generic card's id comes from its message", {
    localRef,
    isTerminalNotificationStatus,
    scheduleAutoDismiss,
    commit
  });
  lift('useEffect', 'handleShowToast', {
    addNotification,
    window,
    APP_EVENTS
  })();
  const updateNotification = lift('useCallback', "The server owns a run's state", {
    localRef,
    isTerminalNotificationStatus,
    shouldAutoDismiss,
    fadeLocal,
    scheduleAutoDismiss,
    settleBulk: () => undefined,
    commit
  });
  lift('useEffect', 'handleNotificationVisibilityChange', {
    shouldAutoDismiss,
    autoDismissTimersRef: { current: new Map() },
    storeRef: { current: modules.createRunStoreState() },
    releaseKeptSuccess: modules.releaseKeptSuccess,
    localRef,
    isTerminalNotificationStatus,
    fadeLocal,
    scheduleAutoDismiss,
    fadeLeavingRuns: () => undefined,
    commit,
    window,
    APP_EVENTS
  })();
  return {
    localRef,
    removing,
    showAnnouncement,
    showToast: (detail) => listeners.get(APP_EVENTS.SHOW_TOAST)({ detail }),
    turnKeepVisibleOff: () => {
      keepVisible = false;
      listeners.get(APP_EVENTS.NOTIFICATION_VISIBILITY_CHANGE)();
    },
    turnKeepVisibleOn: () => {
      keepVisible = true;
      listeners.get(APP_EVENTS.NOTIFICATION_VISIBILITY_CHANGE)();
    },
    updateNotification
  };
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

test('the loop subscribes exactly the events its entries declare, plus the run rows', async () => {
  const registry = [
    await liftDatabaseResetEntry(),
    liftEpicCatalogEntry(),
    liftXboxCatalogEntry(),
    liftSteamSessionErrorEntry()
  ];

  const subscribed = [];
  liftSubscribeEffect({
    on: (eventName) => subscribed.push(eventName),
    off: () => undefined,
    registry,
    dispatchDetail: () => undefined,
    showAnnouncement: () => undefined,
    handleRun: () => undefined,
    buildStartedHandler,
    buildProgressHandler,
    buildCompleteHandler,
    buildAnnouncementHandler
  })();

  // The run rows are subscribed once per mount rather than per entry, so they belong in the
  // expected set; the old wait-queue pair is no longer read at all.
  assert.deepEqual([...subscribed].sort(), [...CLOSED_EVENT_LIST, 'OperationUpdated'].sort());
});

test('the Steam auth refetch keeps its own SteamSessionError subscription', () => {
  const source = readFileSync(
    new URL('../src/contexts/SteamAuthContext.tsx', import.meta.url),
    'utf8'
  );
  assert.match(source, /signalR\.on\('SteamSessionError', handleSteamSessionError\)/);
  assert.match(source, /signalR\.off\('SteamSessionError', handleSteamSessionError\)/);
});

// ── The announcements and the toast bridge [18] ─────────────────────────────

test('a Steam session error is a red card that stays until closed, with no timer', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const cards = liftLocalCards();
    const entry = liftSteamSessionErrorEntry();
    const handleError = buildAnnouncementHandler(entry, entry.complete, cards.showAnnouncement);

    handleError({
      errorType: 'RateLimited',
      titleStageKey: 'signalr.steamSession.errorTitle.rateLimited',
      stageKey: 'signalr.steamSession.rateLimited'
    });
    assert.equal(cards.localRef.current.length, 1);
    const [card] = cards.localRef.current;
    assert.equal(card.type, 'steam_session_error');
    assert.equal(card.status, 'failed');
    assert.equal(card.message, 'signalr.steamSession.errorTitle.rateLimited');
    assert.equal(card.detailMessage, 'signalr.steamSession.rateLimited');

    mock.timers.tick(10 * 60 * 1000);
    assert.equal(cards.localRef.current.length, 1, 'no timer removes the Steam session error');
    assert.deepEqual(cards.removing, []);

    handleError({
      errorType: 'AutoLogout',
      titleStageKey: 'signalr.steamSession.errorTitle.autoLogout',
      stageKey: 'signalr.steamSession.autoLogout'
    });
    assert.equal(cards.localRef.current.length, 1, 'a newer error replaces the card');
    assert.equal(cards.localRef.current[0].message, 'signalr.steamSession.errorTitle.autoLogout');
  } finally {
    mock.timers.reset();
  }
});

test('a Steam error with no title key of its own still gets a title', () => {
  const cards = liftLocalCards();
  const entry = liftSteamSessionErrorEntry();
  buildAnnouncementHandler(
    entry,
    entry.complete,
    cards.showAnnouncement
  )({
    errorType: 'AnErrorTypeNobodyHasAddedYet'
  });
  assert.equal(cards.localRef.current[0].message, 'signalr.steamSession.errorTitle.generic');
});

test('an Xbox catalog update with counts leaves after the one popup time; one with none raises nothing', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const cards = liftLocalCards();
    const entry = liftXboxCatalogEntry();
    const handleUpdate = buildAnnouncementHandler(entry, entry.complete, cards.showAnnouncement);

    // The gate reads the two counts and nothing else, so this is what a download resolution looks
    // like to it: the source and the resolved count are along for the ride.
    handleUpdate({ source: 'xbox-download-resolution', resolvedCount: 3 });
    assert.deepEqual(
      cards.localRef.current,
      [],
      'an update with neither count must not raise a card'
    );

    handleUpdate({ source: 'xbox-mapping', newMappings: 2, newPatterns: 0 });
    assert.equal(cards.localRef.current.length, 1);
    const [card] = cards.localRef.current;
    assert.equal(card.type, 'xbox_catalog_update');
    assert.equal(card.status, 'completed');
    assert.equal(card.message, 'notifications.xboxGameMappingsUpdated.title');

    mock.timers.tick(AUTO_DISMISS_DELAY_MS - 1);
    assert.equal(cards.localRef.current.length, 1);
    mock.timers.tick(1);
    assert.deepEqual(cards.removing, ['xbox_catalog_update'], 'the exit fade starts');
    mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
    assert.deepEqual(cards.localRef.current, []);
  } finally {
    mock.timers.reset();
  }
});

test('an Epic catalog merge that changed nothing raises no card', () => {
  const cards = liftLocalCards();
  const entry = liftEpicCatalogEntry();
  const handleUpdate = buildAnnouncementHandler(entry, entry.complete, cards.showAnnouncement);

  handleUpdate({ totalGames: 900, newGames: 0, updatedGames: 0 });
  assert.deepEqual(cards.localRef.current, []);

  handleUpdate({ totalGames: 900, newGames: 4, updatedGames: 1 });
  assert.equal(cards.localRef.current.length, 1);
  assert.equal(cards.localRef.current[0].type, 'epic_catalog_update');
  assert.equal(cards.localRef.current[0].status, 'completed');
  assert.equal(cards.localRef.current[0].message, 'notifications.epicGameMappingsUpdated.title');
  assert.equal(cards.localRef.current[0].detailMessage, 'total:900');
});

test('a toast leaves after the one popup time, and stays while Keep Notifications Visible is on', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const cards = liftLocalCards();
    cards.showToast({ type: 'success', message: 'Settings saved' });
    assert.equal(cards.localRef.current.length, 1);
    assert.equal(cards.localRef.current[0].type, 'generic');
    mock.timers.tick(AUTO_DISMISS_DELAY_MS - 1);
    assert.equal(cards.localRef.current.length, 1);
    mock.timers.tick(1);
    assert.deepEqual(cards.removing, ['generic_Settings_saved']);
    mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
    assert.deepEqual(cards.localRef.current, []);

    const kept = liftLocalCards({ keepVisible: true });
    kept.showToast({ type: 'error', message: 'Save failed' });
    mock.timers.tick(10 * 60 * 1000);
    assert.equal(kept.localRef.current.length, 1);
  } finally {
    mock.timers.reset();
  }
});

test('with Keep Notifications Visible on no popup leaves by itself, and turning it off gives each the popup time', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    for (const keepVisible of [true, false]) {
      const cards = liftLocalCards({ keepVisible });
      cards.showToast({ type: 'success', message: 'Settings saved' });
      cards.showToast({ type: 'error', message: 'Failed to save theme' });
      cards.showAnnouncement({
        type: 'xbox_catalog_update',
        status: 'completed',
        message: 'notifications.xboxGameMappingsUpdated.title'
      });
      // Two ticks: the fade timer the hold starts does not fire inside the same tick.
      mock.timers.tick(AUTO_DISMISS_DELAY_MS);
      mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
      assert.equal(cards.localRef.current.length, keepVisible ? 3 : 0, `keep=${keepVisible}`);
      if (!keepVisible) continue;

      cards.turnKeepVisibleOff();
      mock.timers.tick(AUTO_DISMISS_DELAY_MS - 1);
      assert.equal(cards.localRef.current.length, 3, 'each held popup gets the full popup time');
      mock.timers.tick(1);
      mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
      assert.deepEqual(cards.localRef.current, []);
    }
  } finally {
    mock.timers.reset();
  }
});

test('an error toast is red and carries its reason; success and info toasts stay completed', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const cards = liftLocalCards();
    cards.showToast({
      type: 'error',
      message: 'Failed to close the notification',
      error: 'Server unreachable'
    });
    cards.showToast({ type: 'success', message: 'Settings saved' });
    cards.showToast({ type: 'info', message: 'Theme applied' });
    const card = (message) => cards.localRef.current.find((c) => c.message === message);
    assert.equal(card('Failed to close the notification').status, 'failed');
    assert.equal(card('Failed to close the notification').error, 'Server unreachable');
    assert.equal(card('Settings saved').status, 'completed');
    assert.equal(card('Settings saved').error, undefined);
    assert.equal(card('Theme applied').status, 'completed');
    assert.equal(card('Theme applied').error, undefined);
  } finally {
    mock.timers.reset();
  }
});

test('a popup whose popup time started stays when Keep Notifications Visible is turned on', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const cards = liftLocalCards();
    cards.showToast({ type: 'success', message: 'Settings saved' });
    mock.timers.tick(2000);
    cards.turnKeepVisibleOn();
    mock.timers.tick(AUTO_DISMISS_DELAY_MS);
    mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
    assert.equal(cards.localRef.current.length, 1);
    assert.deepEqual(cards.removing, []);
  } finally {
    mock.timers.reset();
  }
});

test('a canceled batch leaves on its own unless Keep Notifications Visible is on, and a failed batch stays', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    for (const [keepVisible, status, leaves] of [
      [false, 'cancelled', true],
      [true, 'cancelled', false],
      [false, 'failed', false]
    ]) {
      const cards = liftLocalCards({ keepVisible });
      cards.localRef.current = [bulkRemovalCard({})];
      cards.updateNotification('bulk', {
        status,
        details: status === 'cancelled' ? { cancelled: true } : {}
      });
      // Two ticks: the fade timer the hold starts does not fire inside the same tick.
      mock.timers.tick(10 * 60 * 1000);
      mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
      const label = `${status} keep=${keepVisible}`;
      assert.deepEqual(cards.removing, leaves ? ['bulk'] : [], label);
      assert.equal(cards.localRef.current.length, leaves ? 0 : 1, label);
    }
  } finally {
    mock.timers.reset();
  }
});

/** The shipped batch finalizer, cancelling the batch card in `cards` after one of three items failed. */
const cancelBatchWithFailure = (cards) =>
  bindLifted(
    liftConstArrow(
      'src/components/features/management/game-detection/cacheRemovalHelpers.ts',
      'finalizeBulkRemovalNotification'
    ),
    { FULL_PROGRESS_PERCENT: modules.FULL_PROGRESS_PERCENT }
  )({
    id: 'bulk',
    succeeded: 1,
    failed: 1,
    total: 3,
    cancelled: true,
    t: (key) => key,
    updateNotification: cards.updateNotification,
    text: {
      cancelledKey: 'cancelled',
      cancelledWithFailuresKey: 'cancelledWithFailures',
      partialFailureKey: 'partialFailure',
      completeKey: 'complete'
    }
  });

test('a canceled batch keeps an item failure that never reached the server as a red card that stays', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const cards = liftLocalCards();
    cards.localRef.current = [bulkRemovalCard({ itemOperationIds: [], failedWithoutRun: true })];
    cancelBatchWithFailure(cards);
    mock.timers.tick(10 * 60 * 1000);
    assert.deepEqual(cards.removing, [], 'no card with a failure only it holds leaves on its own');
    assert.deepEqual(
      cards.localRef.current.map((card) => `${card.status}:${card.message}`),
      ['failed:cancelledWithFailures']
    );
    assert.equal(cards.localRef.current[0].details.cancelled, false, 'drawn red, not gray');
  } finally {
    mock.timers.reset();
  }
});

test('a canceled batch leaves, and an item failure the server kept shows as its own red card', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const cards = liftLocalCards();
    cards.localRef.current = [bulkRemovalCard({ itemOperationIds: ['I1'] })];
    const store = pushRun(
      modules,
      modules.createRunStoreState(),
      operationRunRow('I1', {
        operationType: 'gameRemoval',
        name: 'Game Removal',
        status: 'failed',
        retained: true
      }),
      cards.localRef.current
    );
    const drawn = () =>
      modules
        .deriveNotifications(store, cards.localRef.current)
        .map((card) => `${card.id}:${card.status}`);
    assert.deepEqual(drawn(), ['bulk:running'], 'the kept item is folded into its batch card');

    cancelBatchWithFailure(cards);
    mock.timers.tick(10 * 60 * 1000);
    mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
    assert.deepEqual(cards.removing, ['bulk']);
    assert.deepEqual(drawn(), ['I1:failed']);
  } finally {
    mock.timers.reset();
  }
});

test('a finished or canceled batch stays for the popup time, also when Keep Notifications Visible is turned off', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    for (const status of ['completed', 'cancelled']) {
      for (const path of ['ends', 'setting turned off']) {
        const label = `${status}, ${path}`;
        const cards = liftLocalCards();
        cards.localRef.current = [bulkRemovalCard({}, path === 'ends' ? {} : { status })];
        if (path === 'ends') cards.updateNotification('bulk', { status });
        else cards.turnKeepVisibleOff();
        mock.timers.tick(AUTO_DISMISS_DELAY_MS - 1);
        assert.deepEqual(cards.removing, [], `${label}: still shown`);
        mock.timers.tick(1);
        assert.deepEqual(cards.removing, ['bulk'], `${label}: the fade starts`);
        mock.timers.tick(NOTIFICATION_ANIMATION_DURATION_MS);
        assert.equal(cards.localRef.current.length, 0, label);
      }
    }
  } finally {
    mock.timers.reset();
  }
});

// ── The database reset lifecycle ────────────────────────────────────────────

test('database reset events fill in the text of the card its run row opens', async () => {
  const entry = await liftDatabaseResetEntry();
  assert.deepEqual(entry.events, {
    started: 'DatabaseResetStarted',
    progress: 'DatabaseResetProgress',
    complete: 'DatabaseResetComplete'
  });

  const { patches, dispatchDetail } = recordPatches(undefined);
  buildStartedHandler(entry.started, dispatchDetail)({ operationId: 'reset-1' });
  const handleProgress = buildProgressHandler(entry, entry.progress, dispatchDetail);
  handleProgress({ operationId: 'reset-1', status: 'running', percentComplete: 40 });
  handleProgress({ operationId: 'reset-1', status: 'completed', percentComplete: 100 });
  buildCompleteHandler(
    entry,
    entry.complete,
    dispatchDetail
  )({
    operationId: 'reset-1',
    success: true
  });
  // An event without an operation id belongs to no run.
  handleProgress({ status: 'running', percentComplete: 50 });

  assert.deepEqual(
    patches.map(({ operationId, source, patch }) => [operationId, source, patch.message]),
    [
      ['reset-1', 'event', 'signalr.dbReset.starting'],
      ['reset-1', 'event', 'resetting:40'],
      ['reset-1', 'completion', 'reset complete'],
      ['reset-1', 'completion', 'reset complete']
    ]
  );
  assert.equal(patches[1].patch.progress, 40);
  assert.equal(patches[2].patch.progress, 100);
});

for (const status of ['completed', 'failed', 'cancelled']) {
  for (const configured of [false, true]) {
    test(`${status} completion text with a ${configured ? 'present' : 'missing'} message callback`, () => {
      const existing = {
        id: 'active-card',
        type: 'database_reset',
        status: 'running',
        message: 'Meaningful progress',
        detailMessage: 'Kept detail',
        startedAt: new Date(123),
        details: { operationId: 'operation-1' }
      };
      const callbacks = configured
        ? {
            getSuccessMessage: () => 'Required completion sentence',
            getFailureMessage: () => 'Required failure sentence',
            getCancelledMessage: () => 'Required cancellation sentence'
          }
        : {};
      const { patches, dispatchDetail } = recordPatches(existing);
      buildCompleteHandler(
        { type: 'database_reset' },
        callbacks,
        dispatchDetail
      )({
        operationId: 'operation-1',
        success: status === 'completed',
        status,
        ...(status === 'cancelled' ? { message: 'Cancellation detail' } : {})
      });
      const [{ source, patch }] = patches;
      assert.equal(source, 'completion');
      assert.ok(!('detailMessage' in patch), 'an unconfigured detail line is left as it was');
      const sentences = configured
        ? {
            completed: 'Required completion sentence',
            failed: 'Required failure sentence',
            cancelled: 'Required cancellation sentence'
          }
        : {
            completed: 'Meaningful progress',
            failed: 'signalr.generic.failed',
            cancelled: 'Cancellation detail'
          };
      assert.equal(patch.message, sentences[status]);
      assert.equal(patch.error, status === 'failed' ? sentences.failed : undefined);
      assert.equal(patch.details.cancelled, status === 'cancelled' ? true : undefined);
    });
  }
}

test('a real terminal error wins over a success-worded stage key and required callbacks', () => {
  const { patches, dispatchDetail } = recordPatches(undefined);
  buildCompleteHandler(
    { type: 'cache_clearing' },
    {
      getSuccessMessage: () => 'Success sentence',
      getFailureMessage: () => 'Configured failure sentence'
    },
    dispatchDetail
  )({
    operationId: 'clear-1',
    success: false,
    error: 'The cache path cannot be removed',
    stageKey: 'signalr.cacheClear.complete'
  });
  assert.equal(patches[0].patch.message, 'The cache path cannot be removed');
  assert.equal(patches[0].patch.error, 'The cache path cannot be removed');
});

test('an optional terminal detail formatter can clear a completed counter', () => {
  const { patches, dispatchDetail } = recordPatches(undefined);
  buildCompleteHandler(
    { type: 'database_reset' },
    { getDetailMessage: () => undefined, getSuccessMessage: () => 'Reset complete' },
    dispatchDetail
  )({ operationId: 'reset-1', success: true });
  assert.ok('detailMessage' in patches[0].patch);
  assert.equal(patches[0].patch.detailMessage, undefined);
  assert.equal(patches[0].patch.message, 'Reset complete');
});

for (const configured of [false, true])
  test(`status completion uses the ${configured ? 'completion' : 'progress'} callback`, () => {
    const { patches, dispatchDetail } = recordPatches(undefined);
    const handle = buildProgressHandler(
      { type: 'database_reset' },
      {
        getMessage: () => 'Progress sentence',
        getProgress: () => 50,
        getStatus: (event) => event.status,
        ...(configured ? { getCompletedMessage: () => 'Complete sentence' } : {})
      },
      dispatchDetail
    );
    handle({ operationId: 'reset-1', status: 'running' });
    handle({ operationId: 'reset-1', status: 'completed' });
    assert.deepEqual(
      patches.map(({ source }) => source),
      ['event', 'completion']
    );
    assert.equal(patches[1].patch.message, configured ? 'Complete sentence' : 'Progress sentence');
  });

test('a prefill stage change keeps the line, and an eviction tick keeps its warning line', () => {
  const prefill = recordPatches(undefined);
  buildProgressHandler(
    { type: 'scheduled_prefill' },
    {
      getMessage: () => 'Steam: downloading',
      getProgress: (event) => event.percentComplete,
      getDetailMessage: () => '1 GB of 4 GB',
      getStatus: () => undefined,
      getDetails: (event) => ({ stage: event.stage })
    },
    prefill.dispatchDetail
  )({ operationId: 'p-1', stage: 'recovering' });
  assert.deepEqual(prefill.patches[0].patch, { details: { stage: 'recovering' } });

  const eviction = recordPatches(undefined);
  buildProgressHandler(
    { type: 'eviction_scan' },
    {
      getMessage: () => 'Scanning',
      getProgress: () => 40,
      getDetailMessage: () => undefined,
      getStatus: () => undefined
    },
    eviction.dispatchDetail
  )({ operationId: 'e-1' });
  assert.ok(!('detailMessage' in eviction.patches[0].patch));
});

test('required cache and import summaries remain intact without optional stage keys', () => {
  assert.equal(
    modules.formatCacheClearCompleteMessage({
      success: true,
      message: 'Removed 12 files (42 MiB)'
    }),
    'Removed 12 files (42 MiB)'
  );
  assert.equal(
    modules.formatDataImportCompleteMessage({
      success: true,
      message: 'Imported 37 entries in 8 seconds'
    }),
    'Imported 37 entries in 8 seconds'
  );
  assert.equal(
    modules.formatCacheClearCompleteMessage({
      success: true,
      message: 'Removed 12 files',
      stageKey: 'signalr.cacheClear.complete'
    }),
    'signalr.cacheClear.complete'
  );
});

test('incomplete localized interpolation retains the same server sentence', async () => {
  const i18n = moduleUrl(`export default { t: (key, context = {}) => {
    if (key !== 'signalr.scheduledPrefill.runningWithCounts') return key;
    return 'Finished {{completed}} of {{total}}'.replace(/{{(\\w+)}}/g, (token, name) =>
      context[name] === undefined ? token : String(context[name]));
  } };`);
  const helpers = await import(
    await compileToUrl('../src/utils/stageKeyMessage.ts', { '@/i18n': i18n })
  );
  const source = parseSource(REGISTRY_PATH);
  const declaration = findSoleNode(
    source,
    'scheduledPrefillSentence',
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'scheduledPrefillSentence'
  );
  const sentence = bindLifted(`(${declaration.getText(source)})`, helpers);
  assert.equal(
    sentence('signalr.scheduledPrefill.runningWithCounts', { completed: 2 }, 'Finished 2 of 5'),
    'Finished 2 of 5'
  );
  assert.equal(
    sentence(
      'signalr.scheduledPrefill.runningWithCounts',
      { completed: 2, total: 5 },
      'Finished 2 of 5'
    ),
    'Finished 2 of 5'
  );
});
