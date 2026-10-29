import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import i18next from 'i18next';
import ts from 'typescript';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
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
const cacheStatusUrl = await compileToUrl('../src/components/features/prefill/cacheStatus.ts');
const { markCacheAppsUnknown } = await import(cacheStatusUrl);

const recoveryMessages = {
  'errors.steam.signInLost': 'Steam is no longer signed in. Log in again, then try again.',
  'errors.steam.gameDetailsUnavailable': 'Steam did not return game details. Try again.',
  'errors.prefill.requestFailed': 'The prefill daemon could not complete the request. Try again.',
  'signalr.steamSession.savedSignInPreserved':
    'Steam ended the current connection. Your saved sign-in is still available. Retry the operation.',
  'prefill.gameSelection.lastKnownCached': 'Previously cached'
};

test('recovery keys exist in both locales and production translations do not hide missing keys', () => {
  for (const [key, expected] of Object.entries(recoveryMessages)) {
    const english = key.split('.').reduce((value, part) => value?.[part], en);
    const chinese = key.split('.').reduce((value, part) => value?.[part], zh);
    assert.equal(english, expected, key);
    assert.equal(typeof chinese, 'string', key);
    assert.ok(chinese.trim(), key);
    assert.notEqual(chinese, english, key);
  }
  let affectedCalls = 0;
  for (const path of [
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
    'src/components/features/management/schedules/scheduled-prefill/PersistentLoginHost.tsx',
    'src/components/features/prefill/PrefillPanel.tsx',
    'src/components/features/prefill/GameSelectionModal.tsx',
    'src/services/api.service.ts'
  ]) {
    const source = parseSource(path, ts.ScriptKind.TSX);
    const calls = collectNodes(
      source,
      (node) =>
        ts.isCallExpression(node) &&
        (node.expression.getText(source) === 't' || node.expression.getText(source) === 'i18n.t')
    );
    for (const call of calls) {
      const [key, options] = call.arguments;
      assert.ok(!options || !ts.isStringLiteralLike(options), `${path}: string fallback`);
      if (options && ts.isObjectLiteralExpression(options)) {
        assert.ok(
          !options.properties.some(
            (property) => property.name?.getText(source).replace(/['"]/g, '') === 'defaultValue'
          ),
          `${path}: defaultValue`
        );
      }
      if (key && ts.isStringLiteralLike(key) && Object.hasOwn(recoveryMessages, key.text)) {
        affectedCalls += 1;
        assert.equal(call.arguments.length, 1, `${path}: ${key.text}`);
      }
    }
  }
  assert.equal(affectedCalls, 3);
});

test('the signed-out texts promise no automatic sign-in and name no guest session', () => {
  // A person's Cancel, an errored session and a refused saved login all leave the container signed out.
  // The Steam text is for interactive prefill; a scheduled run's card and its run row both use the prefill
  // text, which allows for the automatic sign-in after a drop. Epic and Xbox runs reach the prefill text too,
  // so it names no platform.
  assert.equal(
    en.errors.steam.signInLost,
    'Steam is no longer signed in. Log in again, then try again.'
  );
  assert.equal(zh.errors.steam.signInLost, 'Steam 已退出登录。请重新登录，然后重试。');
  assert.equal(
    en.errors.prefill.signInLost,
    'The service account was signed out before this finished. An always-on container may sign in again by itself with its saved login. If it is still signed out, log in again, then run it again.'
  );
  assert.equal(
    zh.errors.prefill.signInLost,
    '服务账户在完成前已退出登录。常驻容器可能会使用已保存的登录信息自行重新登录。如果仍未登录，请重新登录，然后再次运行。'
  );
  for (const text of [en.errors.steam.signInLost, en.errors.prefill.signInLost]) {
    assert.doesNotMatch(text, /signs in again by itself|guest/i);
  }
  for (const text of [zh.errors.steam.signInLost, zh.errors.prefill.signInLost]) {
    assert.doesNotMatch(text, /会自行重新登录|访客/);
  }
});

const translator = i18next.createInstance();
await translator.init({
  resources: { en: { translation: en }, zh: { translation: zh } },
  lng: 'en',
  fallbackLng: 'en',
  supportedLngs: ['en', 'zh'],
  interpolation: { escapeValue: false }
});

globalThis.testTranslator = translator;

test('the prefill capacity line renders active downloads separately from saved schedules in both locales', async () => {
  // Scheduled Activity dropped its capacity paragraph; each running run shows its own slot instead.
  const paths = [['src/components/features/prefill/PrefillCommandButtons.tsx', 'space-y-1']];
  for (const [path, capacityClass] of paths) {
    const source = parseSource(path, ts.ScriptKind.TSX);
    const call = findSoleNode(
      source,
      'capacity translation',
      (node) =>
        ts.isCallExpression(node) &&
        node.arguments[0]?.getText(source) === "'prefill.runs.capacity'"
    );
    let region = call.parent;
    while (
      region &&
      !(
        ts.isJsxElement(region) && region.getText(source).includes("t('prefill.runs.capacityHelp')")
      )
    )
      region = region.parent;
    assert.ok(region, `${path} keeps capacity and explanation together`);
    const compiled = ts.transpileModule(`const render = () => (${region.getText(source)});`, {
      compilerOptions: { target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.React, jsxFactory: 'h' }
    }).outputText;
    const render = new Function(
      'h',
      't',
      'activeRunCount',
      'maxConcurrentRuns',
      'container',
      `${compiled}\nreturn render();`
    );
    const h = (type, props, ...children) => ({ type, props, children });
    for (const language of ['en', 'zh']) {
      await translator.changeLanguage(language);
      for (const count of [0, 1, 4]) {
        const tree = render(h, translator.t.bind(translator), count, 4, {
          activeRunCount: count,
          maxConcurrentRuns: 4
        });
        assert.equal(tree.type, 'div');
        assert.equal(tree.props.className, capacityClass);
        const paragraphs = tree.children.filter((child) => child?.type === 'p');
        assert.equal(paragraphs.length, 2);
        const [label, help] = paragraphs.map((paragraph) => paragraph.children.join(''));
        assert.equal(
          label,
          language === 'en'
            ? `${count} of 4 simultaneous download${count === 1 ? '' : 's'} active`
            : `当前有 ${count} 个同时下载任务（上限 4 个）`
        );
        assert.equal(
          help,
          language === 'en'
            ? 'Saved schedules use a slot only while downloading.'
            : '已保存的计划仅在下载时占用名额。'
        );
        assert.doesNotMatch(label + help, /{{|prefill\.runs\.|run slots in use/);
      }
    }
  }
});

test('scheduled Activity reads each running run slot limit from that run own container', () => {
  const activity = parseSource(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillActivityModal.tsx',
    ts.ScriptKind.TSX
  );
  assert.doesNotMatch(activity.text, /prefill\.runs\.capacity/);
  const runRenderer = findSoleNode(
    activity,
    'Activity running run renderer',
    (node) =>
      ts.isArrowFunction(node) &&
      node.parameters[0]?.getText(activity) === '{ run, serviceKey }' &&
      node.getText(activity).includes('activity.slot')
  );
  const body = runRenderer.getText(activity);
  assert.match(body, /const container = containers\.containersByServiceKey\.get\(serviceKey\);/);
  assert.match(body, /limit: container\?\.maxConcurrentRuns/);
  assert.match(body, /handleCancelPersistentDownload\(\s*serviceKey,\s*run\.runId\s*\)/);
});

const i18nStub = moduleUrl('export default globalThis.testTranslator;');

const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
  '@/i18n': i18nStub,
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});

const { ApiError, buildApiError } = await import(apiErrorUrl);

const { getErrorMessage } = await import(
  await compileToUrl('../src/utils/error.ts', {
    '@/i18n': i18nStub,
    '../services/apiError': apiErrorUrl
  })
);

const { blankStringValues, translateStageKeyMessage, hasUnresolvedInterpolation } = await import(
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

test('external API error messages resolve in both languages and show an unknown key as its key path', async () => {
  for (const [stageKey, english] of Object.entries(recoveryMessages)) {
    assert.equal(await shownFor({ stageKey, error: english }, 'en'), english);
    const translated = await shownFor({ stageKey, error: english }, 'zh');
    assert.notEqual(translated, english);
    assert.notEqual(translated, stageKey);
    assert.doesNotMatch(translated, /SteamKit2|AsyncJobFailedException| at .*\(/);
    assert.equal(
      await shownFor({ stageKey: `missing.${stageKey}`, error: english }, 'zh'),
      `missing.${stageKey}`
    );
  }
});

test('both picker load failures translate typed reasons and expose missing required keys', async () => {
  const empty = i18next.createInstance();
  await empty.init({ lng: 'en', fallbackLng: false, resources: {} });
  const scheduledSource = liftHookCallback(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
    'useCallback',
    'const key = `${serviceKey}:${sessionId}`'
  );
  const ordinarySource = liftHookCallback(
    'src/components/features/prefill/PrefillPanel.tsx',
    'useCallback',
    'const gamesCache ='
  );
  for (const language of ['en', 'zh', 'missing']) {
    await translator.changeLanguage(language === 'missing' ? 'en' : language);
    const t = (language === 'missing' ? empty : translator).t.bind(
      language === 'missing' ? empty : translator
    );
    // undefined: a body with the server's own text and no stage key; null: a bodyless 503.
    for (const [stageKey, context] of [
      ['errors.steam.signInLost'],
      ['errors.prefill.signInLost'],
      ['errors.prefill.loginRequired'],
      ['errors.prefill.noRunningSession', { service: 'BattleNet' }],
      ['errors.steam.gameDetailsUnavailable'],
      ['errors.prefill.requestFailed'],
      ['errors.prefill.runLimit'],
      [undefined],
      [null]
    ]) {
      const failure = await buildApiError({
        status: 503,
        statusText: 'Unavailable',
        text: async () =>
          stageKey === null
            ? ''
            : JSON.stringify({ stageKey, context, error: 'SteamKit2.AsyncJobFailedException' })
      });
      const key = stageKey ?? 'errors.prefill.requestFailed';
      const expected = language === 'missing' ? key : translator.t(key, context);
      if (language !== 'missing') assert.doesNotMatch(expected, /\{\{/);
      let scheduledMessage;
      const selection = {
        serviceKey: 'steam',
        scheduleId: 'schedule-1',
        sessionId: 's1',
        cachedAppIds: [],
        outdatedAppIds: [],
        unknownAppIds: [],
        apps: [],
        message: null,
        games: []
      };
      await bindLifted(scheduledSource, {
        ApiError,
        t,
        current: { current: { opening: 'opening-1' } },
        identityRef: { current: 'account-a' },
        containerRef: {
          current: {
            sessionId: 's1',
            isRunning: true,
            isAuthenticated: true,
            needsRelogin: false
          }
        },
        configRef: { current: { operatingSystems: ['Windows'] } },
        gameAuthRef: { current: { key: 'steam:s1', authenticated: true } },
        gameSelectionRef: { current: selection },
        gameRequestRef: { current: null },
        markCacheAppsUnknown,
        setLoadingGameSelectionService: () => undefined,
        setGameSelection: (update) => update(selection),
        setGameLoadError: (message) => {
          scheduledMessage = message;
        },
        isScheduledPrefillAnonymousService: (service) => ['battleNet', 'riot'].includes(service),
        getPersistentServiceId: (service) => service,
        ApiService: {
          getPersistentPrefillGames: async () => {
            throw failure;
          }
        }
      })('steam', 's1');
      assert.equal(scheduledMessage, expected);
      let ordinaryMessage;
      await bindLifted(ordinarySource, {
        getErrorMessage,
        t,
        signalR: { session: { id: 's1' } },
        serviceId: 'steam',
        serviceBasePath: 'steam-prefill',
        API_BASE: '/api',
        gamesKeyRef: { current: 'steam:s1' },
        gamesRequestRef: { current: null },
        gamesCacheRef: { current: null },
        cachedAppIdsRef: { current: [] },
        cacheAppsRef: { current: [] },
        gamesCacheWindowMs: 300000,
        ownedGames: [],
        markCacheAppsUnknown,
        setIsLoadingGames: () => undefined,
        setIsUsingGamesCache: () => undefined,
        setOutdatedAppIds: () => undefined,
        setUnknownAppIds: () => undefined,
        setCacheApps: () => undefined,
        setGameLoadError: (message) => {
          ordinaryMessage = message;
        },
        addLog: () => undefined,
        fetch: async () => ({}),
        assertOk: async () => {
          throw failure;
        }
      })();
      // The ordinary picker shows the shared sentence set: a stage key's translation, the server's
      // own text when there is no stage key, and the class sentence for a bodyless failure.
      const ordinaryExpected =
        stageKey === null
          ? translator.t('common.errors.serverUnreachable')
          : stageKey === undefined
            ? 'SteamKit2.AsyncJobFailedException'
            : translator.t(stageKey, context);
      assert.equal(ordinaryMessage, ordinaryExpected);
      assert.equal(ordinaryMessage, getErrorMessage(failure));
    }
  }
});

test('nonterminal app failure does not display or map its unconsumed error fields', () => {
  const source = parseSource('src/components/features/prefill/hooks/usePrefillEventHandlers.ts');
  const callback = findSoleNode(
    source,
    'live prefill progress callback',
    (node) =>
      ts.isArrowFunction(node) && node.getText(source).includes('options.applyRunProgress?.')
  );
  const display = [];
  const handleProgress = bindLifted(callback.getText(source), {
    sessionRef: { current: { id: 's1' } },
    options: { applyRunProgress: (progress) => display.push(progress) }
  });
  // Only a run's own progress, which names its operation and daemon, reaches the bar. A failure
  // with neither attached has no run to show and no field of it is read.
  handleProgress({
    sessionId: 's1',
    progress: {
      state: 'app_failed',
      totalApps: 1,
      errorCode: 'game-details-unavailable',
      requiresLogin: false,
      errorMessage: recoveryMessages['errors.steam.gameDetailsUnavailable']
    }
  });
  assert.deepEqual(display, []);
  const runProgress = { state: 'running', totalApps: 1, operationId: 'o1', daemonInstanceId: 'd1' };
  handleProgress({ sessionId: 's1', progress: runProgress });
  assert.deepEqual(display, [runProgress]);
});

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

/**
 * Source of a module-level `function <name>(...)`, which lifts like an arrow does. An `export`
 * keyword is left off: the lifted text runs as an expression, where `export` does not parse.
 */
const functionFor = (name) =>
  findSoleNode(
    registryFile,
    `${name} declaration`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.getText(registryFile) === name
  )
    .getText(registryFile)
    .replace(/^export\s+/, '');

const scheduledPrefillServiceLabel = bindLifted(functionFor('scheduledPrefillServiceLabel'), {
  i18n: translator,
  SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY
});

// Both wordings below read the stage key through this, so it is lifted the same way they are.
const scheduledPrefillSentence = bindLifted(functionFor('scheduledPrefillSentence'), {
  blankStringValues,
  translateStageKeyMessage,
  hasUnresolvedInterpolation
});

// The progress event and the run-status response describe a service the same way, so the card's
// wording lives in one function both compose from.
const scheduledPrefillMessage = bindLifted(functionFor('scheduledPrefillServiceMessage'), {
  i18n: translator,
  translateStageKeyMessage,
  scheduledPrefillSentence,
  scheduledPrefillServiceLabel
});

const scheduledPrefillFailure = bindLifted(
  arrowFor('scheduled prefill getFailureMessage', 'scheduledPrefill.events.failed'),
  {
    i18n: translator,
    translateStageKeyMessage,
    scheduledPrefillSentence,
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
      'An existing persistent battlenet container is still being removed or restarting. Please try again shortly.',
    stageKey: 'errors.prefill.containerBusy',
    context: { service: 'battlenet' }
  };

  assert.equal(
    await shownFor(body, 'en'),
    'An existing persistent Battle.net container is still being removed or restarting. Please try again shortly.'
  );
  assert.equal(
    await shownFor(body, 'zh'),
    '现有的 Battle.net 持久容器仍在移除或重启中。请稍后重试。'
  );
});

test('container refusals name the service by its display name for every daemon', async () => {
  // The daemons send their internal service names; the reader sees the names the Services rows use.
  const labels = {
    Steam: 'Steam',
    Epic: 'Epic Games',
    Xbox: 'Xbox',
    battlenet: 'Battle.net',
    riot: 'Riot'
  };
  for (const [service, label] of Object.entries(labels)) {
    for (const [stageKey, context] of [
      ['errors.prefill.containerBusy', { service }],
      ['errors.prefill.containerRemovalFailed', { service }],
      ['errors.prefill.daemonShuttingDown', { service, sessionId: 'session-1' }]
    ]) {
      for (const language of ['en', 'zh']) {
        const shown = await shownFor({ error: 'English line', stageKey, context }, language);
        assert.ok(shown.includes(label), `${stageKey} ${service} ${language}: ${shown}`);
        assert.doesNotMatch(shown, /errors\.prefill|\{\{|\$t\(/, `${stageKey} ${service}`);
        if (!label.includes(service)) assert.ok(!shown.includes(service), `${stageKey} ${service}`);
      }
    }
  }
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

test('a game name holding braces does not turn the progress line back into English', async () => {
  for (const game of ['Foo {{Bar}} Edition', 'Thing}}']) {
    const event = {
      serviceId: 'Steam',
      stage: 'running',
      message: `Downloading ${game} (2 of 5 games)`,
      stageKey: 'signalr.scheduledPrefill.downloadingGame',
      stageContext: { game, completed: 2, total: 5 }
    };

    assert.equal(await cardFor(event, 'en'), `Steam: Downloading ${game} (2 of 5 games)`);
    assert.equal(await cardFor(event, 'zh'), `Steam：正在下载 ${game}（2/5 个游戏）`);
  }

  // A token the context did not fill still falls back to the English beside the key.
  const missingTotal = {
    serviceId: 'Steam',
    stage: 'running',
    message: 'Downloading Foo {{Bar}} Edition (2 of 5 games)',
    stageKey: 'signalr.scheduledPrefill.downloadingGame',
    stageContext: { game: 'Foo {{Bar}} Edition', completed: 2 }
  };
  assert.equal(
    await cardFor(missingTotal, 'zh'),
    'Steam：Downloading Foo {{Bar}} Edition (2 of 5 games)'
  );
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

test('a superseded history row stored as a key is read as a sentence', async () => {
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

test('concurrency refusals and runtime failures have matching localized messages', async () => {
  for (const key of [
    'runLimit',
    'operationConflict',
    'operationNotFound',
    'instanceChanged',
    'ambiguousOperation',
    'outcomeUnknown'
  ]) {
    const stageKey = `errors.prefill.${key}`;
    assert.equal(typeof en.errors.prefill[key], 'string');
    assert.equal(typeof zh.errors.prefill[key], 'string');
    assert.equal(
      await shownFor({ stageKey, error: en.errors.prefill[key] }, 'zh'),
      zh.errors.prefill[key]
    );
  }
  assert.equal(typeof en.signalr.scheduledPrefill.failedMaxRuntime, 'string');
  assert.equal(typeof zh.signalr.scheduledPrefill.failedMaxRuntime, 'string');
  for (const [key, value] of Object.entries(en.prefill.runs)) {
    assert.equal(typeof zh.prefill.runs[key], 'string');
    const tokens = (text) => [...text.matchAll(/{{(\w+)}}/g)].map((match) => match[1]).sort();
    assert.deepEqual(tokens(value), tokens(zh.prefill.runs[key]));
  }
});
