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

const REGISTRY_PATH = 'src/contexts/notifications/notificationRegistry.ts';
const HANDLERS_PATH = 'src/contexts/notifications/useNotificationHandlers.ts';
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
