import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import i18next from 'i18next';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * A scheduled prefill writes most of what a reader watches: the skip reason, the progress line, the
 * completion. All of it used to travel as an English sentence the browser printed as it arrived, so
 * a Chinese operator watched an English card the whole way through. The backend now names each of
 * those sentences with a key beside the English, and the two roads that carry them - the refusal
 * body through `getErrorMessage`, the notification through the registry - are checked here against
 * the real locale files.
 */

const localeFile = (name) =>
  JSON.parse(readFileSync(new URL(`../src/i18n/locales/${name}.json`, import.meta.url), 'utf8'));

const en = localeFile('en');
const zh = localeFile('zh');

const translator = i18next.createInstance();
await translator.init({
  resources: { en: { translation: en }, zh: { translation: zh } },
  lng: 'en',
  fallbackLng: 'en',
  supportedLngs: ['en', 'zh'],
  interpolation: { escapeValue: false }
});

globalThis.testTranslator = translator;
const i18nStub = moduleUrl('export default globalThis.testTranslator;');

const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});

const { buildApiError } = await import(apiErrorUrl);

const { getErrorMessage } = await import(
  await compileToUrl('../src/utils/error.ts', {
    '@/i18n': i18nStub,
    '../services/apiError': apiErrorUrl
  })
);

const { translateStageKeyMessage } = await import(
  await compileToUrl('../src/utils/stageKeyMessage.ts', { '@/i18n': i18nStub })
);

const { SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY } = await import(
  await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/constants.ts'
  )
);

/** The refusal body the API wrote, read back the way a screen reads it. */
const shownFor = async (body, language) => {
  await translator.changeLanguage(language);
  return getErrorMessage(
    await buildApiError({
      status: body.statusCode ?? 409,
      statusText: 'Conflict',
      text: async () => JSON.stringify(body)
    })
  );
};

// ---------------------------------------------------------------------------
// The card's own wording, lifted out of the registry so the test drives the arrow that ships
// ---------------------------------------------------------------------------

const registryFile = parseSource('src/contexts/notifications/notificationRegistry.ts');

const arrowFor = (label, marker) =>
  findSoleNode(
    registryFile,
    label,
    (node) =>
      ts.isPropertyAssignment(node) &&
      ts.isArrowFunction(node.initializer) &&
      node.initializer.getText(registryFile).includes(marker)
  ).initializer.getText(registryFile);

/** Source of a module-level `function <name>(...)`, which lifts like an arrow does. */
const functionFor = (name) =>
  findSoleNode(
    registryFile,
    `${name} declaration`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.getText(registryFile) === name
  ).getText(registryFile);

const scheduledPrefillServiceLabel = bindLifted(functionFor('scheduledPrefillServiceLabel'), {
  i18n: translator,
  SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY
});

// The progress event and the run-status response describe a service the same way, so the card's
// wording lives in one function both compose from.
const scheduledPrefillMessage = bindLifted(functionFor('scheduledPrefillServiceMessage'), {
  i18n: translator,
  translateStageKeyMessage,
  scheduledPrefillServiceLabel
});

const scheduledPrefillFailure = bindLifted(
  arrowFor('scheduled prefill getFailureMessage', 'scheduledPrefill.events.failed'),
  {
    i18n: translator,
    translateStageKeyMessage,
    scheduledPrefillServiceLabel,
    GENERIC_FAILURE_I18N_KEY: 'signalr.generic.failed'
  }
);

const cardFor = async (event, language) => {
  await translator.changeLanguage(language);
  return scheduledPrefillMessage(event);
};

// ---------------------------------------------------------------------------

test('a prefill refused because the container is still going down reads in the reader language', async () => {
  const body = {
    error:
      'An existing persistent steam container is still being removed or restarting. Please try again shortly.',
    stageKey: 'errors.prefill.containerBusy',
    context: { service: 'steam' }
  };

  assert.equal(
    await shownFor(body, 'en'),
    'An existing persistent steam container is still being removed or restarting. Please try again shortly.'
  );
  assert.equal(await shownFor(body, 'zh'), '现有的 steam 持久容器仍在移除或重启中。请稍后重试。');
});

test('the ban refusal reads in the reader language', async () => {
  const body = {
    statusCode: 403,
    error: 'You are banned from using the prefill feature.',
    stageKey: 'errors.prefill.banned'
  };

  assert.equal(await shownFor(body, 'en'), 'You are banned from using the prefill feature.');
  assert.equal(await shownFor(body, 'zh'), '您已被禁止使用预填充功能。');
});

test('a run skipped because a prefill is already going says so in the reader language', async () => {
  const event = {
    serviceId: 'Steam',
    stage: 'skipped',
    message: 'A prefill is already in progress',
    stageKey: 'signalr.scheduledPrefill.skippedAlreadyRunning'
  };

  assert.equal(await cardFor(event, 'en'), 'Steam skipped: A prefill is already in progress');
  assert.equal(await cardFor(event, 'zh'), 'Steam 已跳过：预填充已在进行中');
});

test('the game being downloaded and its position in the run survive the translation', async () => {
  const event = {
    serviceId: 'Steam',
    stage: 'running',
    message: 'Downloading Team Fortress 2 (2 of 5 games)',
    stageKey: 'signalr.scheduledPrefill.downloadingGame',
    stageContext: { game: 'Team Fortress 2', completed: 2, total: 5 }
  };

  assert.equal(await cardFor(event, 'en'), 'Steam: Downloading Team Fortress 2 (2 of 5 games)');
  assert.equal(await cardFor(event, 'zh'), 'Steam：正在下载 Team Fortress 2（2/5 个游戏）');
});

test('a needs-login skip keeps the prerequisite it named, in the reader language', async () => {
  const event = {
    serviceId: 'Steam',
    stage: 'needs-login',
    message: 'No running persistent container for Steam',
    needsLoginReason:
      'No running persistent container. Start and log in the persistent container before scheduling.',
    stageKey: 'signalr.scheduledPrefill.needsPersistentContainer'
  };

  assert.equal(
    await cardFor(event, 'zh'),
    'Steam 需要登录：没有正在运行的持久容器。请先启动持久容器并在其中登录，再设置计划。'
  );
});

test('a daemon sentence with no key still reaches the card instead of vanishing', async () => {
  const event = {
    serviceId: 'Steam',
    stage: 'failed',
    message: 'SteamKit2 refused the depot manifest request'
  };

  assert.equal(await cardFor(event, 'zh'), 'Steam：SteamKit2 refused the depot manifest request');
});

test('a failed service names itself and its reason in the reader language', async () => {
  await translator.changeLanguage('zh');
  assert.equal(
    scheduledPrefillFailure({
      serviceId: 'Steam',
      error: 'All due services need login',
      stageKey: 'signalr.scheduledPrefill.runAllNeedLogin'
    }),
    'Steam 失败：所有到期的服务都需要登录'
  );

  // A run that died on an exception has only .NET's own text, so the card shows that rather than
  // an empty line.
  assert.equal(
    scheduledPrefillFailure({ serviceId: 'Steam', error: 'The operation has timed out.' }),
    'Steam 失败：The operation has timed out.'
  );
});

test('a superseded history row is stored as a key and read as a sentence', async () => {
  const csharp = readFileSync(
    new URL(
      '../../Api/LancacheManager/Core/Services/Prefill/PrefillSessionService.cs',
      import.meta.url
    ),
    'utf8'
  );
  assert.match(csharp, /stale\.ErrorMessage = "signalr\.prefillSession\.superseded";/);

  await translator.changeLanguage('zh');
  assert.equal(
    translateStageKeyMessage('signalr.prefillSession.superseded'),
    '已被新的预填充操作取代'
  );
  // Rows written before the key was introduced still hold English, and are shown as they are.
  assert.equal(
    translateStageKeyMessage('Superseded by new prefill operation'),
    'Superseded by new prefill operation'
  );
});

test('every scheduled prefill and session key is translated with the same placeholders', () => {
  const leaves = (node, prefix) =>
    Object.entries(node).flatMap(([key, value]) =>
      value !== null && typeof value === 'object'
        ? leaves(value, `${prefix}.${key}`)
        : [[`${prefix}.${key}`, value]]
    );

  const tokens = (value) =>
    [...String(value).matchAll(/{{\s*([\w.]+)\s*}}/g)].map((m) => m[1]).sort();

  const missing = [];
  for (const group of ['scheduledPrefill', 'prefillSession']) {
    for (const [key, value] of leaves(en.signalr[group], `signalr.${group}`)) {
      const chinese = key
        .split('.')
        .reduce((node, segment) => (node ? node[segment] : undefined), { signalr: zh.signalr });
      if (typeof chinese !== 'string') {
        missing.push(`zh.json is missing ${key}`);
        continue;
      }
      if (tokens(chinese).join() !== tokens(value).join()) {
        missing.push(`${key} has different en/zh placeholders`);
      }
    }
  }

  assert.deepEqual(missing, []);
});
