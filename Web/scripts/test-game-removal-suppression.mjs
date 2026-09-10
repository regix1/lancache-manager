import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import {
  MemoryStorage,
  notificationEvents,
  bindLifted,
  collectNodes,
  compileToUrl,
  liftConstArrow,
  liftHookCallback,
  loadNotificationModules,
  parseSource,
  moduleUrl
} from './transpile-module.mjs';

/**
 * Regression test for the per-item removal card that should stay hidden while the
 * bulk card whose OWN items produce that notification type is running, but must
 * NOT be swallowed by an unrelated bulk card (a different batch, or the same batch
 * running a different item type). Compiles handlers.ts from real source so
 * the suppression check under test is the one that ships. Covers all four batch/
 * item-type pairs: the cache batch's game and service items, the evicted batch,
 * and the log batch.
 */

const I18N_STUB = moduleUrl(`export default { t: (key) => key };`);

const loadHandlerFactories = async (i18nUrl = I18N_STUB) => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const storageUrl = await compileToUrl('../src/utils/storage.ts');
  const handlersUrl = await compileToUrl('../src/contexts/notifications/handlers.ts', {
    './constants': constantsUrl,
    './notificationStatus': statusUrl,
    '@utils/storage': storageUrl,
    '@/i18n': i18nUrl
  });
  return await import(handlersUrl);
};

/** Runs a Started event for `type` against a fresh card list holding one running bulk card. */
const runStartedForType = async (type, bulkCardItemTypes) => {
  globalThis.localStorage = new MemoryStorage();
  const { createStartedHandler } = await loadHandlerFactories();

  let state = [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      startedAt: new Date(),
      details: { itemTypes: bulkCardItemTypes }
    }
  ];
  const setNotifications = (update) => {
    state = update(state);
  };

  const handler = createStartedHandler(
    {
      type,
      getId: () => `${type}_card`,
      storageKey: `test-${type}`,
      defaultMessage: 'Removing...'
    },
    setNotifications
  );

  handler({});
  return state;
};

const hasCard = (state, type) => state.some((n) => n.type === type);

test('cancel requests serialize clicks and protect replacement operations', async () => {
  const errors = [];
  let release;
  let calls = 0;
  let state = {
    id: 'slot',
    type: 'game_detection',
    status: 'running',
    controlOnly: true,
    details: { operationId: 'first' }
  };
  const cancel = bindLifted(
    liftConstArrow('src/components/common/notificationCancel.ts', 'handleCancel'),
    {
      CANCEL_CONFIG_BY_TYPE: { game_detection: { cancelKind: 'serverOp' } },
      pendingCancels: new Set(),
      notifyToastError: (key) => errors.push(key),
      isTerminalNotificationStatus: (status) =>
        ['completed', 'failed', 'cancelled', 'skipped'].includes(status),
      isAbortError: (error) => error?.name === 'AbortError',
      ApiError: class extends Error {},
      ApiService: {
        cancelOperation: () => {
          calls++;
          return new Promise((resolve) => {
            release = resolve;
          });
        },
        forceKillOperation: () => {
          calls++;
          return Promise.reject(new Error('Connection closed'));
        }
      }
    }
  );
  const update = (_id, change) => {
    state = { ...state, ...(typeof change === 'function' ? change(state) : change) };
  };
  const remove = () => {
    state = undefined;
  };
  const get = () => state;
  const first = cancel(state, update, remove, get);
  await cancel(state, update, remove, get);
  assert.equal(calls, 1);
  assert.equal(state.details.cancelPending, true);
  release({});
  await first;
  assert.equal(state.status, 'cancelling');
  await cancel(state, update, remove, get);
  assert.equal(errors.length, 1);
  assert.equal(state.controlOnly, true);
  assert.equal(state.details.cancelRequested, false);
  const deferred = cancel(
    { ...state, details: { ...state.details, cancelRequested: true } },
    update,
    remove,
    get,
    true
  );
  const replacement = {
    id: 'slot',
    type: 'game_detection',
    status: 'running',
    details: { operationId: 'second' }
  };
  state = replacement;
  release({ alreadyFinished: true });
  await deferred;
  assert.equal(state, replacement);
  assert.equal(calls, 3);
});

test('platform starts create only per-platform hidden controls', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  const handlers = await loadHandlerFactories();
  const source = parseSource('src/contexts/notifications/handlers.ts');
  const declaration = collectNodes(
    source,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'buildStartedHandler'
  )[0];
  const build = bindLifted(declaration.getText(source), {
    createStartedHandler: handlers.createStartedHandler
  });
  let state = [];
  const started = build(
    { type: 'scheduled_prefill', id: 'prefill', storageKey: '', cancelKind: 'serverOp' },
    { shouldDisplay: () => false, defaultMessage: 'Prefill' },
    (update) => {
      state = update(state);
    },
    () => undefined
  );
  started({ operationId: 'aggregate' });
  assert.equal(state.length, 0);
  started({ operationId: 'steam-op', serviceId: 'steam' });
  started({ operationId: 'epic-op', serviceId: 'epic' });
  assert.equal(state.length, 2);
  assert.deepEqual(state.map((n) => n.details.service).sort(), ['epic', 'steam']);
  assert.ok(state.every((n) => n.controlOnly));
});

test('hidden waiting handoff preserves the exact replacement in either event order', async () => {
  const handlers = await loadHandlerFactories();
  const old = {
    id: handlers.operationCardId('old'),
    type: 'game_detection',
    status: 'waiting',
    controlOnly: true,
    message: 'Detection',
    startedAt: new Date('2026-09-01T00:00:00Z'),
    details: { operationId: 'old' }
  };
  for (const arrived of [false, true]) {
    const next = {
      ...old,
      id: handlers.operationCardId('new'),
      status: 'running',
      details: { operationId: 'new' }
    };
    const result = await runWaitingCompleteHandler(
      'game_detection',
      arrived ? [old, next] : [old],
      {
        operationId: 'old',
        operationType: 'gameDetection',
        promoted: true,
        nextOperationId: 'new',
        nextStatus: 'running'
      }
    );
    assert.equal(result.state.length, 1);
    assert.equal(result.state[0].details.operationId, 'new');
    assert.equal(result.state[0].controlOnly, true);
    const failed = await runWaitingCompleteHandler('game_detection', result.state, {
      operationId: 'different',
      operationType: 'gameDetection',
      error: 'Validation failed'
    });
    assert.equal(failed.state.length, 2);
    assert.equal(failed.state.find((n) => n.status === 'failed').error, 'Validation failed');
  }
});

test('hidden operations keep separate controls and preserve cancellation during replay', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  const handlers = await loadHandlerFactories();
  const live = {
    id: 'slot',
    type: 'game_detection',
    status: 'running',
    details: { operationId: 'visible' }
  };
  let state = [live];
  const set = (update) => {
    state = update(state);
  };
  const config = {
    type: 'game_detection',
    getId: () => 'slot',
    storageKey: 'hidden',
    shouldDisplay: () => false,
    canControl: () => true,
    defaultMessage: 'Detection',
    getDetails: (event) => ({ operationId: event.operationId })
  };
  const started = handlers.createStartedHandler(config, set);
  started({ operationId: 'first' });
  started({ operationId: 'second' });
  const first = state.find((n) => n.details.operationId === 'first');
  first.details.cancelRequested = true;
  first.details.cancelPending = true;
  started({ operationId: 'first' });
  assert.equal(state.length, 3);
  assert.equal(
    state.find((n) => n.id === 'slot'),
    live
  );
  assert.equal(state.find((n) => n.id === first.id).details.cancelPending, true);
  assert.equal(state.filter((n) => n.controlOnly).length, 2);
  assert.equal(globalThis.localStorage.getItem('hidden'), null);
  for (const useAnimationDelay of [true, false]) {
    const complete = handlers.createCompletionHandler(
      { ...config, useAnimationDelay },
      set,
      () => undefined
    );
    complete({ operationId: 'first', success: false, error: 'Connection closed' });
    complete({ operationId: 'first', success: false, error: 'Connection closed' });
    const failed = state.find((n) => n.details.operationId === 'first');
    assert.equal(failed.status, 'failed');
    assert.equal(failed.error, 'Connection closed');
    assert.equal(failed.controlOnly, undefined);
    assert.equal(state.length, 3);
  }
  handlers.createCompletionHandler(
    config,
    set,
    () => undefined
  )({ operationId: 'second', success: true });
  assert.equal(state.length, 2);
  assert.equal(state[0], live);
});

test('hidden skipped and cancelled progress are quiet while failures survive occupied slots', async () => {
  globalThis.localStorage = new MemoryStorage();
  const handlers = await loadHandlerFactories();
  for (const status of ['skipped', 'cancelled', 'failed']) {
    const live = {
      id: 'slot',
      type: 'game_detection',
      status: 'completed',
      details: { operationId: 'other' }
    };
    let state = [live];
    const progress = handlers.createStatusAwareProgressHandler(
      {
        type: 'game_detection',
        getId: () => 'slot',
        storageKey: '',
        shouldDisplay: () => false,
        canControl: () => true,
        getStatus: (event) => event.status,
        getMessage: () => 'Detection',
        getProgress: () => 42,
        getErrorMessage: () => 'Write failed',
        getDetails: (event) => ({ operationId: event.operationId })
      },
      (update) => {
        state = update(state);
      },
      () => undefined
    );
    progress({ operationId: 'hidden', status: 'running' });
    progress({ operationId: 'hidden', status, error: 'Write failed' });
    assert.equal(state[0], live);
    assert.equal(state.length, status === 'failed' ? 2 : 1);
    if (status === 'failed') assert.equal(state[1].error, 'Write failed');
  }
});

test('eviction waiting wire routes once through warning rendering and actual dismissal', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  const english = JSON.parse(
    readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
  );
  const i18n = {
    t: (key, values = {}) => {
      const sentence = key.split('.').reduce((value, part) => value?.[part], english) ?? key;
      return sentence.replace(/{{(\w+)}}/g, (_, name) => String(values[name] ?? ''));
    }
  };
  const i18nUrl = moduleUrl(
    `const english = ${JSON.stringify(english)}; export default { t: ${i18n.t.toString()} };`
  );
  const handlers = await loadHandlerFactories(i18nUrl);
  const constants = await import(await compileToUrl('../src/contexts/notifications/constants.ts'));
  const { isTerminalNotificationStatus } = await import(
    await compileToUrl('../src/contexts/notifications/notificationStatus.ts')
  );
  const source = parseSource('src/contexts/notifications/useNotificationHandlers.ts');
  const lookup = collectNodes(
    source,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'findEntryForWireType'
  )[0];
  const findEntryForWireType = bindLifted(
    `(registry, wireType) => { ${lookup.getText(source)} return findEntryForWireType(registry, wireType); }`,
    constants
  );
  const entries = parseSource('src/contexts/notifications/notificationRegistry.ts');
  const entry = collectNodes(
    entries,
    (node) =>
      ts.isObjectLiteralExpression(node) &&
      node.properties.some(
        (property) =>
          ts.isPropertyAssignment(property) &&
          property.name.getText(entries) === 'type' &&
          property.initializer.getText(entries) === "'eviction_scan'"
      )
  )[0];
  const registry = [
    Object.fromEntries(
      ['type', 'id'].map((name) => {
        const value = entry.properties
          .find((property) => property.name?.getText(entries) === name)
          .initializer.getText(entries);
        return [name, bindLifted(`() => (${value})`, constants)()];
      })
    )
  ];
  assert.equal(findEntryForWireType(registry, 'evictionScan'), registry[0]);
  assert.equal(findEntryForWireType(registry, 'unknown'), undefined);
  const live = { ...registry[0], status: 'running', details: { operationId: 'existing' } };
  let state = [live];
  const setNotifications = (update) => {
    state = update(state);
  };
  const timers = [];
  const setTimeout = (callback) => {
    timers.push(callback);
    return timers.length;
  };
  const autoDismissTimersRef = { current: new Map() };
  let keep = false;
  const removeNotificationAnimated = bindLifted(
    liftHookCallback(
      'src/contexts/notifications/NotificationsContext.tsx',
      'useCallback',
      'NOTIFICATION_REMOVING'
    ),
    {
      window: { dispatchEvent: () => undefined },
      CustomEvent: class {},
      APP_EVENTS: { NOTIFICATION_REMOVING: 'removing' },
      setTimeout,
      setNotifications,
      autoDismissTimersRef,
      events: notificationEvents(),
      isTerminalNotificationStatus,
      NOTIFICATION_ANIMATION_DURATION_MS: constants.NOTIFICATION_ANIMATION_DURATION_MS
    }
  );
  const scheduleAutoDismiss = bindLifted(
    liftHookCallback(
      'src/contexts/notifications/NotificationsContext.tsx',
      'useCallback',
      'const currentTimer'
    ),
    {
      shouldAutoDismiss: () => !keep,
      cancelAutoDismissTimer: () => undefined,
      getNextInstanceId: () => 1,
      autoDismissTimersRef,
      setNotifications,
      setTimeout,
      queueMicrotask: (callback) => callback(),
      removeNotificationAnimated,
      isTerminalNotificationStatus,
      AUTO_DISMISS_DELAY_MS: constants.AUTO_DISMISS_DELAY_MS
    }
  );
  const waiting = bindLifted(
    liftConstArrow('src/contexts/notifications/useNotificationHandlers.ts', 'waitingHandler'),
    {
      registry,
      findEntryForWireType,
      acknowledgedIds: new Set(),
      events: notificationEvents(),
      ...handlers,
      cancelAutoDismissTimer: () => undefined,
      scheduleAutoDismiss,
      setNotifications,
      isTerminalNotificationStatus,
      i18n
    }
  );
  const event = {
    operationId: 'admitted',
    operationType: 'evictionScan',
    name: 'Eviction Scan',
    blockedByName: 'Cache File Scan',
    silent: true
  };
  for (const eventFirst of [true, false]) {
    const key = `http-${eventFirst}`;
    const response = { status: 'skipped', skippedReason: 'held', showNotification: false };
    const handleRunNow = bindLifted(
      liftHookCallback(
        'src/components/features/management/schedules/SchedulesSection.tsx',
        'useCallback',
        'ApiService.triggerSchedule(key)'
      ),
      {
        ApiService: { triggerSchedule: async () => response },
        t: i18n.t,
        markStarting: () => undefined,
        clearPending: () => undefined,
        setCompletedKeys: () => undefined,
        setTimeout: () => undefined,
        addNotification: (notice) => {
          state.push(notice);
        },
        cacheQueuedReasonKey: 'held',
        getErrorMessage: String
      }
    );
    if (eventFirst) waiting({ ...event, operationId: key });
    await handleRunNow('cacheReconciliation');
    if (!eventFirst) waiting({ ...event, operationId: key });
    assert.equal(state.length, 3);
    assert.equal(state.find((n) => n.type === 'generic').id, `queued_${key}`);
    timers.shift()();
    timers.shift()();
    assert.equal(state.length, 2);
    assert.equal(state.find((n) => n.controlOnly).details.operationId, key);
    state = [live];
  }
  waiting(event);
  waiting(event);
  assert.equal(state[0], live);
  const card = state.find((n) => n.type === 'generic');
  assert.equal(card.id, 'queued_admitted');
  assert.equal(card.type, 'generic');
  assert.equal(card.status, 'skipped');
  assert.equal(card.details.notificationType, 'warning');
  assert.equal(card.progress, undefined);
  assert.equal(
    card.message,
    i18n.t('management.schedules.queuedUntilCacheFreeNamed', { name: event.name })
  );
  const color = bindLifted(
    liftConstArrow('src/components/common/notificationCancel.ts', 'getNotificationColor'),
    {}
  );
  assert.equal(color(card), 'var(--theme-warning)');
  const bar = parseSource('src/components/common/UniversalNotificationBar.tsx', ts.ScriptKind.TSX);
  const classified = collectNodes(
    bar,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(bar) === 'classified'
  )[0];
  const classify = bindLifted(
    `() => { let fullOrder = 0; return ${classified.initializer.getText(bar)}; }`,
    {
      sorted: [card],
      controls: [],
      SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY: {},
      displayModes: { cacheReconciliation: 'condensed' },
      NOTIFICATION_IDS: constants.NOTIFICATION_IDS,
      isMobile: false,
      MOBILE_FULL_CARD_CAP: 2
    }
  );
  assert.equal(classify()[0].condensed, false);
  assert.equal(timers.length, 1);
  timers.shift()();
  timers.shift()();
  assert.equal(state.length, 2);
  assert.equal(state.find((n) => n.controlOnly).details.operationId, 'admitted');
  waiting(event);
  assert.equal(state.length, 2);
  assert.equal(timers.length, 0);
  keep = true;
  waiting({ ...event, operationId: 'kept' });
  assert.equal(state.find((n) => n.type === 'generic').status, 'skipped');
  assert.equal(timers.length, 0);
  assert.equal(
    handlers.waitingCardMessage({ name: 'Eviction Scan', blockedByName: 'Eviction Scan' }),
    'Eviction Scan: waiting for Eviction Scan to finish...'
  );
});

test('schedule HTTP responses leave retained and hidden acknowledgment to the waiting event', async () => {
  const source = liftHookCallback(
    'src/components/features/management/schedules/SchedulesSection.tsx',
    'useCallback',
    'ApiService.triggerSchedule(key)'
  );
  for (const eventFirst of [true, false]) {
    for (const response of [
      { status: 'skipped', skippedReason: 'held', showNotification: false },
      { status: 'alreadyRunning', alreadyRunning: true, showNotification: false },
      { status: 'started', showNotification: false }
    ]) {
      const notices = [];
      const handler = bindLifted(source, {
        ApiService: { triggerSchedule: async () => response },
        t: (key) => key,
        markStarting: () => undefined,
        clearPending: () => undefined,
        setCompletedKeys: () => undefined,
        setTimeout: () => undefined,
        addNotification: (notice) => notices.push(notice),
        cacheQueuedReasonKey: 'held',
        getErrorMessage: String
      });
      const yellow = {
        type: 'generic',
        status: 'skipped',
        details: { notificationType: 'warning' }
      };
      if (eventFirst && response.status !== 'started') notices.push(yellow);
      await handler('cacheReconciliation');
      if (!eventFirst && response.status !== 'started') notices.push(yellow);
      assert.deepEqual(notices, response.status === 'started' ? [] : [yellow]);
    }
  }
});

test('hidden immediate Storage scan response creates no running seed', async () => {
  const source = liftConstArrow(
    'src/components/features/management/sections/StorageSection.tsx',
    'handleStartEvictionScan'
  );
  const identity = parseSource(
    'src/components/features/management/game-detection/gameRemovalEntity.ts'
  );
  const predicate = collectNodes(
    identity,
    (node) =>
      ts.isFunctionDeclaration(node) && node.name?.text === 'shouldPinOperationIdFromResponse'
  )[0];
  const { shouldPinOperationIdFromResponse } = await import(
    moduleUrl(
      ts.transpileModule(predicate.getText(identity), {
        compilerOptions: { module: ts.ModuleKind.ESNext }
      }).outputText
    )
  );
  let result = { operationId: 'scan', showNotification: false };
  let seeded = false;
  const handler = bindLifted(source, {
    evictionScanInFlightRef: { current: false },
    setIsStartingEvictionScan: () => undefined,
    ApiService: {
      startEvictionScan: async () => result
    },
    shouldPinOperationIdFromResponse,
    addNotification: () => {
      seeded = true;
    },
    buildSeededRunningNotification: () => ({}),
    t: (key) => key,
    onError: (error) => {
      throw new Error(error);
    },
    getErrorMessage: String,
    isMountedRef: { current: true }
  });
  await handler();
  assert.equal(seeded, false);
  result = { operationId: 'scan', queued: true };
  await handler();
  assert.equal(seeded, false);
  result = { operationId: 'scan', alreadyRunning: true };
  await handler();
  assert.equal(seeded, false);
  result = { operationId: 'scan', showNotification: true };
  await handler();
  assert.equal(seeded, true);
});

test('schedule responses retain visible success and genuine skipped or failed messages', async () => {
  const source = liftHookCallback(
    'src/components/features/management/schedules/SchedulesSection.tsx',
    'useCallback',
    'ApiService.triggerSchedule(key)'
  );
  for (const [response, expected] of [
    [{ status: 'skipped', skippedReason: 'unrelated', showNotification: false }, 'warning'],
    [{ status: 'started', showNotification: true }, 'success'],
    [{ status: 'alreadyRunning', alreadyRunning: true, showNotification: true }, 'info'],
    [null, 'error']
  ]) {
    const notices = [];
    const handler = bindLifted(source, {
      ApiService: {
        triggerSchedule: async () => {
          if (!response) throw new Error('failure');
          return response;
        }
      },
      t: (key) => key,
      markStarting: () => undefined,
      clearPending: () => undefined,
      setCompletedKeys: () => undefined,
      setTimeout: () => undefined,
      addNotification: (notice) => notices.push(notice),
      cacheQueuedReasonKey: 'held',
      getErrorMessage: String
    });
    await handler('cacheReconciliation');
    assert.equal(notices.length, 1);
    assert.equal(notices[0].details.notificationType, expected);
  }
});

test('all maintenance waits resolve through the shipped map and registry cancel contract', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  const modules = await loadNotificationModules(I18N_STUB);
  const source = parseSource('src/contexts/notifications/useNotificationHandlers.ts');
  const lookup = collectNodes(
    source,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'findEntryForWireType'
  )[0];
  const findEntryForWireType = bindLifted(
    `(registry, wireType) => { ${lookup.getText(source)} return findEntryForWireType(registry, wireType); }`,
    modules
  );
  const expected = {
    logRotation: ['log_rotation', 'none'],
    gameImageFetch: ['game_image_fetch', 'serverOp'],
    cacheSnapshot: ['cache_snapshot', 'serverOp'],
    operationHistoryCleanup: ['operation_history_cleanup', 'serverOp'],
    dashboardCacheWarmer: ['dashboard_cache_warmer', 'serverOp']
  };

  for (const [wireType, [notificationType, cancelKind]] of Object.entries(expected)) {
    assert.equal(modules.OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[wireType], notificationType);
    const entry = findEntryForWireType(modules.NOTIFICATION_REGISTRY, wireType);
    assert.equal(entry?.type, notificationType);
    assert.equal(entry?.cancelKind, cancelKind);
  }

  assert.equal(findEntryForWireType(modules.NOTIFICATION_REGISTRY, 'cacheFileCount'), undefined);
});

const CASES = [
  ['game_removal', 'cache batch, game items'],
  ['service_removal', 'cache batch, service items'],
  ['eviction_removal', 'evicted batch'],
  ['log_removal', 'log batch']
];

for (const [type, label] of CASES) {
  test(`${label}: an unrelated running batch does not suppress the ${type} card`, async () => {
    const state = await runStartedForType(type, []);
    assert.ok(hasCard(state, type), `${type} card should open when no running batch owns it`);
  });

  test(`${label}: the owning bulk card suppresses the ${type} card`, async () => {
    const state = await runStartedForType(type, [type]);
    assert.ok(!hasCard(state, type), `${type} card should stay suppressed while its batch runs`);
  });
}

test('a batch declaring two item types suppresses both and leaves other types alone', async () => {
  const bulkCardItemTypes = ['game_removal', 'service_removal'];
  assert.ok(
    !hasCard(await runStartedForType('game_removal', bulkCardItemTypes), 'game_removal'),
    'game_removal is one of the batch item types, so it should stay suppressed'
  );
  assert.ok(
    !hasCard(await runStartedForType('service_removal', bulkCardItemTypes), 'service_removal'),
    'service_removal is the other batch item type, so it should also stay suppressed'
  );
  assert.ok(
    hasCard(await runStartedForType('eviction_removal', bulkCardItemTypes), 'eviction_removal'),
    "eviction_removal is not one of this batch's item types, so its card should still open"
  );
  assert.ok(
    hasCard(await runStartedForType('log_removal', bulkCardItemTypes), 'log_removal'),
    "log_removal is not one of this batch's item types, so its card should still open"
  );
});

/**
 * The Started handler is not the only place the marker gates a card: a queued item that has
 * not started yet renders a purple waiting card through a separate handler in
 * useNotificationHandlers.ts, and that handler needs the same guard or a queued item would
 * flash a waiting card beside the batch card. Lift the handler straight out of its source (it
 * lives inside a hook's useEffect and is not exported) and run it with its free variables
 * supplied directly, the same way test-context-resync-wiring.mjs runs a recovery callback
 * lifted out of a hook call.
 */
const QUEUED_BEHIND_SCAN = {
  operationType: 'irrelevant-for-this-test',
  operationId: 'op-1',
  name: 'Eviction Scan',
  blockedByName: 'Cache File Scan'
};

/**
 * Runs waitingHandler for one queued event of `type` against a starting card list. `event` and
 * `dismissed` are for the silent run, which sends a different event and is the only case that
 * arms the dismiss timer from in here.
 */
const runWaitingHandler = async (
  type,
  startingCards,
  event = QUEUED_BEHIND_SCAN,
  dismissed = [],
  repeats = 1,
  updates = []
) => {
  const {
    createCompletionHandler,
    findBulkCardOwningOperation,
    eventTargetsCard,
    operationCardId,
    rememberEvent
  } = await loadHandlerFactories();
  const { isTerminalNotificationStatus } = await import(
    await compileToUrl('../src/contexts/notifications/notificationStatus.ts')
  );

  const arrowSource = liftConstArrow(
    'src/contexts/notifications/useNotificationHandlers.ts',
    'waitingHandler'
  );

  let state = startingCards;
  const setNotifications = (updater) => {
    state = updater(state);
    updates.push(state);
  };

  const waitingHandler = bindLifted(arrowSource, {
    registry: [],
    acknowledgedIds: new Set(),
    events: notificationEvents(),
    createCompletionHandler,
    operationCardId,
    rememberEvent,
    findEntryForWireType: () => ({ type, id: `${type}_card` }),
    cancelAutoDismissTimer: () => undefined,
    scheduleAutoDismiss: (id) => dismissed.push(id),
    setNotifications,
    findBulkCardOwningOperation,
    eventTargetsCard,
    isTerminalNotificationStatus,
    i18n: { t: (key, values) => (values?.name ? `${key}:${values.name}` : key) },
    waitingCardMessage: (source) =>
      source.blockedByName ? `waiting for ${source.blockedByName}` : 'waiting'
  });

  for (let index = 0; index < repeats; index++) waitingHandler(event);
  return state;
};

/**
 * A run whose schedule told it to keep its cards to itself still has to say it was queued: with no
 * card at all, a person who set the schedule reads the silence as the run having been dropped. It
 * says it once, in the amber notice that times out on its own, and never puts up the purple card
 * that would sit there until the blocker finished. The notice names the run, because several
 * schedules can be silent and a sentence about "this run" does not say which one is waiting.
 */
test('a silent queued run gets the self-clearing notice instead of the purple waiting card', async () => {
  const dismissed = [];
  const state = await runWaitingHandler(
    'game_removal',
    [],
    { ...QUEUED_BEHIND_SCAN, silent: true },
    dismissed
  );

  const card = state.find((n) => n.id === `queued_${QUEUED_BEHIND_SCAN.operationId}`);
  assert.equal(card.status, 'skipped', 'a silent run must not raise the purple waiting card');
  assert.equal(
    card.message,
    'management.schedules.queuedUntilCacheFreeNamed:Eviction Scan',
    'it names the run and says it starts by itself, not who it is parked behind'
  );
  assert.deepEqual(
    dismissed,
    [`queued_${QUEUED_BEHIND_SCAN.operationId}`],
    'the notice clears itself; nothing else is coming to remove it'
  );
});

test('silent acknowledgment preserves an occupied live slot and dismisses once', async () => {
  const live = {
    id: 'game_removal_card',
    type: 'game_removal',
    status: 'running',
    details: { operationId: 'other' }
  };
  const dismissed = [];
  const state = await runWaitingHandler(
    'game_removal',
    [live],
    { ...QUEUED_BEHIND_SCAN, silent: true },
    dismissed,
    2
  );
  assert.equal(state.length, 3);
  assert.equal(state[0], live);
  assert.equal(state.filter((n) => n.controlOnly).length, 1);
  assert.equal(state.find((n) => n.type === 'generic').status, 'skipped');
  assert.equal(dismissed.length, 1);
});

test('a run that is not silent still gets the purple waiting card naming its blocker', async () => {
  const dismissed = [];
  const state = await runWaitingHandler('game_removal', [], QUEUED_BEHIND_SCAN, dismissed);

  const card = state.find((n) => n.id === 'game_removal_card');
  assert.equal(card.status, 'waiting');
  assert.equal(card.message, 'waiting for Cache File Scan');
  assert.deepEqual(dismissed, [], 'the purple card stays up until the operation leaves the queue');
});

test('an unchanged queued event preserves the card and collection identities', async () => {
  const updates = [];
  await runWaitingHandler('game_removal', [], QUEUED_BEHIND_SCAN, [], 2, updates);
  assert.equal(updates.length, 2);
  assert.equal(updates[1], updates[0]);
  assert.equal(updates[1][0], updates[0][0]);
});

test('queued updates preserve pending and acknowledged cancellation state', async () => {
  for (const status of ['waiting', 'cancelling']) {
    const [queued] = await runWaitingHandler('game_removal', []);
    const card = {
      ...queued,
      status,
      startedAt: new Date('2026-01-01T00:00:00Z'),
      instanceVersion: 7,
      details: {
        ...queued.details,
        cancelRequested: true,
        cancelSent: true,
        cancelPending: status === 'waiting',
        cancelling: status === 'cancelling'
      }
    };
    const initial = [card];
    const repeated = await runWaitingHandler('game_removal', initial);
    assert.equal(repeated, initial);
    const changed = await runWaitingHandler('game_removal', initial, {
      ...QUEUED_BEHIND_SCAN,
      blockedByName: 'Eviction Scan'
    });
    assert.equal(changed.length, 1);
    assert.deepEqual(changed[0], { ...card, message: 'waiting for Eviction Scan' });
    assert.equal(changed[0].details, card.details);
    assert.equal(changed[0].startedAt, card.startedAt);
    assert.equal(
      await runWaitingHandler('game_removal', changed, {
        ...QUEUED_BEHIND_SCAN,
        blockedByName: 'Eviction Scan'
      }),
      changed
    );
  }
});

test('a queued item opens its waiting card when no owning bulk card is running', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      startedAt: new Date(),
      details: { itemTypes: ['eviction_removal'] }
    }
  ]);
  assert.ok(
    state.some((n) => n.id === 'game_removal_card' && n.status === 'waiting'),
    'a queued item should get its own waiting card when nothing suppresses it'
  );
});

test('the owning bulk card suppresses the queued waiting card too, not just the started card', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      startedAt: new Date(),
      // The batch's item request is still on the wire, which is exactly when the queue push for
      // that item arrives with no id on the card to compare it against.
      details: { itemTypes: ['service_removal', 'game_removal'], itemRequestPending: true }
    }
  ]);
  assert.ok(
    !state.some((n) => n.id === 'game_removal_card' && n.status === 'waiting'),
    'the waiting card must stay suppressed while the owning bulk card is running'
  );
  // Suppressing the card must not also swallow the blocker's name: it is the only thing that
  // explains why the batch is sitting still, and the batch card cannot work it out on its own.
  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.message,
    'waiting for Cache File Scan',
    'the batch card must name the blocking operation while its item is parked'
  );
});

test('a batch still owns its item cards after it turns purple while queued', async () => {
  const { findBulkCardOwningOperation } = await loadHandlerFactories();
  // The batch card goes to 'waiting' while its current item is parked behind another operation.
  // It is still the owner: if this stopped matching, the queue's own card would reappear next to
  // it and the user would see the same sentence twice, which is the bug this whole pair prevents.
  const parkedBatch = [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'waiting',
      details: { itemTypes: ['service_removal', 'game_removal'], itemRequestPending: true }
    }
  ];
  assert.ok(
    findBulkCardOwningOperation('game_removal', 'op-1', parkedBatch),
    'a parked batch must still own the item types it declared'
  );
  assert.equal(
    findBulkCardOwningOperation('log_removal', 'op-1', parkedBatch),
    undefined,
    'it must not claim a type it never declared'
  );
});

test('a batch whose items are a different type keeps its own message', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['eviction_removal'] }
    }
  ]);
  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.message,
    'Removing 1 of 2 - Arma 3',
    'an unrelated batch must not have its message overwritten by another queue blocker'
  );
});

/**
 * Declaring an item type is not the same as having started the operation. Two batches can declare
 * one type, and a batch shares its types with every removal a user starts from elsewhere in the
 * app, so the type alone cannot decide whose queued operation this is. A batch publishes its
 * current item's operation id while that item is in flight, and that is what settles it.
 */
test('a queued operation the batch did not start gets its own card instead of relabelling the batch', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['game_removal'], currentOperationId: 'the-batch-own-item' }
    }
  ]);

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.status,
    'running',
    "a batch busy with its own item must not be relabelled by another operation's queue event"
  );
  assert.equal(bulk.message, 'Removing 1 of 2 - Arma 3', 'and it keeps its own item line');

  const queued = state.find((n) => n.id === 'game_removal_card');
  assert.ok(queued, 'the queued operation needs a card of its own or it cannot be cancelled');
  assert.equal(
    queued.details.operationId,
    'op-1',
    'that card carries the operation id the X button cancels'
  );
});

/**
 * An empty `currentOperationId` is only ever ambiguous for one request round trip: the queue push
 * arrives while the item's own request is still on the wire. Outside that window the field is empty
 * because the batch has nothing to publish - the queue answered 'alreadyRunning' and handed back a
 * live removal's id the batch refused - and that lasts as long as the other removal runs. Treating
 * it as ownership there hides an unrelated operation behind the batch card for minutes, with no card
 * of its own to cancel from.
 */
test('a batch with no request on the wire does not swallow a queued operation it never started', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['game_removal'] }
    }
  ]);

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(bulk.status, 'running', 'the batch is busy with its own item, not with this one');
  assert.equal(bulk.message, 'Removing 1 of 2 - Arma 3', 'and it keeps its own item line');

  const queued = state.find((n) => n.id === 'game_removal_card');
  assert.ok(queued, 'the queued operation needs a card of its own or it cannot be cancelled');
  assert.equal(queued.details.operationId, 'op-1');
});

test('the batch card takes the queue wording for the item the batch itself started', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'bulk_removal_x',
      type: 'bulk_removal',
      status: 'running',
      message: 'Removing 1 of 2 - Arma 3',
      startedAt: new Date(),
      details: { itemTypes: ['game_removal'], currentOperationId: 'op-1' }
    }
  ]);

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(bulk.status, 'waiting');
  assert.equal(bulk.message, 'waiting for Cache File Scan');
  assert.ok(
    !state.some((n) => n.id === 'game_removal_card'),
    'the batch card already reports this item, so no second card may appear beside it'
  );
});

test('a queued operation does not evict the running card of another operation in the same slot', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'game_removal_card',
      type: 'game_removal',
      status: 'running',
      message: 'Removing Arma 3',
      detailMessage: 'Deleting 1200 files',
      progress: 42,
      startedAt: new Date(),
      details: { operationId: 'B' }
    }
  ]);

  const running = state.find((n) => n.id === 'game_removal_card');
  assert.equal(running.status, 'running', 'the running operation keeps its card');
  assert.equal(running.progress, 42, 'and its progress');
  assert.equal(running.detailMessage, 'Deleting 1200 files', 'and its stage text');
  assert.equal(
    running.details.operationId,
    'B',
    'and its operation id, so the X still cancels the operation the card is showing'
  );
});

test('a queued operation still takes the slot from a card whose operation has finished', async () => {
  const state = await runWaitingHandler('game_removal', [
    {
      id: 'game_removal_card',
      type: 'game_removal',
      status: 'completed',
      message: 'Removed Arma 3',
      startedAt: new Date(),
      details: { operationId: 'B' }
    }
  ]);

  const card = state.find((n) => n.id === 'game_removal_card');
  assert.equal(card.status, 'waiting', 'a finished card is not live state worth protecting');
  assert.equal(card.details.operationId, 'op-1');
});

/** Runs waitingCompleteHandler for one wait-queue completion against a starting card list. */
const runWaitingCompleteHandler = async (type, startingCards, event) => {
  const arrowSource = liftConstArrow(
    'src/contexts/notifications/useNotificationHandlers.ts',
    'waitingCompleteHandler'
  );

  let state = startingCards;
  const dismissed = [];
  const setNotifications = (updater) => {
    state = updater(state);
  };

  const waitingCompleteHandler = bindLifted(arrowSource, {
    registry: [],
    acknowledgedIds: new Set(),
    events: notificationEvents(),
    ...(await loadHandlerFactories()),
    recover: undefined,
    cancelAutoDismissTimer: () => undefined,
    findEntryForWireType: () => ({ type, id: `${type}_card` }),
    setNotifications,
    scheduleAutoDismiss: (id) => dismissed.push(id),
    i18n: { t: (key) => key },
    GENERIC_FAILURE_I18N_KEY: 'generic.failure'
  });

  waitingCompleteHandler(event);
  return { state, dismissed };
};

test('a wait-queue completion for an operation nothing is showing arms no dismiss timer', async () => {
  const { state, dismissed } = await runWaitingCompleteHandler(
    'game_removal',
    [
      {
        id: 'game_removal_card',
        type: 'game_removal',
        status: 'running',
        startedAt: new Date(),
        details: { operationId: 'B' }
      },
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'waiting',
        startedAt: new Date(),
        details: { itemTypes: ['game_removal'], currentOperationId: 'C' }
      }
    ],
    { operationType: 'gameRemoval', operationId: 'A', cancelled: true }
  );

  assert.deepEqual(dismissed, [], 'nothing matched, so nothing may be put on a timer to disappear');
  assert.equal(
    state.find((n) => n.id === 'bulk_removal_x').status,
    'waiting',
    "a batch parked behind a different operation is none of this event's business"
  );
});

test('a batch card relabelled while its item was queued goes back to running when that item is cancelled', async () => {
  const { state, dismissed } = await runWaitingCompleteHandler(
    'game_removal',
    [
      {
        id: 'bulk_removal_x',
        type: 'bulk_removal',
        status: 'waiting',
        message: 'waiting for Cache File Scan',
        startedAt: new Date(),
        details: { itemTypes: ['game_removal'], currentOperationId: 'A' }
      }
    ],
    { operationType: 'gameRemoval', operationId: 'A', cancelled: true }
  );

  const bulk = state.find((n) => n.id === 'bulk_removal_x');
  assert.equal(
    bulk.status,
    'running',
    'the batch run is not over just because one of its items left the queue'
  );
  assert.deepEqual(dismissed, [], 'the batch card is still live, so it must not be timed out');
});

/**
 * A batch card is only quiet because it declares which per-item notification types its own items
 * produce. Nothing in the type system requires that: a fourth batch added later without
 * `details.itemTypes` duplicates every per-item card and every behaviour test above still passes,
 * because those tests supply the declaration themselves rather than reading it from source.
 *
 * So read it from source. Every `addNotification({ type: 'bulk_removal', ... })` in the app must
 * carry a literal `itemTypes`, and the set of types declared across all batches must match the
 * CASES the behaviour tests exercise - a new batch type with no behaviour test fails here, and a
 * behaviour test for a type no batch declares fails here too.
 */
const sourceFilesUnder = (dirUrl) => {
  const found = [];
  const walk = (url) => {
    for (const entry of readdirSync(url, { withFileTypes: true })) {
      const child = new URL(`${entry.name}${entry.isDirectory() ? '/' : ''}`, url);
      if (entry.isDirectory()) {
        walk(child);
      } else if (/\.tsx?$/.test(entry.name)) {
        found.push(child);
      }
    }
  };
  walk(dirUrl);
  return found;
};

/** The object literal passed to an `addNotification({ type: 'bulk_removal', ... })` call. */
const bulkCardLiterals = (sourceFile) =>
  collectNodes(
    sourceFile,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile).endsWith('addNotification') &&
      node.arguments.length > 0 &&
      ts.isObjectLiteralExpression(node.arguments[0]) &&
      node.arguments[0].properties.some(
        (prop) =>
          ts.isPropertyAssignment(prop) &&
          prop.name.getText(sourceFile) === 'type' &&
          ts.isStringLiteral(prop.initializer) &&
          prop.initializer.text === 'bulk_removal'
      )
  ).map((call) => call.arguments[0]);

/** Value of `property` on an object literal, or undefined when absent. */
const literalProperty = (objectLiteral, sourceFile, property) =>
  objectLiteral.properties.find(
    (prop) => ts.isPropertyAssignment(prop) && prop.name.getText(sourceFile) === property
  )?.initializer;

test('every batch card in the app declares the item types it owns', () => {
  const declared = new Set();
  let batchCardsFound = 0;

  for (const fileUrl of sourceFilesUnder(new URL('../src/', import.meta.url))) {
    const source = readFileSync(fileUrl, 'utf8');
    if (!source.includes('bulk_removal')) continue;

    const relative = fileUrl.href.slice(new URL('../', import.meta.url).href.length);
    const sourceFile = ts.createSourceFile(relative, source, ts.ScriptTarget.Latest, true);

    for (const card of bulkCardLiterals(sourceFile)) {
      batchCardsFound += 1;
      const details = literalProperty(card, sourceFile, 'details');
      assert.ok(
        details && ts.isObjectLiteralExpression(details),
        `${relative}: a bulk_removal card needs a literal details object declaring itemTypes`
      );

      const itemTypes = literalProperty(details, sourceFile, 'itemTypes');
      assert.ok(
        itemTypes && ts.isArrayLiteralExpression(itemTypes) && itemTypes.elements.length > 0,
        `${relative}: this batch card declares no itemTypes, so every item it runs will open a ` +
          'second card beside it. Add the per-item notification types this batch produces.'
      );

      for (const element of itemTypes.elements) {
        assert.ok(
          ts.isStringLiteral(element),
          `${relative}: itemTypes must be string literals so this check can read them`
        );
        declared.add(element.text);
      }
    }
  }

  assert.ok(batchCardsFound > 0, 'found no bulk_removal cards at all; this check has gone blind');
  assert.deepEqual(
    [...declared].sort(),
    CASES.map(([type]) => type).sort(),
    'the types batches declare and the types the behaviour tests above cover have drifted apart'
  );
});
