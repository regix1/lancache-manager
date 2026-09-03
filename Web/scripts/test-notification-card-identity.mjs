import assert from 'node:assert/strict';
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
 * A scheduled prefill run works several platforms at the same time, and each one gets its own
 * card. Every card in this app used to be its type's only card, so the thing that decides which
 * card an event lands on is the thing to test: the registry entry's own id function, driven
 * through the real handler factories with two platforms' events in sequence.
 *
 * The storage half matters just as much. One key holds every live card of the type, so a platform
 * that finishes must take its own card out of that key and leave its siblings there to be restored
 * after a reload.
 *
 * The compact bar is the third place identity decides something: it folds a service's cards onto
 * one line, which is right for a run reported twice and wrong for four platforms running at once.
 */

const REGISTRY_PATH = 'src/contexts/notifications/notificationRegistry.ts';
const HANDLERS_PATH = 'src/contexts/notifications/useNotificationHandlers.ts';
const BAR_PATH = 'src/components/common/UniversalNotificationBar.tsx';

/** Keeps the key and its values, so an assertion can name the service that produced a line. */
const i18nStub = {
  t: (key, params) => (params ? `${key}|${Object.values(params).join('|')}` : key)
};

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

const liftRegistryEntry = (type, bindings) => {
  const element = registryArray.elements.find((candidate) => typeOf(candidate) === type);
  assert.ok(element, `${type} is not in NOTIFICATION_REGISTRY`);
  return bindLifted(`() => (${element.getText(registryFile)})`, bindings)();
};

/** Source of a module-level `function <name>(...)` in the registry, ready to bind. */
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
  const sourceFile = parseSource(HANDLERS_PATH);
  const declaration = findSoleNode(
    sourceFile,
    `${name} declaration`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
  );
  return bindLifted(`(${declaration.getText(sourceFile)})`, bindings);
};

const loadHandlers = async () => {
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

const loadRegistryEntries = async () => {
  const constantsUrl = await compileToUrl('../src/contexts/notifications/constants.ts');
  const i18nUrl = moduleUrl('export default { t: (key) => key };');
  return await import(
    await compileToUrl('../src/contexts/notifications/registryEntries.ts', {
      './constants': constantsUrl,
      '@utils/stageKeyMessage': await compileToUrl('../src/utils/stageKeyMessage.ts', {
        '@/i18n': i18nUrl
      }),
      '@/i18n': i18nUrl
    })
  );
};

const loadConstants = async () =>
  await import(await compileToUrl('../src/contexts/notifications/constants.ts'));

/** The scheduled-prefill entry as it ships, with every free name it reads supplied. */
const liftScheduledPrefillEntry = async () => {
  const registryEntries = await loadRegistryEntries();
  const constants = await loadConstants();
  const { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } = await import(
    await compileToUrl(
      '../src/components/features/management/schedules/scheduled-prefill/constants.ts'
    )
  );
  const { translateStageKeyMessage } = await import(
    await compileToUrl('../src/utils/stageKeyMessage.ts', {
      '@/i18n': moduleUrl('export default globalThis.testI18n;')
    })
  );

  const scheduledPrefillCardId = registryFunction('scheduledPrefillCardId', {
    NOTIFICATION_IDS: constants.NOTIFICATION_IDS
  });
  const scheduledPrefillServiceLabel = registryFunction('scheduledPrefillServiceLabel', {
    i18n: i18nStub,
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY
  });
  const scheduledPrefillServiceMessage = registryFunction('scheduledPrefillServiceMessage', {
    i18n: i18nStub,
    translateStageKeyMessage,
    scheduledPrefillServiceLabel
  });

  const entry = liftRegistryEntry('scheduled_prefill', {
    buildStandardOperationEntry: registryEntries.buildStandardOperationEntry,
    visibleWhenNotSilent: registryEntries.visibleWhenNotSilent,
    translateStageKeyMessage,
    scheduledPrefillCardId,
    scheduledPrefillServiceLabel,
    scheduledPrefillServiceMessage,
    formatScheduledPrefillDetailMessage: (event) => `bytes:${event.bytesDownloaded ?? 0}`,
    NOTIFICATION_IDS: constants.NOTIFICATION_IDS,
    NOTIFICATION_STORAGE_KEYS: constants.NOTIFICATION_STORAGE_KEYS,
    GENERIC_FAILURE_I18N_KEY: constants.GENERIC_FAILURE_I18N_KEY,
    CANCEL_TOOLTIP: { scheduledPrefill: 'common.notifications.cancelScheduledPrefill' },
    i18n: i18nStub
  });
  return { entry, scheduledPrefillCardId, storageKey: entry.storageKey };
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

const progressEvent = (serviceId, operationId, message) => ({
  operationId,
  serviceId,
  stage: 'running',
  message,
  percentComplete: 12,
  showNotification: true
});

const driveScheduledPrefill = async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.testI18n = i18nStub;
  const { createStatusAwareProgressHandler, createCompletionHandler } = await loadHandlers();
  const { entry, scheduledPrefillCardId, storageKey } = await liftScheduledPrefillEntry();
  const cards = newCardList();

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
    cards.removeNotification
  );

  return { cards, entry, onProgress, onComplete, scheduledPrefillCardId, storageKey };
};

const persisted = (storageKey) => JSON.parse(globalThis.localStorage.getItem(storageKey) ?? 'null');

const recoveryFile = parseSource('src/contexts/notifications/recovery.ts');

/** Source of a module-level function in the recovery engine, which is all module-private. */
const recoveryFunction = (name, bindings) =>
  bindLifted(
    findSoleNode(
      recoveryFile,
      `${name} declaration`,
      (node) => ts.isFunctionDeclaration(node) && node.name?.getText(recoveryFile) === name
    ).getText(recoveryFile),
    bindings
  );

/** The recovery engine as it ships, over a run-status response the test writes. */
const liftRecovery = async () => {
  const constants = await loadConstants();
  const storageUrl = await compileToUrl('../src/utils/storage.ts');
  const { storage } = await import(storageUrl);

  const isSameOperation = recoveryFunction('isSameOperation', {});
  const mergeableDetails = recoveryFunction('mergeableDetails', {
    LIVE_ONLY_CANCEL_DETAIL_KEYS: constants.LIVE_ONLY_CANCEL_DETAIL_KEYS
  });
  const reconcileRecoveredCard = recoveryFunction('reconcileRecoveredCard', {
    isSameOperation,
    mergeableDetails
  });
  const { isTerminalNotificationStatus } = await import(
    await compileToUrl('../src/contexts/notifications/notificationStatus.ts')
  );
  return recoveryFunction('createSimpleRecoveryFunction', {
    storage,
    reconcileRecoveredCard,
    isTerminalNotificationStatus,
    i18n: i18nStub,
    FULL_PROGRESS_PERCENT: constants.FULL_PROGRESS_PERCENT
  });
};

test('a reload mid-run rebuilds a card for every service still running', async () => {
  const { cards, entry, scheduledPrefillCardId } = await driveScheduledPrefill();
  const createSimpleRecoveryFunction = await liftRecovery();

  const runStatus = {
    isRunning: true,
    operationId: 'operation-run',
    showNotification: true,
    services: [
      {
        serviceId: 'Steam',
        operationId: 'operation-steam',
        stage: 'running',
        message: 'Downloading a Steam game',
        percentComplete: 40
      },
      {
        serviceId: 'Epic',
        operationId: 'operation-epic',
        stage: 'running',
        message: 'Downloading an Epic game',
        percentComplete: 5
      }
    ]
  };

  await createSimpleRecoveryFunction(
    entry.recovery,
    entry.type,
    entry.id,
    entry.storageKey,
    async () => ({ ok: true, json: async () => runStatus }),
    cards.setNotifications,
    cards.scheduleAutoDismiss
  )();

  assert.deepEqual(
    cards.state.map((card) => card.id),
    [scheduledPrefillCardId('Steam'), scheduledPrefillCardId('Epic')]
  );
  assert.deepEqual(
    cards.state.map((card) => card.details.operationId),
    ['operation-steam', 'operation-epic']
  );
  assert.match(cards.state[0].message, /steam/);
  assert.match(cards.state[1].message, /epic/);
});

test('a recovery poll leaves the card of a service that already finished alone', async () => {
  const { cards, entry, onProgress, onComplete, scheduledPrefillCardId } =
    await driveScheduledPrefill();
  const createSimpleRecoveryFunction = await liftRecovery();

  onProgress(progressEvent('Steam', 'operation-steam', 'Downloading a Steam game'));
  onProgress(progressEvent('Epic', 'operation-epic', 'Downloading an Epic game'));

  // Epic finishes. Its card is terminal and sits for a few seconds before its dismiss timer
  // removes it, which is the whole window in which a person reads why a service was skipped.
  onComplete({
    operationId: 'operation-epic',
    serviceId: 'Epic',
    success: true,
    showNotification: true
  });

  // The tab is switched away and back, which re-runs recovery. Only Steam is still running, so
  // Epic is not in the response at all.
  await createSimpleRecoveryFunction(
    entry.recovery,
    entry.type,
    entry.id,
    entry.storageKey,
    async () => ({
      ok: true,
      json: async () => ({
        isRunning: true,
        operationId: 'operation-run',
        showNotification: true,
        services: [
          {
            serviceId: 'Steam',
            operationId: 'operation-steam',
            stage: 'running',
            message: 'Downloading a Steam game',
            percentComplete: 40
          }
        ]
      })
    }),
    cards.setNotifications,
    cards.scheduleAutoDismiss
  )();

  const epic = cards.state.find((card) => card.id === scheduledPrefillCardId('Epic'));
  assert.ok(epic, 'the finished service kept its card');
  assert.equal(epic.status, 'completed');
  assert.match(epic.message, /epic/);

  const steam = cards.state.find((card) => card.id === scheduledPrefillCardId('Steam'));
  assert.equal(steam.status, 'running');
  assert.equal(cards.state.length, 2);
});

test('a run that has finished stale-completes every card it left behind', async () => {
  const { cards, entry, onProgress, scheduledPrefillCardId } = await driveScheduledPrefill();
  const createSimpleRecoveryFunction = await liftRecovery();

  onProgress(progressEvent('Steam', 'operation-steam', 'Downloading a Steam game'));
  onProgress(progressEvent('Epic', 'operation-epic', 'Downloading an Epic game'));

  await createSimpleRecoveryFunction(
    entry.recovery,
    entry.type,
    entry.id,
    entry.storageKey,
    async () => ({ ok: true, json: async () => ({ isRunning: false, services: [] }) }),
    cards.setNotifications,
    cards.scheduleAutoDismiss
  )();

  // Each card keeps its own id rather than collapsing onto the type's, and each is dismissed.
  assert.deepEqual(
    cards.state.map((card) => [card.id, card.status]),
    [
      [scheduledPrefillCardId('Steam'), 'completed'],
      [scheduledPrefillCardId('Epic'), 'completed']
    ]
  );
  assert.deepEqual(
    cards.dismissals.map(([id]) => id).filter((id) => id.startsWith(entry.id)),
    [scheduledPrefillCardId('Steam'), scheduledPrefillCardId('Epic'), entry.id]
  );
});

test('two services running at once each get their own card', async () => {
  const { cards, onProgress, scheduledPrefillCardId } = await driveScheduledPrefill();

  onProgress(progressEvent('Steam', 'operation-steam', 'Downloading a Steam game'));
  onProgress(progressEvent('Epic', 'operation-epic', 'Downloading an Epic game'));

  assert.equal(cards.state.length, 2);
  assert.deepEqual(
    cards.state.map((card) => card.id),
    [scheduledPrefillCardId('Steam'), scheduledPrefillCardId('Epic')]
  );

  // Each card keeps its own service in its own line: neither overwrote the other.
  const [steam, epic] = cards.state;
  assert.match(steam.message, /steam/);
  assert.match(epic.message, /epic/);
  assert.doesNotMatch(steam.message, /epic/);

  // The X on a card cancels the operation that card names, so the ids must not be shared.
  assert.equal(steam.details.operationId, 'operation-steam');
  assert.equal(epic.details.operationId, 'operation-epic');
});

test('both cards are persisted under the one key so a reload restores them all', async () => {
  const { onProgress, scheduledPrefillCardId, storageKey } = await driveScheduledPrefill();

  onProgress(progressEvent('Steam', 'operation-steam', 'Downloading a Steam game'));
  onProgress(progressEvent('Epic', 'operation-epic', 'Downloading an Epic game'));

  assert.deepEqual(Object.keys(persisted(storageKey)), [
    scheduledPrefillCardId('Steam'),
    scheduledPrefillCardId('Epic')
  ]);
});

test('one service finishing leaves the other running, on screen and in storage', async () => {
  const { cards, onProgress, onComplete, scheduledPrefillCardId, storageKey } =
    await driveScheduledPrefill();

  onProgress(progressEvent('Steam', 'operation-steam', 'Downloading a Steam game'));
  onProgress(progressEvent('Epic', 'operation-epic', 'Downloading an Epic game'));

  onComplete({
    operationId: 'operation-steam',
    serviceId: 'Steam',
    success: true,
    showNotification: true
  });

  const steam = cards.state.find((card) => card.id === scheduledPrefillCardId('Steam'));
  const epic = cards.state.find((card) => card.id === scheduledPrefillCardId('Epic'));
  assert.equal(steam.status, 'completed');
  assert.match(steam.message, /steam/);
  assert.equal(epic.status, 'running');
  assert.match(epic.message, /epic/);

  // The finished service's card is the only one cleared: the key still holds the running one.
  assert.deepEqual(Object.keys(persisted(storageKey)), [scheduledPrefillCardId('Epic')]);
});

test('a service that skipped closes as skipped and keeps the line saying why', async () => {
  const { cards, onProgress, onComplete, scheduledPrefillCardId, storageKey } =
    await driveScheduledPrefill();

  onProgress({
    operationId: 'operation-steam',
    serviceId: 'Steam',
    stage: 'skipped',
    message: 'No running persistent container',
    percentComplete: 1,
    showNotification: true
  });
  onProgress(progressEvent('Epic', 'operation-epic', 'Downloading an Epic game'));

  const skipLine = cards.state.find((card) => card.id === scheduledPrefillCardId('Steam')).message;
  assert.match(skipLine, /events\.skipped\|.*steam/);

  onComplete({
    operationId: 'operation-steam',
    serviceId: 'Steam',
    success: true,
    error: 'No running persistent container',
    status: 'skipped',
    showNotification: true
  });

  const steam = cards.state.find((card) => card.id === scheduledPrefillCardId('Steam'));
  assert.equal(steam.status, 'skipped');
  assert.equal(steam.message, skipLine);
  assert.doesNotMatch(steam.message, /completed|failed/);
  assert.deepEqual(Object.keys(persisted(storageKey)), [scheduledPrefillCardId('Epic')]);
});

test('the run-level start and terminal open and close no card of their own', async () => {
  const { cards, entry, onComplete } = await driveScheduledPrefill();

  assert.equal(entry.started.shouldDisplay({ showNotification: true, serviceId: null }), false);
  assert.equal(entry.complete.shouldDisplay({ showNotification: true, serviceId: null }), false);
  assert.equal(entry.started.shouldDisplay({ showNotification: true, serviceId: 'Steam' }), true);

  onComplete({ operationId: 'operation-run', serviceId: null, success: true });
  assert.deepEqual(cards.state, []);
});

// ── The compact bar's grouping ──────────────────────────────────────────────

const barFile = parseSource(BAR_PATH, ts.ScriptKind.TSX);

/** The shipped `groupKey` expression, called with the item and the set it reads. */
const groupKeyFor = () => {
  const declaration = findSoleNode(
    barFile,
    'groupKey declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(barFile) === 'groupKey'
  );
  return bindLifted(
    `(item, TYPES_WITH_A_CARD_PER_ENTITY) => (${declaration.initializer.getText(barFile)})`,
    {}
  );
};

/** The types whose entry computes a card id per event, read off the registry the app ships. */
const typesWithACardPerEntity = () =>
  new Set(
    registryArray.elements
      .filter((element) => {
        const object = entryObject(element);
        return (
          ts.isObjectLiteralExpression(object) &&
          object.properties.some(
            (property) =>
              ts.isPropertyAssignment(property) && property.name.getText(registryFile) === 'getId'
          )
        );
      })
      .map((element) => typeOf(element))
  );

test('the compact bar gives each service card its own line', () => {
  const groupKey = groupKeyFor();
  const perEntity = typesWithACardPerEntity();

  // Only scheduled prefill owns several cards today, so only its grouping changes.
  assert.deepEqual([...perEntity], ['scheduled_prefill']);

  const steam = {
    serviceKey: 'scheduledPrefill',
    notification: { type: 'scheduled_prefill', id: 'notification_scheduled_prefill_Steam' }
  };
  const epic = {
    serviceKey: 'scheduledPrefill',
    notification: { type: 'scheduled_prefill', id: 'notification_scheduled_prefill_Epic' }
  };

  assert.notEqual(groupKey(steam, perEntity), groupKey(epic, perEntity));
});

test('a service reported twice still folds onto one line', () => {
  const groupKey = groupKeyFor();
  const perEntity = typesWithACardPerEntity();

  // The pair the grouping was written for: an eviction scan's own card and the toast answering the
  // Run Now click that started it. Two notifications, two types, one service, one line.
  const runCard = {
    serviceKey: 'cacheReconciliation',
    notification: { type: 'eviction_scan', id: 'notification_eviction_scan' }
  };
  const acknowledgment = {
    serviceKey: 'cacheReconciliation',
    notification: { type: 'generic', id: 'generic_Started_Eviction_Scan' }
  };

  assert.equal(groupKey(runCard, perEntity), groupKey(acknowledgment, perEntity));
  assert.equal(groupKey(runCard, perEntity), 'svc:cacheReconciliation');

  // A notification with no service of its own keeps a line to itself, as it always did.
  const loose = { serviceKey: undefined, notification: { type: 'generic', id: 'generic_hello' } };
  assert.equal(groupKey(loose, perEntity), 'id:generic_hello');
});
