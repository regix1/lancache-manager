import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import {
  MemoryStorage,
  notificationEvents,
  loadNotificationModules,
  liftConstArrow,
  liftHookCallback,
  bindLifted,
  compileToUrl,
  findSoleNode,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * Every notification type shares one set of handler factories, and the per-event card id that
 * scheduled prefill needs runs through all of them. A type that does not ask for it must keep the
 * one card it has always had, and must keep persisting that card on its own rather than inside a
 * record of cards. Database reset stands in for the eighteen types in that position: it has all
 * three lifecycle phases, so a started, a progress and a terminal event all pass through here.
 */

const REGISTRY_PATH = 'src/contexts/notifications/notificationRegistry.ts';
const HANDLERS_PATH = 'src/contexts/notifications/handlers.ts';

const i18nStub = { t: (key) => key };

const registryFile = parseSource(REGISTRY_PATH);

const registryArray = findSoleNode(
  registryFile,
  'NOTIFICATION_REGISTRY declaration',
  (node) =>
    ts.isVariableDeclaration(node) &&
    node.name.getText(registryFile) === 'NOTIFICATION_REGISTRY' &&
    node.initializer !== undefined &&
    ts.isArrayLiteralExpression(node.initializer)
).initializer;

const entryObject = (element) => (ts.isCallExpression(element) ? element.arguments[0] : element);

const typeOf = (element) => {
  const object = entryObject(element);
  const property = ts.isObjectLiteralExpression(object)
    ? object.properties.find(
        (candidate) =>
          ts.isPropertyAssignment(candidate) && candidate.name.getText(registryFile) === 'type'
      )
    : undefined;
  return property && ts.isStringLiteral(property.initializer)
    ? property.initializer.text
    : undefined;
};

const liftHandlerBuilder = (name, bindings) => {
  const sourceFile = parseSource(HANDLERS_PATH);
  const declaration = findSoleNode(
    sourceFile,
    `${name} declaration`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
  );
  return bindLifted(`(${declaration.getText(sourceFile)})`, bindings);
};

const loadHandlers = async () => {
  globalThis.sessionStorage ??= new MemoryStorage();
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const storageUrl = await compileToUrl('../src/utils/storage.ts');
  return await import(
    await compileToUrl('../src/contexts/notifications/handlers.ts', {
      './constants': constantsUrl,
      './notificationStatus': statusUrl,
      '@utils/storage': storageUrl,
      '@/i18n': moduleUrl('export default { t: (key) => key };')
    })
  );
};

const liftDatabaseResetEntry = async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const i18nUrl = moduleUrl('export default { t: (key) => key };');
  const registryEntries = await import(
    await compileToUrl('../src/contexts/notifications/registryEntries.ts', {
      './constants': constantsUrl,
      '@utils/stageKeyMessage': await compileToUrl('../src/utils/stageKeyMessage.ts', {
        '@/i18n': i18nUrl
      }),
      '@/i18n': i18nUrl
    })
  );
  const constants = await import(constantsUrl);

  const element = registryArray.elements.find(
    (candidate) => typeOf(candidate) === 'database_reset'
  );
  assert.ok(element, 'database_reset is not in NOTIFICATION_REGISTRY');
  return bindLifted(`() => (${element.getText(registryFile)})`, {
    buildStandardOperationEntry: registryEntries.buildStandardOperationEntry,
    stageKeyMessage: registryEntries.stageKeyMessage,
    cappedProgress: registryEntries.cappedProgress,
    operationIdDetails: registryEntries.operationIdDetails,
    NOTIFICATION_IDS: constants.NOTIFICATION_IDS,
    NOTIFICATION_STORAGE_KEYS: constants.NOTIFICATION_STORAGE_KEYS,
    GENERIC_FAILURE_I18N_KEY: constants.GENERIC_FAILURE_I18N_KEY,
    CANCEL_TOOLTIP: { databaseReset: 'common.notifications.cancelDatabaseReset' },
    translateRecoveryStage: (stageKey, context, fallbackKey) => stageKey ?? fallbackKey,
    formatDatabaseResetProgressMessage: () => undefined,
    formatDatabaseResetCompleteMessage: () => undefined,
    i18n: i18nStub
  })();
};

const newCardList = () => {
  const cards = { state: [], dismissals: [] };
  cards.setNotifications = (updater) => {
    cards.state = updater(cards.state);
  };
  cards.scheduleAutoDismiss = (id, delayMs) => cards.dismissals.push([id, delayMs]);
  cards.cancelAutoDismissTimer = () => undefined;
  cards.events = notificationEvents();
  return cards;
};

test('a type with no per-event card id keeps exactly one card through a whole run', async () => {
  globalThis.localStorage = new MemoryStorage();
  const { createStartedHandler, createStatusAwareProgressHandler, createCompletionHandler } =
    await loadHandlers();
  const entry = await liftDatabaseResetEntry();
  const cards = newCardList();

  assert.equal(entry.getId, undefined, 'database_reset must not define a per-event card id');

  const onStarted = liftHandlerBuilder('buildStartedHandler', { createStartedHandler })(
    entry,
    entry.started,
    cards.setNotifications,
    cards.cancelAutoDismissTimer
  );
  const onProgress = liftHandlerBuilder('buildProgressHandler', {
    createStatusAwareProgressHandler
  })(
    entry,
    entry.progress,
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.cancelAutoDismissTimer
  );
  const onComplete = liftHandlerBuilder('buildCompleteHandler', { createCompletionHandler })(
    entry,
    cards.setNotifications,
    cards.scheduleAutoDismiss,
    cards.events?.current
  );

  onStarted({ operationId: 'operation-1', showNotification: true });
  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].id, entry.id);

  onProgress({ operationId: 'operation-1', percentComplete: 20, showNotification: true });
  onProgress({ operationId: 'operation-1', percentComplete: 60, showNotification: true });
  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].id, entry.id);

  // Still one card, still written to its key as a card rather than as a record of cards, which is
  // the shape the reload path has always read for this type.
  const saved = JSON.parse(globalThis.localStorage.getItem(entry.storageKey));
  assert.equal(saved.id, entry.id);
  assert.equal(saved.status, 'running');

  onComplete({ operationId: 'operation-1', success: true, showNotification: true });
  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].status, 'completed');
  assert.equal(globalThis.localStorage.getItem(entry.storageKey), null);
});

const dictionaries = Object.fromEntries(
  ['en', 'zh'].map((language) => [
    language,
    JSON.parse(
      readFileSync(new URL(`../src/i18n/locales/${language}.json`, import.meta.url), 'utf8')
    )
  ])
);
let language = 'en';
const translate = (key, values = {}) => {
  const text = key.split('.').reduce((value, part) => value?.[part], dictionaries[language]);
  return String(text ?? values.defaultValue ?? key).replace(/{{(\w+)}}/g, (_, name) =>
    String(values[name] ?? `{{${name}}}`)
  );
};
globalThis.notificationLifecycleI18n = {
  t: translate,
  language: 'en',
  exists: (key) =>
    typeof key.split('.').reduce((value, part) => value?.[part], dictionaries[language]) ===
    'string'
};
let lifecycleModules;
const loadLifecycle = async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  lifecycleModules ??= loadNotificationModules(
    moduleUrl('export default globalThis.notificationLifecycleI18n;')
  );
  return lifecycleModules;
};

const markPresented = liftHookCallback(
  'src/contexts/notifications/NotificationsContext.tsx',
  'useEffect',
  'terminal.presented'
);
const waitingSource = liftConstArrow(
  'src/contexts/notifications/useNotificationHandlers.ts',
  'waitingHandler'
);
const handoffSource = liftConstArrow(
  'src/contexts/notifications/useNotificationHandlers.ts',
  'waitingCompleteHandler'
);
const lookupSource = parseSource('src/contexts/notifications/useNotificationHandlers.ts');
const lookupDeclaration = findSoleNode(
  lookupSource,
  'wire lookup',
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'findEntryForWireType'
).getText(lookupSource);

const lifecycle = async () => {
  const modules = await loadLifecycle();
  const events = notificationEvents();
  const fixture = {
    state: [],
    updates: [],
    events,
    dismissals: [],
    cancelled: [],
    recoveries: 0,
    modules
  };
  const setNotifications = (update) => {
    fixture.state = typeof update === 'function' ? update(fixture.state) : update;
    fixture.updates.push(fixture.state);
    bindLifted(markPresented, {
      notifications: fixture.state,
      events,
      isTerminalNotificationStatus: modules.isTerminalNotificationStatus
    })();
  };
  const scheduleAutoDismiss = (id, delay) => fixture.dismissals.push([id, delay]);
  const cancelAutoDismissTimer = (id) => fixture.cancelled.push(id);
  const bindings = {
    ...modules,
    events,
    registry: modules.NOTIFICATION_REGISTRY,
    acknowledgedIds: events.current.acknowledgedIds,
    findEntryForWireType: bindLifted(`(${lookupDeclaration})`, modules),
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer,
    recover: () => {
      fixture.recoveries++;
    },
    i18n: globalThis.notificationLifecycleI18n
  };
  fixture.rebuild = () => {
    fixture.waiting = bindLifted(waitingSource, bindings);
    fixture.handoff = bindLifted(handoffSource, bindings);
  };
  fixture.rebuild();
  fixture.event = (type, phase, event) => {
    const entry = modules.NOTIFICATION_REGISTRY.find((candidate) => candidate.type === type);
    const handler =
      phase === 'started'
        ? modules.buildStartedHandler(
            entry,
            entry.started,
            setNotifications,
            cancelAutoDismissTimer,
            events.current,
            false,
            scheduleAutoDismiss
          )
        : phase === 'progress'
          ? modules.buildProgressHandler(
              entry,
              entry.progress,
              setNotifications,
              scheduleAutoDismiss,
              cancelAutoDismissTimer,
              events.current
            )
          : modules.buildCompleteHandler(
              entry,
              setNotifications,
              scheduleAutoDismiss,
              events.current
            );
    handler(event);
  };
  fixture.setNotifications = setNotifications;
  fixture.scheduleAutoDismiss = scheduleAutoDismiss;
  fixture.cancelAutoDismissTimer = cancelAutoDismissTimer;
  fixture.remove = bindLifted(
    liftHookCallback(
      'src/contexts/notifications/NotificationsContext.tsx',
      'useCallback',
      'cancelAutoDismissTimer(id)'
    ),
    {
      setNotifications,
      cancelAutoDismissTimer,
      events
    }
  );
  return fixture;
};

const queued = (operationId = 'W') => ({
  operationId,
  operationType: 'evictionScan',
  name: 'Eviction Scan',
  blockedByName: 'Cache File Scan'
});
const handoff = (extra = {}) => ({
  operationId: 'W',
  operationType: 'evictionScan',
  promoted: true,
  cancelled: false,
  nextOperationId: 'N',
  nextStatus: 'running',
  ...extra
});
const scan = (operationId = 'N', extra = {}) => ({
  operationId,
  showNotification: true,
  percentComplete: 43,
  stageKey: 'signalr.evictionScan.scanning',
  context: {},
  ...extra
});

for (const silent of [true, false]) {
  for (const order of ['SH', 'HS']) {
    test(`cache scan predecessor keeps one card through ${order}, silent=${silent}`, async () => {
      const f = await lifecycle();
      f.waiting({ ...queued(), operationType: 'cacheSizeScan', silent, acknowledge: false });
      const original = f.state[0];
      for (const phase of order) {
        if (phase === 'S')
          f.event('cache_size_scan', 'started', {
            operationId: 'N',
            previousOperationId: 'W',
            showNotification: !silent,
            stageKey: 'signalr.cacheSizeScan.starting'
          });
        else f.handoff(handoff({ operationType: 'cacheSizeScan' }));
        assert.equal(f.state.length, 1, JSON.stringify(f.state));
        assert.equal(f.state[0].id, original.id);
        assert.equal(f.state[0].startedAt, original.startedAt);
        assert.equal(f.state[0].details.operationId, 'N');
      }
      assert.ok(f.updates.every((state) => state.length === 1));
      assert.equal(f.state[0].message, 'Starting cache file scan...');
      f.event('cache_size_scan', 'started', {
        operationId: 'N',
        previousOperationId: 'W',
        showNotification: !silent
      });
      assert.equal(f.state.length, 1);
    });
  }
}

test('cache scan recovery merges its registered predecessor before committing', async () => {
  const f = await lifecycle();
  f.waiting({ ...queued(), operationType: 'cacheSizeScan', silent: true, acknowledge: false });
  const original = f.state[0];
  const run = f.modules.createRecoveryRunner(
    async (url) => {
      const body =
        url === '/api/operations/waiting'
          ? [{ ...queued(), operationType: 'cacheSizeScan', showNotification: false }]
          : url === '/api/cache/size/scan/status'
            ? {
                isProcessing: true,
                operationId: 'N',
                previousOperationId: 'W',
                showNotification: false,
                status: 'running',
                percentComplete: 12,
                stageKey: 'signalr.cacheSizeScan.scanning'
              }
            : undefined;
      return new Response(body === undefined ? null : JSON.stringify(body), {
        status: body === undefined ? 401 : 200
      });
    },
    f.setNotifications,
    f.scheduleAutoDismiss,
    f.events,
    () => f.state,
    f.cancelAutoDismissTimer
  );
  await run();
  assert.equal(f.state.length, 1, JSON.stringify(f.state));
  assert.equal(f.state[0].id, original.id);
  assert.equal(f.state[0].startedAt, original.startedAt);
  assert.equal(f.state[0].details.operationId, 'N');
  assert.equal(f.state[0].progress, 12);
  assert.ok(f.updates.every((state) => state.length === 1));
  f.waiting({ ...queued(), operationType: 'cacheSizeScan', silent: true, acknowledge: false });
  assert.equal(f.state.length, 1);
  assert.equal(f.state[0].details.operationId, 'N');
});

test('cache scan predecessor preserves terminal outcome and unrelated scan', async () => {
  const f = await lifecycle();
  f.event('cache_size_scan', 'started', { operationId: 'U', showNotification: false });
  const unrelated = f.state[0];
  f.waiting({ ...queued(), operationType: 'cacheSizeScan', silent: true, acknowledge: false });
  f.event('cache_size_scan', 'complete', {
    operationId: 'N',
    success: false,
    status: 'failed',
    error: 'Disk unavailable'
  });
  f.event('cache_size_scan', 'started', {
    operationId: 'N',
    previousOperationId: 'W',
    showNotification: false
  });
  assert.equal(f.state.length, 2);
  assert.equal(f.state[0], unrelated);
  assert.equal(f.state[1].details.operationId, 'N');
  assert.equal(f.state[1].status, 'failed');
  f.remove(f.state[1].id);
  f.event('cache_size_scan', 'started', {
    operationId: 'N',
    previousOperationId: 'W',
    showNotification: false
  });
  assert.deepEqual(f.state, [unrelated]);
});

test('recovery commits an exact missing-wait successor once when the updater is replayed', async () => {
  const f = await lifecycle();
  f.waiting(queued());
  const before = f.state[0];
  const urls = [];
  let commits = 0;
  const fetch = async (url) => {
    urls.push(url);
    const body =
      url === '/api/operations/waiting'
        ? []
        : url === '/api/operations/W'
          ? { id: 'W', active: false, nextOperationId: 'N', nextStatus: 'failed' }
          : url === '/api/operations/N'
            ? { id: 'N', active: false, status: 'failed', error: 'Disk unavailable' }
            : undefined;
    return new Response(body === undefined ? null : JSON.stringify(body), {
      status: body === undefined ? 401 : 200
    });
  };
  const run = f.modules.createRecoveryRunner(
    fetch,
    (update) => {
      commits++;
      const first = update(f.state);
      assert.equal(update(f.state), first);
      f.setNotifications(first);
    },
    f.scheduleAutoDismiss,
    f.events,
    () => f.state,
    f.cancelAutoDismissTimer
  );
  await run();
  assert.equal(commits, 1);
  assert.ok(urls.includes('/api/operations/W'));
  assert.ok(urls.includes('/api/operations/N'));
  assert.equal(f.state.length, 1);
  assert.equal(f.state[0].id, before.id);
  assert.equal(f.state[0].startedAt, before.startedAt);
  assert.equal(f.state[0].details.operationId, 'N');
  assert.equal(f.state[0].status, 'failed');
  assert.equal(f.dismissals.length, 1);
});

test('recovery coalesces concurrent triggers to one trailing pass', async () => {
  const f = await lifecycle();
  let release;
  let waitingRequests = 0;
  let commits = 0;
  const gate = new Promise((resolve) => {
    release = resolve;
  });
  const run = f.modules.createRecoveryRunner(
    async (url) => {
      if (url !== '/api/operations/waiting') return new Response(null, { status: 401 });
      waitingRequests++;
      if (waitingRequests === 1) await gate;
      return new Response('[]');
    },
    (update) => {
      commits++;
      f.setNotifications(update);
    },
    f.scheduleAutoDismiss,
    f.events,
    () => f.state
  );
  const first = run();
  assert.equal(run(), first);
  assert.equal(run(), first);
  release();
  await first;
  assert.equal(waitingRequests, 2);
  assert.equal(commits, 2);
});

test('a delayed waiting snapshot cannot resurrect a dismissed live terminal', async () => {
  const f = await lifecycle();
  f.waiting(queued());
  let release;
  const gate = new Promise((resolve) => {
    release = resolve;
  });
  const run = f.modules.createRecoveryRunner(
    async (url) => {
      if (url !== '/api/operations/waiting') return new Response(null, { status: 401 });
      await gate;
      return new Response(JSON.stringify([queued()]));
    },
    f.setNotifications,
    f.scheduleAutoDismiss,
    f.events,
    () => f.state
  );
  const running = run();
  f.handoff(handoff());
  f.event('eviction_scan', 'complete', scan('N', { status: 'completed', success: true }));
  f.remove(f.state[0].id);
  release();
  await running;
  assert.deepEqual(f.state, []);
});

for (const status of ['completed', 'failed', 'cancelled', 'skipped']) {
  for (const order of ['HSPC', 'SPHC', 'SPCH', 'CHSP', 'HCSP']) {
    test(`a ${status} successor retains the waiting slot through ${order}`, async () => {
      const f = await lifecycle();
      f.waiting(queued());
      const before = f.state[0];
      before.details.cancelRequested = true;
      before.details.cancelSent = true;
      for (const phase of order) {
        if (phase === 'H') f.handoff(handoff());
        if (phase === 'S') f.event('eviction_scan', 'started', scan());
        if (phase === 'P') f.event('eviction_scan', 'progress', scan());
        if (phase === 'C')
          f.event(
            'eviction_scan',
            'complete',
            scan('N', {
              status,
              success: status === 'completed' || status === 'skipped',
              cancelled: status === 'cancelled',
              error: status === 'failed' ? 'Scan disk error' : undefined
            })
          );
      }
      assert.equal(f.state.length, 1);
      const card = f.state[0];
      assert.equal(card.id, before.id);
      assert.equal(card.startedAt, before.startedAt);
      assert.equal(card.details.operationId, 'N');
      assert.equal(card.status, status);
      assert.equal(card.details.cancelRequested, true);
      assert.equal(f.dismissals.length, 1);
      const snapshot = JSON.stringify(f.state);
      f.rebuild();
      f.handoff(handoff());
      f.handoff(handoff({ nextOperationId: 'M' }));
      f.handoff(handoff({ nextOperationId: 'W' }));
      f.event('eviction_scan', 'progress', scan());
      f.event('eviction_scan', 'started', scan('W'));
      assert.equal(JSON.stringify(f.state), snapshot);
      f.remove(card.id);
      f.rebuild();
      f.event('eviction_scan', 'started', scan());
      f.waiting(queued());
      assert.deepEqual(f.state, []);
    });
  }
}

test('handoff before Waiting and a dismissed successor cannot recreate a running card', async () => {
  const f = await lifecycle();
  f.handoff(handoff());
  f.waiting(queued());
  assert.deepEqual(f.state, []);
  f.event('eviction_scan', 'complete', scan('N', { success: true, status: 'completed' }));
  const id = f.state[0].id;
  f.remove(id);
  f.handoff(handoff());
  f.event('eviction_scan', 'started', scan());
  assert.deepEqual(f.state, []);
});

test('an exact handoff cannot change an unrelated singleton or its persistence and timer', async () => {
  const f = await lifecycle();
  f.event('eviction_scan', 'started', scan('M'));
  f.event('eviction_scan', 'progress', scan('M', { percentComplete: 67 }));
  const card = f.state[0];
  const storageKey = f.modules.NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN;
  const persisted = localStorage.getItem(storageKey);
  const cancellations = f.cancelled.length;
  f.handoff(handoff());
  f.event('eviction_scan', 'complete', scan('N', { success: true, status: 'completed' }));
  assert.deepEqual(f.state, [card]);
  assert.equal(localStorage.getItem(storageKey), persisted);
  assert.equal(f.cancelled.length, cancellations);
  assert.deepEqual(f.dismissals, []);
});

test('equivalent target cards merge into the older slot and terminal dismissal stays final', async () => {
  const f = await lifecycle();
  f.waiting({ ...queued(), silent: true, acknowledge: false });
  const waiting = f.state[0];
  waiting.startedAt = new Date('2026-09-01T00:00:00Z');
  f.event('eviction_scan', 'started', scan());
  f.event('eviction_scan', 'complete', scan('N', { success: true, status: 'completed' }));
  assert.equal(f.state.length, 2);
  f.handoff(handoff());
  assert.equal(f.state.length, 1);
  assert.equal(f.state[0].id, waiting.id);
  assert.equal(f.state[0].startedAt, waiting.startedAt);
  assert.equal(f.state[0].status, 'completed');
  const g = await lifecycle();
  g.waiting({ ...queued(), silent: true, acknowledge: false });
  g.event('eviction_scan', 'complete', scan('N', { success: true, status: 'completed' }));
  g.remove(g.state.find((n) => n.details.operationId === 'N').id);
  g.handoff(handoff());
  assert.deepEqual(g.state, []);
});

for (const locale of ['en', 'zh']) {
  test(`correlated detection remains parent-owned and keeps separate errors in ${locale}`, async () => {
    language = locale;
    const f = await lifecycle();
    f.event('eviction_scan', 'started', scan('E', { showNotification: false }));
    const parent = f.state[0];
    const independent = {
      operationId: 'I',
      parentOperationId: null,
      showNotification: true,
      scanType: 'full',
      status: 'running',
      percentComplete: 20
    };
    f.event('game_detection', 'started', independent);
    const child = {
      ...independent,
      operationId: 'G',
      parentOperationId: 'E',
      showNotification: false
    };
    localStorage.setItem(
      f.modules.NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
      JSON.stringify({
        id: 'old-child',
        type: 'game_detection',
        status: 'running',
        details: { operationId: 'G' }
      })
    );
    for (const phase of ['started', 'progress', 'complete'])
      f.event('game_detection', phase, {
        ...child,
        success: false,
        status: phase === 'complete' ? 'failed' : 'running',
        error: 'Child disk error'
      });
    assert.equal(f.state.length, 2);
    assert.ok(f.state.every((card) => card.details.operationId !== 'G'));
    assert.equal(localStorage.getItem(f.modules.NOTIFICATION_STORAGE_KEYS.GAME_DETECTION), null);
    f.event(
      'eviction_scan',
      'progress',
      scan('E', { showNotification: false, context: { detectionError: 'Child disk error' } })
    );
    const active = f.state.find((card) => card.details.operationId === 'E');
    assert.equal(active.id, parent.id);
    assert.equal(active.startedAt, parent.startedAt);
    assert.equal(active.controlOnly, undefined);
    assert.equal(active.status, 'running');
    assert.equal(active.error, undefined);
    assert.match(active.detailMessage, /Child disk error/);
    assert.doesNotMatch(active.detailMessage, /signalr\.|{{/);
    f.event(
      'eviction_scan',
      'complete',
      scan('E', {
        status: 'failed',
        success: false,
        error: 'Parent Rust error',
        context: { detectionError: 'Child disk error' }
      })
    );
    const terminal = f.state.find((card) => card.details.operationId === 'E');
    assert.equal(terminal.error, 'Parent Rust error');
    assert.equal(terminal.message, 'Parent Rust error');
    assert.match(terminal.detailMessage, /Child disk error/);
    assert.doesNotMatch(terminal.detailMessage, /Parent Rust error/);
    assert.equal(f.modules.detectionErrorDetail({ error: 'Only parent' }), undefined);
    assert.equal(f.modules.detectionErrorDetail({ context: { detectionError: '' } }), undefined);
    language = 'en';
  });
}

test('corruption aggregate completion only terminalizes its captured operation', async () => {
  const f = await lifecycle();
  const event = {
    operationId: 'L',
    service: 'steam',
    detectionMethod: 'structural',
    showNotification: true,
    percentComplete: 25,
    context: {}
  };
  f.event('corruption_removal', 'started', event);
  const completion = {
    ...event,
    service: 'all',
    success: true,
    status: 'completed',
    context: { completedCount: 2, serviceCount: 2, files: 7 }
  };
  f.event('corruption_removal', 'complete', completion);
  assert.equal(f.state[0].status, 'completed');
  const dismissals = f.dismissals.length;
  f.event('corruption_removal', 'complete', completion);
  assert.equal(f.dismissals.length, dismissals);
  f.event('corruption_removal', 'started', { ...event, operationId: 'M' });
  const newer = f.state[0];
  f.event('corruption_removal', 'complete', completion);
  assert.deepEqual(f.state, [newer]);
  const g = await lifecycle();
  g.event('corruption_removal', 'complete', completion);
  assert.deepEqual(g.state, []);
  g.event('corruption_removal', 'started', event);
  assert.deepEqual(g.state, []);
});

test('bounded event bodies retain provider-lifetime terminal outcomes and active holds', async () => {
  const f = await lifecycle();
  f.events.current.held.set('held', 1);
  f.modules.rememberEvent(
    f.events.current,
    'eviction_scan',
    'complete',
    'EvictionScanComplete',
    scan('held', { success: true, status: 'completed' })
  );
  for (let index = 0; index < 150; index++)
    f.modules.rememberEvent(
      f.events.current,
      'eviction_scan',
      'complete',
      'EvictionScanComplete',
      scan(`terminal-${index}`, { success: false, status: 'failed', error: `error-${index}` })
    );
  assert.equal(f.events.current.records.size, 128);
  assert.ok(f.events.current.records.has('held'));
  assert.equal(f.events.current.terminals.get('terminal-0').error, 'error-0');
  assert.equal(
    f.modules.rememberEvent(
      f.events.current,
      'eviction_scan',
      'started',
      'EvictionScanStarted',
      scan('terminal-0')
    ),
    false
  );
});

test('the bar dismissal delay removes only the captured logical operation instance', () => {
  const source = liftConstArrow(
    'src/components/common/UniversalNotificationBar.tsx',
    'handleDismiss'
  );
  const run = (replace) => {
    const original = {
      id: 'singleton',
      instanceVersion: 7,
      startedAt: new Date('2026-09-08T10:00:00Z'),
      details: { operationId: 'operation-1' }
    };
    const notificationsRef = { current: [original] };
    const callbacks = [];
    const removed = [];
    let dismissingIds = new Set();
    const handleDismiss = bindLifted(source, {
      notificationsRef,
      setDismissingIds: (update) => {
        dismissingIds = update(dismissingIds);
      },
      setTimeout: (callback) => callbacks.push(callback),
      removeNotification: (id) => removed.push(id),
      NOTIFICATION_ANIMATION_DURATION_MS: 300
    });

    handleDismiss(original.id);
    assert.deepEqual([...dismissingIds], [original.id]);
    notificationsRef.current = [replace(original)];
    assert.equal(callbacks.length, 1);
    callbacks[0]();
    assert.deepEqual([...dismissingIds], []);
    return removed;
  };

  assert.deepEqual(
    run((original) => original),
    ['singleton']
  );
  assert.deepEqual(
    run((original) => ({
      ...original,
      details: { operationId: 'operation-2' }
    })),
    [],
    'a replacement operation reusing the singleton id survives the old callback'
  );
  assert.deepEqual(
    run((original) => ({
      ...original,
      instanceVersion: original.instanceVersion + 1
    })),
    [],
    'a newer logical instance of the same operation survives the old callback'
  );
});
