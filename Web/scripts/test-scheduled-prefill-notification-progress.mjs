import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  MemoryStorage,
  notificationEvents,
  liftHookCallback,
  bindLifted,
  compileToUrl,
  findSoleNode,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

const REGISTRY_PATH = 'src/contexts/notifications/notificationRegistry.ts';
const HANDLERS_PATH = 'src/contexts/notifications/handlers.ts';
const registryFile = parseSource(REGISTRY_PATH);

const i18n = {
  t: (key, values) =>
    values
      ? `${key}:${Object.entries(values)
          .map(([name, value]) => `${name}=${value}`)
          .join(',')}`
      : key
};

const registryFunction = (name, bindings) =>
  bindLifted(
    findSoleNode(
      registryFile,
      `${name} declaration`,
      (node) => ts.isFunctionDeclaration(node) && node.name?.getText(registryFile) === name
    ).getText(registryFile),
    bindings
  );

const liftHandlerBuilder = (name, bindings) => {
  const file = parseSource(HANDLERS_PATH);
  const declaration = findSoleNode(
    file,
    `${name} declaration`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
  );
  return bindLifted(`(${declaration.getText(file)})`, bindings);
};

const loadScheduledEntry = async () => {
  const constants = await import(await compileToUrl('../src/contexts/notifications/constants.ts'));
  const registryEntries = await import(
    await compileToUrl('../src/contexts/notifications/registryEntries.ts', {
      './constants': await compileToUrl('../src/contexts/notifications/constants.ts'),
      '@/i18n': moduleUrl('export default globalThis.notificationI18n;'),
      '@utils/stageKeyMessage': await compileToUrl('../src/utils/stageKeyMessage.ts', {
        '@/i18n': moduleUrl('export default globalThis.notificationI18n;')
      })
    })
  );
  const { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } = await import(
    await compileToUrl(
      '../src/components/features/management/schedules/scheduled-prefill/constants.ts'
    )
  );
  const { translateStageKeyMessage, hasUnresolvedInterpolation } = await import(
    await compileToUrl('../src/utils/stageKeyMessage.ts', {
      '@/i18n': moduleUrl('export default globalThis.notificationI18n;')
    })
  );
  const cardId = registryFunction('scheduledPrefillCardId', {
    NOTIFICATION_IDS: constants.NOTIFICATION_IDS
  });
  const serviceLabel = registryFunction('scheduledPrefillServiceLabel', {
    i18n,
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY
  });
  const sentence = registryFunction('scheduledPrefillSentence', {
    translateStageKeyMessage,
    hasUnresolvedInterpolation
  });
  const message = registryFunction('scheduledPrefillServiceMessage', {
    i18n,
    translateStageKeyMessage,
    scheduledPrefillSentence: sentence,
    scheduledPrefillServiceLabel: serviceLabel
  });
  const registryArray = findSoleNode(
    registryFile,
    'NOTIFICATION_REGISTRY declaration',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(registryFile) === 'NOTIFICATION_REGISTRY' &&
      node.initializer !== undefined &&
      ts.isArrayLiteralExpression(node.initializer)
  ).initializer;
  const element = registryArray.elements.find((candidate) =>
    candidate.getText(registryFile).includes("type: 'scheduled_prefill'")
  );
  assert.ok(element, 'scheduled prefill registry entry exists');
  return bindLifted(`() => (${element.getText(registryFile)})`, {
    buildStandardOperationEntry: registryEntries.buildStandardOperationEntry,
    visibleWhenNotSilent: registryEntries.visibleWhenNotSilent,
    scheduledPrefillCardId: cardId,
    scheduledPrefillServiceLabel: serviceLabel,
    scheduledPrefillServiceMessage: message,
    scheduledPrefillDetails: registryFunction('scheduledPrefillDetails', {}),
    formatScheduledPrefillDetailMessage: (event) => `bytes:${event.bytesDownloaded}`,
    NOTIFICATION_IDS: constants.NOTIFICATION_IDS,
    NOTIFICATION_STORAGE_KEYS: constants.NOTIFICATION_STORAGE_KEYS,
    GENERIC_FAILURE_I18N_KEY: constants.GENERIC_FAILURE_I18N_KEY,
    CANCEL_TOOLTIP: { scheduledPrefill: 'cancel' },
    i18n
  })();
};

const liveEvent = (percentComplete, game, bytesDownloaded) => ({
  operationId: 'operation-steam',
  serviceId: 'Steam',
  stage: 'running',
  stageKey:
    percentComplete === null
      ? 'signalr.scheduledPrefill.downloadingGameUnknownTotal'
      : 'signalr.scheduledPrefill.downloadingGame',
  stageContext: percentComplete === null ? { game } : { game, completed: 1, total: 3 },
  message: `Downloading ${game}`,
  percentComplete,
  bytesDownloaded,
  showNotification: true
});

test('scheduled progress clears stale percent when the daemon total becomes unknown', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  globalThis.notificationI18n = i18n;
  const entry = await loadScheduledEntry();
  const { createStatusAwareProgressHandler } = await import(
    await compileToUrl('../src/contexts/notifications/handlers.ts', {
      './constants': await compileToUrl('../src/contexts/notifications/constants.ts'),
      './notificationStatus': await compileToUrl(
        '../src/contexts/notifications/notificationStatus.ts'
      ),
      '@utils/storage': await compileToUrl('../src/utils/storage.ts'),
      '@/i18n': moduleUrl('export default globalThis.notificationI18n;')
    })
  );
  const cards = { state: [] };
  const onProgress = liftHandlerBuilder('buildProgressHandler', {
    createStatusAwareProgressHandler
  })(
    entry,
    entry.progress,
    (update) => {
      cards.state = update(cards.state);
    },
    () => undefined,
    () => undefined
  );

  onProgress(liveEvent(1, 'First game', 128));
  onProgress(liveEvent(null, 'Second game', 512));

  assert.equal(cards.state.length, 1);
  assert.equal(cards.state[0].progress, undefined);
  assert.match(cards.state[0].message, /Second game/);
  assert.equal(cards.state[0].detailMessage, 'bytes:512');

  const recovered = entry.recovery.recoverCards({
    services: [{ ...liveEvent(null, 'Second game', 512), isRunning: true }]
  });
  assert.equal(recovered[0].progress, undefined);
  assert.match(recovered[0].message, /Second game/);
});

test('scheduled reconnect preserves both cards and rejects replayed progress and recovery snapshots', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  globalThis.notificationI18n = i18n;
  const entry = await loadScheduledEntry();
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const statusUrl = await compileToUrl('../src/contexts/notifications/notificationStatus.ts');
  const handlers = await import(
    await compileToUrl('../src/contexts/notifications/handlers.ts', {
      './constants': constantsUrl,
      './notificationStatus': statusUrl,
      '@utils/storage': await compileToUrl('../src/utils/storage.ts'),
      '@/i18n': moduleUrl('export default globalThis.notificationI18n;')
    })
  );
  let cards = [];
  const setNotifications = (update) => {
    cards = update(cards);
  };
  const events = notificationEvents().current;
  const progress = liftHandlerBuilder('buildProgressHandler', handlers)(
    entry,
    entry.progress,
    setNotifications,
    () => undefined,
    () => undefined,
    events
  );
  const first = {
    ...liveEvent(42, 'First game', 420),
    eventEpoch: 'epoch-a',
    eventSequence: 10,
    daemonInstanceId: 'daemon'
  };
  const second = {
    ...liveEvent(31, 'Second game', 310),
    operationId: 'operation-two',
    eventEpoch: 'epoch-b',
    eventSequence: 20,
    daemonInstanceId: 'daemon'
  };
  progress(first);
  progress(second);
  const initial = [...cards];
  const disconnect = bindLifted(
    liftHookCallback(
      'src/contexts/notifications/NotificationsContext.tsx',
      'useEffect',
      'if (signalR.isConnected) return;'
    ),
    {
      signalR: { isConnected: false },
      setNotifications,
      events: { current: events },
      isTerminalNotificationStatus: (await import(statusUrl)).isTerminalNotificationStatus
    }
  );
  disconnect();
  assert.deepEqual(
    cards.map((card) => card.id),
    initial.map((card) => card.id)
  );
  assert.equal(
    cards.every((card) => card.details.connectionRecovering),
    true
  );
  assert.deepEqual(
    cards.map((card) => card.message),
    initial.map((card) => card.message)
  );
  const sibling = cards[1];
  progress({
    ...first,
    eventSequence: 11,
    stage: 'recovering',
    stageKey: null,
    message: 'Reconnecting',
    percentComplete: null,
    bytesDownloaded: null
  });
  assert.equal(cards[0].message, initial[0].message);
  assert.equal(cards[0].progress, 42);
  assert.equal(cards[0].detailMessage, initial[0].detailMessage);
  assert.equal(cards[1], sibling);
  const recovering = cards[0];
  for (const event of [
    first,
    { ...first, eventSequence: 11 },
    { ...first, eventSequence: 100, daemonInstanceId: 'old-daemon' }
  ])
    progress(event);
  assert.equal(cards[0], recovering);

  const source = parseSource('src/contexts/notifications/recovery.ts');
  const lift = (name, bindings) =>
    bindLifted(
      findSoleNode(
        source,
        name,
        (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
      )
        .getText(source)
        .replace(/^export /, ''),
      bindings
    );
  const reconcile = lift('reconcileRecoveredCard', {
    isSameOperation: lift('isSameOperation', {}),
    mergeableDetails: lift('mergeableDetails', {
      LIVE_ONLY_CANCEL_DETAIL_KEYS: (await import(constantsUrl)).LIVE_ONLY_CANCEL_DETAIL_KEYS
    }),
    isTerminalNotificationStatus: (await import(statusUrl)).isTerminalNotificationStatus
  });
  assert.equal(reconcile(recovering, initial[0]), recovering);
  const recovered = reconcile(recovering, {
    ...recovering,
    details: { ...recovering.details, eventSequence: 12, recovering: false }
  });
  assert.equal(recovered.id, recovering.id);
  assert.equal(recovered.message, recovering.message);
  assert.equal(recovered.progress, recovering.progress);
  assert.equal(recovered.details.recovering, false);
  assert.equal(reconcile(recovered, { ...recovered }), recovered);
  progress({ ...first, eventSequence: 12, bytesDownloaded: 450 });
  assert.equal(cards[0].details.recovering, false);
  assert.equal(cards[1], sibling);

  const constants = await import(constantsUrl);
  const recover = lift('createSimpleRecoveryFunction', {
    ...handlers,
    canRecover: lift('canRecover', {}),
    reconcileRecoveredCard: reconcile,
    NOTIFICATION_REGISTRY: [entry],
    i18n,
    isTerminalNotificationStatus: (await import(statusUrl)).isTerminalNotificationStatus,
    FULL_PROGRESS_PERCENT: constants.FULL_PROGRESS_PERCENT,
    GENERIC_FAILURE_I18N_KEY: constants.GENERIC_FAILURE_I18N_KEY
  });
  const pass = () => ({
    startedAt: new Date(),
    revision: events.revision,
    starting: [...cards],
    events,
    connectionGeneration: events.connectionGeneration,
    changed: new Set(),
    cancelAutoDismissTimer: () => undefined
  });
  const read = (response, captured = pass()) =>
    recover(
      entry.recovery,
      entry.type,
      entry.id,
      entry.storageKey,
      async () => ({ ok: true, json: async () => response }),
      setNotifications,
      () => undefined,
      captured
    )();
  const originalId = cards[0].id;
  const candidate = {
    ...first,
    eventEpoch: 'epoch-new',
    eventSequence: 2,
    stageContext: { game: 'Restored game', completed: 1, total: 3 }
  };
  progress(candidate);
  assert.equal(cards[0].details.eventEpoch, 'epoch-a');
  assert.equal(events.pending.get(first.operationId).body.eventSequence, 2);
  await read({
    isRunning: true,
    services: [{ ...first, eventEpoch: 'epoch-new', eventSequence: 1 }]
  });
  assert.equal(cards[0].id, originalId);
  assert.equal(cards[0].details.eventEpoch, 'epoch-new');
  assert.equal(cards[0].details.eventSequence, 2);
  assert.match(cards[0].message, /Restored game/);
  assert.equal(events.pending.size, 0);
  const adopted = cards[0];
  progress({ ...first, eventSequence: 999 });
  progress({ ...first, eventEpoch: undefined, eventSequence: undefined });
  assert.equal(cards[0], adopted);

  const oldPass = pass();
  events.connectionGeneration++;
  await read({ isRunning: false, services: [] }, oldPass);
  assert.equal(cards[0], adopted);
  const runner = lift('createRecoveryRunner', {
    createSimpleRecoveryFunction: recover,
    createWaitingOperationsRecoveryFunction: () => async () => undefined,
    NOTIFICATION_REGISTRY: [entry]
  });
  let release;
  const response = new Promise((resolve) => {
    release = resolve;
  });
  const runRecovery = runner(
    async () => response,
    setNotifications,
    () => undefined,
    { current: events },
    () => cards
  );
  const request = runRecovery();
  progress({ ...candidate, eventSequence: 3 });
  const beforeEmpty = cards[0];
  release({ ok: true, json: async () => ({ isRunning: false, services: [] }) });
  await request;
  assert.equal(cards[0], beforeEmpty);
  for (let attempt = 0; attempt < 3; attempt++) {
    await recover(
      entry.recovery,
      entry.type,
      entry.id,
      entry.storageKey,
      async () => {
        throw new Error('connection unavailable');
      },
      setNotifications,
      () => undefined,
      pass()
    )();
  }
  assert.equal(cards.length, 2);
  assert.equal(
    cards.every((card) => card.type === 'scheduled_prefill' && card.details.connectionRecovering),
    true
  );

  const complete = handlers.buildCompleteHandler(entry, setNotifications, () => undefined, events);
  complete({
    ...candidate,
    eventEpoch: 'epoch-terminal',
    eventSequence: 3,
    success: true,
    status: 'completed'
  });
  progress({ ...candidate, eventEpoch: 'epoch-terminal', eventSequence: 4 });
  assert.equal(events.pending.get(first.operationId).phase, 'complete');
  await read({
    isRunning: true,
    services: [{ ...candidate, eventEpoch: 'epoch-terminal', eventSequence: 1 }]
  });
  assert.equal(cards[0].status, 'completed');
  const terminal = cards[0];
  progress({ ...candidate, eventEpoch: 'epoch-terminal', eventSequence: 99 });
  assert.equal(cards[0], terminal);
});

test('scheduled announcements throttle ordinary text and announce lifecycle transitions once', () => {
  const source = parseSource(
    'src/components/common/UnifiedNotificationItem.tsx',
    ts.ScriptKind.TSX
  );
  const declaration = findSoleNode(
    source,
    'announcement hook',
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'useNotificationAnnouncement'
  );
  let now = 10000;
  let state;
  let ref;
  const announced = [];
  const hook = bindLifted(declaration.getText(source), {
    useTranslation: () => ({ t: (key) => key }),
    useState: (initial) => {
      state ??= initial;
      return [
        state,
        (value) => {
          state = value;
          announced.push(value);
        }
      ];
    },
    useRef: (initial) => (ref ??= { current: initial }),
    useEffect: (effect) => effect(),
    Date: { now: () => now },
    ANNOUNCEMENT_MIN_INTERVAL_MS: 5000,
    isTerminalNotificationStatus: (status) =>
      ['completed', 'failed', 'cancelled', 'skipped'].includes(status)
  });
  const card = {
    type: 'scheduled_prefill',
    status: 'running',
    message: 'Game one',
    progress: 10,
    details: {}
  };
  hook(card);
  for (let index = 0; index < 25; index++) {
    now += 100;
    hook({ ...card, message: `Game ${index}`, progress: 10 + index / 10 });
  }
  assert.equal(announced.length, 0);
  now = 15000;
  hook({ ...card, message: 'Latest game', progress: 30 });
  assert.equal(announced.length, 1);
  const recovering = { ...card, message: 'Latest game', details: { recovering: true } };
  hook(recovering);
  assert.equal(announced.length, 2);
  for (let index = 0; index < 25; index++) hook(recovering);
  assert.equal(announced.length, 2);
  hook({ ...recovering, status: 'cancelling' });
  assert.equal(announced.length, 3);
  assert.match(announced[2], /prefill.progress.cancelling/);
  const terminal = { ...card, message: 'Finished', status: 'completed' };
  hook(terminal);
  hook(terminal);
  assert.equal(announced.length, 4);

  for (const status of ['completed', 'failed', 'cancelled', 'skipped']) {
    for (const progressAriaValueText of [undefined, 'Everything was already cached']) {
      state = undefined;
      ref = undefined;
      announced.length = 0;
      const running = {
        ...card,
        message: 'Everything was already cached',
        progressAriaValueText
      };
      hook(running);
      now += 5000;
      hook({ ...running });
      assert.equal(announced.length, 0, 'ordinary same-text progress remains silent');
      hook({ ...running, status });
      assert.equal(announced.length, 1, 'same-text terminal announces once');
      assert.equal(announced[0], `prefill.runs.${status} Everything was already cached`);
      for (let index = 0; index < 25; index++) {
        now += 5000;
        hook({ ...running, status });
      }
      assert.equal(announced.length, 1, 'duplicate terminal remains silent');
    }
  }
});
