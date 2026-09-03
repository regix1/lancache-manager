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
 * Every notification type shares one set of handler factories, and the per-event card id that
 * scheduled prefill needs runs through all of them. A type that does not ask for it must keep the
 * one card it has always had, and must keep persisting that card on its own rather than inside a
 * record of cards. Database reset stands in for the eighteen types in that position: it has all
 * three lifecycle phases, so a started, a progress and a terminal event all pass through here.
 */

const REGISTRY_PATH = 'src/contexts/notifications/notificationRegistry.ts';
const HANDLERS_PATH = 'src/contexts/notifications/useNotificationHandlers.ts';

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
  cards.removeNotification = () => undefined;
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
    cards.removeNotification
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
