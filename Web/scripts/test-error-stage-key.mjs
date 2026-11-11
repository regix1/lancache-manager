import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import i18next from 'i18next';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ts from 'typescript';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  compileTree,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * The API names the reason it refused a request as an i18n key sent beside the English sentence.
 * Every screen that shows a failure reads it through `getErrorMessage`, so a key that function does
 * not look at is a key nobody ever sees: the reader gets the English sentence in every language,
 * even where the Chinese for it has been sitting in the locale file all along.
 *
 * The notification cards take the other road, `translateStageKeyMessage`, whose pass-through branch
 * prints whatever it was handed. A key arriving there and being printed as a key path would be worse
 * than the English it replaced, so both roads are checked here against the real locale files.
 */

const localeFile = (name) =>
  JSON.parse(readFileSync(new URL(`../src/i18n/locales/${name}.json`, import.meta.url), 'utf8'));

/** Source text of a file under `src/`. */
const read = (path) => readFileSync(new URL(`../src/${path}`, import.meta.url), 'utf8');

const en = localeFile('en');
const zh = localeFile('zh');

/**
 * i18next itself, initialized the way `src/i18n/index.ts` initializes it, so `defaultValue` and the
 * English fallback behave here exactly as they do in the app.
 */
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

// The real constants module reads `import.meta.env` while it loads, which only vite supplies.
// `apiError.ts` reads APP_EVENTS from it on the 401 branch alone, and nothing here sends a 401.
const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
  '@/i18n': i18nStub,
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});

const { buildApiError, ApiError } = await import(apiErrorUrl);

const { getErrorMessage, isAbortError } = await import(
  await compileToUrl('../src/utils/error.ts', {
    '@/i18n': i18nStub,
    '../services/apiError': apiErrorUrl
  })
);

const { translateStageKeyMessage, translateRecoveryStage, datasourceFailureValues } = await import(
  // compileTree, because the recovery stage reads `import.meta.env`, which only vite supplies.
  await compileTree('../src/utils/stageKeyMessage.ts', { '@/i18n': i18nStub })
);

/** The failed response for a status whose body is exactly `text`, as the browser hands it over. */
const failedWith = (status, text) => buildApiError(new Response(text, { status }));

/** The failed response the browser gets, with the body the API wrote into it. */
const refusal = (body) => failedWith(400, JSON.stringify(body));

/** What one refusal body reads as, for a reader of that language. */
const shownFor = async (body, language) => {
  await translator.changeLanguage(language);
  return getErrorMessage(await refusal(body));
};

test('the typed error rejects empty messages at construction', () => {
  for (const message of ['', '   ']) {
    assert.throws(
      () => new ApiError({ message, status: 500, kind: 'http', body: null }),
      /nonempty message/
    );
  }
  const error = new ApiError({
    message: 'Authoritative sentence',
    status: 500,
    kind: 'http',
    body: { error: 'Raw detail' }
  });
  assert.equal(getErrorMessage(error), 'Authoritative sentence');
});

/** The current language's sentence for `key`, failing when the locale has no words for it. */
const sentence = (key) => {
  const translated = translator.t(key);
  assert.notEqual(translated, key, `${key} is missing from the ${translator.language} locale`);
  return translated;
};

test('a gateway failure with no reason reads as the server being unreachable', async () => {
  // What a reverse proxy answers while the API is down: an empty body, a plain-text line, or its
  // own HTML page. None of it is a sentence the reader can act on, and the status alone says why.
  const bodies = [
    '',
    '  ',
    'Upstream failure',
    '<html><body><h1>502 Bad Gateway</h1></body></html>'
  ];
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    for (const status of [502, 503, 504]) {
      for (const text of bodies) {
        const error = await failedWith(status, text);
        assert.ok(error.message.trim());
        assert.equal(
          getErrorMessage(error),
          sentence('common.errors.serverUnreachable'),
          `${status} with body ${JSON.stringify(text)} in ${language}`
        );
      }
    }
  }
});

test('a failure with no reason reads as its status class', async () => {
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    // A bare 401 challenge. Built directly: the API layer dispatches a browser event on 401.
    const unauthorized = new ApiError({
      message: 'HTTP 401: Unauthorized',
      status: 401,
      kind: 'auth',
      body: null
    });
    assert.equal(getErrorMessage(unauthorized), sentence('common.errors.notAuthorized'));
    assert.equal(
      getErrorMessage(await failedWith(403, '{}')),
      sentence('common.errors.notAuthorized')
    );
    assert.equal(
      getErrorMessage(await failedWith(500, '')),
      sentence('common.errors.requestFailed')
    );
    // A framework ProblemDetails body names no reason a reader can use.
    assert.equal(
      getErrorMessage(await failedWith(400, JSON.stringify({ title: 'x', status: 400 }))),
      sentence('common.errors.requestFailed')
    );
  }
});

test('a failure that names its reason keeps the server sentence whatever its status', async () => {
  const reason = 'The container could not start the prefill. Try again.';
  assert.equal(getErrorMessage(await failedWith(503, JSON.stringify({ error: reason }))), reason);
});

test('a request that never reached the server reads as unreachable or timed out', async () => {
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    // Chrome, Safari and Firefox word the same unreachable server three ways.
    for (const message of [
      'Failed to fetch',
      'Load failed',
      'NetworkError when attempting to fetch resource.'
    ]) {
      assert.equal(
        getErrorMessage(new TypeError(message)),
        sentence('common.errors.serverUnreachable'),
        message
      );
    }
    assert.equal(
      getErrorMessage(new DOMException('signal timed out', 'TimeoutError')),
      sentence('errors.http.timeout')
    );
  }
  // A TypeError thrown by a bug is not a network failure and keeps its own text.
  assert.equal(getErrorMessage(new TypeError('x is not a function')), 'x is not a function');
});

test('unknown empty failures receive the shared unknown-error translation', async () => {
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    for (const error of [new Error(''), new Error('  '), '', ' ']) {
      assert.equal(getErrorMessage(error), translator.t('common.unknownError'));
    }
  }
});

test('raw HTTP variants keep their sentence and optional structured details', async () => {
  for (const [body, expected] of [
    [{ message: 'Message only' }, 'Message only'],
    [{ error: 'Error only' }, 'Error only'],
    [{ message: 'Primary', error: 'Secondary' }, 'Primary'],
    [
      { message: 'Primary', details: 'Detail', suggestion: 'Action' },
      'Primary\n\nDetail\n\nAction'
    ],
    [{ message: '   ', error: 'Reason' }, 'Reason']
  ]) {
    assert.equal(getErrorMessage(await refusal(body)), expected);
  }
  const error = await refusal({ message: '   ', error: '' });
  assert.match(error.message, /^HTTP 400:/);
});

test('a canonical conflict may use only its localized stage', async () => {
  const body = {
    code: 'OPERATION_CONFLICT',
    stageKey: 'errors.conflict.downloadHistoryUpgradeActive'
  };
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    const error = await failedWith(409, JSON.stringify(body));
    assert.equal(error.kind, 'conflict');
    assert.deepEqual(error.cause, body);
    assert.equal(error.message, translator.t(body.stageKey));
  }
});

test('a canonical conflict without a usable stage is a protocol parse failure', async () => {
  await translator.changeLanguage('en');
  for (const stageKey of [undefined, null, '', '   ', 42]) {
    const body = {
      code: 'OPERATION_CONFLICT',
      error: 'English prose cannot replace the required typed stage',
      ...(stageKey === undefined ? {} : { stageKey })
    };
    const error = await failedWith(409, JSON.stringify(body));
    assert.equal(error.kind, 'parse');
    assert.equal(error.status, 409);
    assert.equal(error.body, null);
    assert.deepEqual(error.cause, body);
    assert.equal(error.message, en.common.errors.invalidJsonResponse);
  }
});

test('a noncanonical 409 keeps its existing stage-only behavior', async () => {
  const body = { stageKey: 'errors.validation.failed' };
  const error = await failedWith(409, JSON.stringify(body));
  assert.equal(error.kind, 'conflict');
  assert.deepEqual(error.cause, body);
  assert.match(error.message, /^HTTP 409:/);
});

test('a refusal that names its reason is read in the reader language', async () => {
  // What GlobalExceptionMiddleware writes for an UnauthorizedAccessException in production.
  const denied = {
    error: 'Access denied',
    stageKey: 'errors.http.accessDenied',
    statusCode: 403
  };

  assert.equal(await shownFor(denied, 'zh'), zh.errors.http.accessDenied);
  assert.notEqual(await shownFor(denied, 'zh'), denied.error);
  assert.equal(await shownFor(denied, 'en'), en.errors.http.accessDenied);
});

test('the validation refusal reads as a sentence in both languages', async () => {
  // What ValidationFilter writes when a request fails its validator. The per-field list rides along
  // untouched; the reader sees the heading sentence.
  const invalid = {
    error: 'Validation failed',
    stageKey: 'errors.validation.failed',
    errors: [{ field: 'password', message: 'Password cannot exceed 256 characters' }]
  };

  assert.equal(await shownFor(invalid, 'zh'), zh.errors.validation.failed);
  assert.equal(await shownFor(invalid, 'en'), 'Validation failed');
});

test('a refusal with no key still reads as the English sentence', async () => {
  // The great majority of throw sites today. Nothing about them may change until their own key
  // arrives, so this is the check that the fix costs nothing where it does not apply.
  const unkeyed = { error: 'Cache path does not exist for any datasource' };

  assert.equal(await shownFor(unkeyed, 'zh'), unkeyed.error);
  assert.equal(await shownFor(unkeyed, 'en'), unkeyed.error);
});

test('a key this build has no words for shows its key path, not the server sentence', async () => {
  const newer = {
    error: 'A newer server refused this for a reason this build has no words for',
    stageKey: 'errors.http.somethingAddedLater'
  };

  assert.equal(await shownFor(newer, 'zh'), newer.stageKey);
});

test('the values sent beside the key reach the template', async () => {
  // No refusal key carries a placeholder yet, so this borrows one from the same bundle that does.
  // It is the path every interpolated refusal key will take once a throw site sends a context.
  const interpolated = {
    error: 'Unexpected response when starting log removal for steam',
    stageKey: 'management.logRemoval.errors.unexpectedResponse',
    context: { service: 'steam' }
  };

  const shown = await shownFor(interpolated, 'zh');
  assert.equal(
    shown,
    zh.management.logRemoval.errors.unexpectedResponse.replace('{{service}}', 'steam')
  );
  assert.ok(!shown.includes('{{'), `the placeholder was never filled in: ${shown}`);
});

test('a recovery stage keeps its values when a value contains braces', async () => {
  const stageKey = 'signalr.logProcessing.failedForDatasourceWithError';
  const fallbackKey = 'initialization.logProcessing.failedToProcess';
  const braceError = 'malformed array literal: "{{1,2}}"';

  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    assert.equal(
      translateRecoveryStage(stageKey, datasourceFailureValues('steam', braceError), fallbackKey),
      translator.t(stageKey, { datasource: 'steam', databaseError: braceError })
    );
    // A template token that has no value still falls back instead of printing the token.
    assert.equal(
      translateRecoveryStage(stageKey, { datasource: 'steam' }, fallbackKey),
      translator.t(fallbackKey)
    );
    assert.equal(
      translateRecoveryStage(stageKey, undefined, fallbackKey),
      translator.t(fallbackKey)
    );
  }
});

test('a notification card translates a refusal key instead of printing it', async () => {
  await translator.changeLanguage('zh');

  assert.equal(translateStageKeyMessage('errors.validation.failed'), zh.errors.validation.failed);
  assert.equal(translateStageKeyMessage('errors.http.timeout'), zh.errors.http.timeout);
});

test('a run ended on a sign-in, management or common key shows that key translated', async () => {
  await translator.changeLanguage('zh');

  // A refused sign-in ends its run on a prefill key, a refusal can carry a management key and a
  // stopped sign-in a common one; the card's error line reads each of them from the run.
  assert.equal(
    translateStageKeyMessage('prefill.auth.signInRefused'),
    zh.prefill.auth.signInRefused
  );
  assert.equal(
    translateStageKeyMessage('management.datasources.noActiveDatasources'),
    zh.management.datasources.noActiveDatasources
  );
  assert.equal(
    translateStageKeyMessage('common.notifications.warnings.signInSessionEnded'),
    zh.common.notifications.warnings.signInSessionEnded
  );
});

test('a notification card still shows a sentence the backend composed itself', async () => {
  await translator.changeLanguage('zh');

  // A count and a path the server filled in at run time. There is no key to look these up by, so
  // passing them through is the only thing left, and it is what the card shows today.
  const composed = 'Deleted 412 downloads, 96 log entries';
  assert.equal(translateStageKeyMessage(composed), composed);
});

// ---------------------------------------------------------------------------
// The first-admin screen, which reads the response body itself rather than through getErrorMessage
// ---------------------------------------------------------------------------

const accountStep = parseSource(
  'src/components/initialization/steps/AdminAccountStep.tsx',
  ts.ScriptKind.TSX
);

/** Source text of the expression that screen builds its message from, as it ships. */
const submitErrorExpression = findSoleNode(
  accountStep,
  'setSubmitError call that reads the key',
  (node) =>
    ts.isCallExpression(node) &&
    node.expression.getText(accountStep) === 'setSubmitError' &&
    node.arguments.length === 1 &&
    node.arguments[0].getText(accountStep).includes('stageKey')
).arguments[0].getText(accountStep);

/** What the screen puts under the form for one response body. */
const accountStepShows = (data) => {
  const t = (key, options) => translator.t(key, options);
  return bindLifted(`() => (${submitErrorExpression})`, { data, t, recovering: false })();
};

test('the account screen reads a refusal it has words for in the reader language', async () => {
  await translator.changeLanguage('zh');

  const refused = {
    error: 'An account already exists on this installation',
    stageKey: 'errors.accountSetup.accountExists'
  };

  assert.equal(accountStepShows(refused), zh.errors.accountSetup.accountExists);
});

test('the account screen reads a broken password rule as the translated validation sentence', async () => {
  await translator.changeLanguage('zh');

  // The whole body ValidationFilter sends when a password rule fails. The form checks every rule
  // the server's validator holds before it sends, so this body follows only a request built by
  // hand, and its English per-field sentence is never shown in place of the keyed heading.
  const brokenRule = {
    error: 'Validation failed',
    stageKey: 'errors.validation.failed',
    errors: [{ field: 'password', message: 'Password cannot exceed 256 characters' }]
  };

  assert.equal(accountStepShows(brokenRule), zh.errors.validation.failed);
});

test('the account screen with no key in the body reads its own status sentence', async () => {
  await translator.changeLanguage('zh');

  assert.equal(
    accountStepShows({ error: 'An unexpected error occurred' }),
    zh.initialization.adminAccount.errors.createFailed
  );
});

test('a Run Now refused because the service is turned off in the server configuration reads in the reader language', async () => {
  const body = {
    error: 'This service is turned off in the server configuration, so it cannot run.',
    stageKey: 'management.schedules.runNowServiceDisabled'
  };
  const error = await failedWith(409, JSON.stringify(body));
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    assert.equal(getErrorMessage(error), sentence('management.schedules.runNowServiceDisabled'));
  }
  await translator.changeLanguage('zh');
  assert.notEqual(getErrorMessage(error), body.error);
});

test('a Run Now refused because the scan program is missing reads in the reader language', async () => {
  const body = {
    error: 'The scan program this service needs is missing on the server, so it cannot run.',
    stageKey: 'management.schedules.runNowProgramMissing'
  };
  const error = await failedWith(409, JSON.stringify(body));
  for (const language of ['en', 'zh']) {
    await translator.changeLanguage(language);
    assert.equal(getErrorMessage(error), sentence('management.schedules.runNowProgramMissing'));
  }
  await translator.changeLanguage('zh');
  assert.notEqual(getErrorMessage(error), body.error);
});

// ---------------------------------------------------------------------------
// The database setup screens, which answer a refusal from SetupController
// ---------------------------------------------------------------------------

/** A failed fetch answer carrying the body SetupController writes for one refusal. */
const refusedWith = (status, body) => new Response(JSON.stringify(body), { status });

/** The success branch of ApiService.handleResponse is not under test; its failure branch is buildApiError. */
const handleResponse = async (response) => {
  if (!response.ok) throw await buildApiError(response);
  return JSON.parse(await response.text());
};

/** ApiService.setExternalDbCredentials as it ships, with its fetch and helpers supplied. */
const liftExternalCredentialsRequest = (fetch) => {
  const service = parseSource('src/services/api.service.ts');
  const method = findSoleNode(
    service,
    'setExternalDbCredentials method',
    (node) =>
      ts.isMethodDeclaration(node) && node.name.getText(service) === 'setExternalDbCredentials'
  );
  const parameters = method.parameters.map((parameter) => parameter.getText(service)).join(', ');
  return bindLifted(`async (${parameters}) => ${method.body.getText(service)}`, {
    fetch,
    API_BASE: '/api',
    ApiService: { getJsonFetchOptions: () => ({}), handleResponse }
  });
};

const passwordIsTheUsername = {
  error: 'Password cannot be the same as the username',
  stageKey: 'initialization.databaseSetup.errors.passwordSameAsUsername'
};

test('the password recovery form reads a refusal in the reader language', async () => {
  await translator.changeLanguage('zh');
  const shown = [];
  const handleSubmit = bindLifted(
    liftHookCallback('src/contexts/ConfigContext.tsx', 'useCallback', 'setup/credentials'),
    {
      canSubmit: true,
      isSaving: false,
      setIsSaving: () => undefined,
      setFailureMessage: (message) => shown.push(message),
      setSavedMessage: () => undefined,
      fetch: async () => refusedWith(400, passwordIsTheUsername),
      API_BASE: '/api',
      ApiService: { getJsonFetchOptions: () => ({}), handleResponse },
      username: 'lancache',
      password: 'lancache',
      apiKey: 'key',
      setApiKey: () => undefined,
      setPassword: () => undefined,
      t: (key, options) => translator.t(key, options),
      onSaved: () => assert.fail('a refusal is not a save'),
      getErrorMessage
    }
  );
  await handleSubmit();

  assert.equal(shown.at(-1), zh.initialization.databaseSetup.errors.passwordSameAsUsername);
});

test('the setup wizard database step reads a refusal in the reader language', async () => {
  await translator.changeLanguage('zh');
  const shown = [];
  const handleSubmit = bindLifted(
    liftHookCallback(
      'src/components/initialization/steps/DatabaseSetupStep.tsx',
      'useCallback',
      'setup/credentials'
    ),
    {
      validateForm: () => true,
      setIsSubmitting: () => undefined,
      setSubmitError: (message) => shown.push(message),
      fetch: async () => refusedWith(400, passwordIsTheUsername),
      API_BASE: '/api',
      ApiService: { getJsonFetchOptions: () => ({}), handleResponse },
      authenticationEnabled: false,
      form: {
        username: 'lancache',
        password: 'lancache',
        confirmPassword: 'lancache',
        apiKey: 'k'
      },
      setSetupSuccess: () => assert.fail('a refusal is not a save'),
      onSetupComplete: () => undefined,
      t: (key, options) => translator.t(key, options),
      getErrorMessage
    }
  );
  await handleSubmit();

  assert.equal(shown.at(-1), zh.initialization.databaseSetup.errors.passwordSameAsUsername);
});

test('the external database step reads a refusal and its values in the reader language', async () => {
  await translator.changeLanguage('zh');
  const unreachable = {
    error: 'Could not connect to db:5432/lancache',
    stageKey: 'errors.setup.connectFailed',
    context: { host: 'db', port: 5432, database: 'lancache' }
  };
  const shown = [];
  const handleSubmit = bindLifted(
    liftHookCallback(
      'src/components/initialization/steps/ExternalDatabaseSetupStep.tsx',
      'useCallback',
      'setExternalDbCredentials'
    ),
    {
      validateForm: () => true,
      setIsSubmitting: () => undefined,
      setSubmitError: (message) => shown.push(message),
      ApiService: {
        setExternalDbCredentials: liftExternalCredentialsRequest(async () =>
          refusedWith(400, unreachable)
        )
      },
      form: {
        host: 'db',
        port: '5432',
        database: 'lancache',
        username: 'lancache',
        password: 'a-password',
        apiKey: 'k'
      },
      setSetupSuccess: () => assert.fail('a refusal is not a save'),
      onSetupComplete: () => undefined,
      t: (key, options) => translator.t(key, options),
      getErrorMessage
    }
  );
  await handleSubmit();

  assert.equal(shown.at(-1), translator.t('errors.setup.connectFailed', unreachable.context));
  assert.match(shown.at(-1), /db:5432\/lancache/);
  assert.notEqual(shown.at(-1), unreachable.error);
});

// ---------------------------------------------------------------------------
// Sign-in, guest session and database reset, which return or receive a key rather than throw
// ---------------------------------------------------------------------------

/** The auth service as it ships, with `fetch` answering every route but the status through `answer`. */
const loadAuthService = async (answer) => {
  globalThis.fetch = async (url) =>
    String(url).endsWith('/api/auth/status')
      ? new Response(JSON.stringify({ isAuthenticated: false, sessionType: null }), { status: 200 })
      : answer();
  const url = await compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': i18nStub,
    '@utils/constants': moduleUrl(
      `export const getApiUrl = () => ''; export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`
    ),
    '@utils/antiforgery': moduleUrl(`export const antiforgeryHeaders = () => ({});`),
    '@utils/error': moduleUrl(`export const isAbortError = () => false;`),
    '@utils/userInteractionTracker': moduleUrl(
      `export const hasRecentUserInteraction = () => false;`
    ),
    './apiError': apiErrorUrl
  });
  return (await import(url)).default;
};

test('a refused sign-in reads the same zh sentence for a wrong username and a wrong password', async (t) => {
  t.after(() => {
    delete globalThis.fetch;
  });
  await translator.changeLanguage('zh');
  // The login route answers every credential refusal with this one body, so usernames cannot be
  // told apart by the reply.
  const refused = {
    error: 'Invalid API key, username or password',
    stageKey: 'errors.login.invalidCredentials'
  };
  const authService = await loadAuthService(
    () => new Response(JSON.stringify(refused), { status: 401 })
  );

  const wrongUsername = await authService.login('key', 'nobody', 'Correct-Password-1');
  const wrongPassword = await authService.login('key', 'admin', 'Wrong-Password-1');

  assert.deepEqual(wrongUsername, {
    success: false,
    message: sentence('errors.login.invalidCredentials')
  });
  assert.deepEqual(wrongPassword, wrongUsername);
});

test('a sign-in answered by a proxy page reads the status sentence', async (t) => {
  t.after(() => {
    delete globalThis.fetch;
  });
  await translator.changeLanguage('zh');
  const authService = await loadAuthService(
    () => new Response('<html><body>502 Bad Gateway</body></html>', { status: 502 })
  );

  assert.deepEqual(await authService.login('key', 'admin', 'Password-1'), {
    success: false,
    message: translator.t('auth.errors.loginFailedStatus', { status: 502 })
  });
});

test('a refused guest session reads in the reader language', async (t) => {
  t.after(() => {
    delete globalThis.fetch;
  });
  await translator.changeLanguage('zh');
  const authService = await loadAuthService(
    () =>
      new Response(
        JSON.stringify({ error: 'Guest access is disabled', stageKey: 'errors.guest.disabled' }),
        { status: 403 }
      )
  );

  assert.deepEqual(await authService.startGuestSession(), {
    success: false,
    message: sentence('errors.guest.disabled')
  });
});

// Subscribes the sign-in dialog's shipped reset handler with the given state setters and returns
// the handler it registered.
const subscribeResetHandler = (setters) => {
  const handlers = new Map();
  const formatters = Object.fromEntries(
    ['formatDatabaseResetProgressMessage', 'formatDatabaseResetCompleteMessage'].map((name) => [
      name,
      bindLifted(liftConstArrow('src/contexts/notifications/detailMessageFormatters.ts', name), {
        i18n: translator
      })
    ])
  );
  bindLifted(
    liftHookCallback(
      'src/components/modals/auth/AuthenticationModal.tsx',
      'useEffect',
      'handleDatabaseResetProgress'
    ),
    {
      on: (name, handler) => handlers.set(name, handler),
      off: () => undefined,
      t: (key, options) => translator.t(key, options),
      ...setters,
      ...formatters
    }
  )();
  return handlers.get('DatabaseResetProgress');
};

// Renders the sign-in dialog's shipped reset banner, with the shipped expressions of each banner
// flag the file declares, for one state.
const renderResetBanner = (resetStatus, resetJustCompleted) => {
  const modal = parseSource('src/components/modals/auth/AuthenticationModal.tsx');
  const banner = findSoleNode(
    modal,
    'database reset banner',
    (node) =>
      ts.isJsxExpression(node) &&
      node.getText(modal).startsWith('{(resetStatus.isResetting || resetJustCompleted)')
  );
  const icon =
    (name) =>
    ({ className }) =>
      React.createElement('svg', { 'data-icon': name, className });
  // Each flag reads the state and the flags declared before it.
  const flags = { resetStatus, resetJustCompleted };
  for (const name of ['resetFailed', 'resetCanceled', 'resetTextClass']) {
    for (const declaration of collectNodes(
      modal,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(modal) === name
    )) {
      flags[name] = bindLifted(`() => (${declaration.initializer.getText(modal)})`, {
        ...flags
      })();
    }
  }
  return renderToStaticMarkup(
    bindLifted(
      `() => (<>${banner.getText(modal)}</>)`,
      {
        React,
        t: (key, options) => translator.t(key, options),
        ...flags,
        formatPercent: String,
        ProgressBar: () => null,
        CheckCircle: icon('CheckCircle'),
        AlertCircle: icon('AlertCircle'),
        Database: icon('Database'),
        Info: icon('Info')
      },
      { jsx: ts.JsxEmit.React }
    )()
  );
};

test('the sign-in dialog reads database reset progress in the reader language', async () => {
  await translator.changeLanguage('zh');
  const stored = [];
  const deliver = subscribeResetHandler({
    setResetStatus: (value) => stored.push(value),
    setResetJustCompleted: () => undefined,
    scheduleResetBannerHide: () => undefined
  });

  const context = { deleted: 5, total: 50, percent: 10 };
  deliver({
    status: 'running',
    percentComplete: 10,
    stageKey: 'signalr.dbReset.clearingLogEntries',
    context,
    message: 'Clearing log entries'
  });
  assert.equal(stored.at(-1).message, translator.t('signalr.dbReset.clearingLogEntries', context));

  // A reset that left a prefill sign-in active reads the same here as on the notification strip.
  deliver({
    status: 'completed',
    percentComplete: 100,
    stageKey: 'signalr.dbReset.complete',
    context: { persistentLoginFailures: ['steam'] },
    message: 'Done'
  });
  assert.equal(stored.at(-1).message, sentence('signalr.dbReset.completeWithActiveLogins'));
});

test('a canceled database reset ends the sign-in dialog banner instead of leaving it running', async () => {
  await translator.changeLanguage('zh');
  const stored = [];
  const justCompleted = [];
  const deliver = subscribeResetHandler({
    setResetStatus: (value) => stored.push(value),
    setResetJustCompleted: (value) => justCompleted.push(value),
    scheduleResetBannerHide: () => undefined
  });

  deliver({
    status: 'canceled',
    percentComplete: 40,
    stageKey: 'signalr.dbReset.canceled',
    context: {}
  });

  // The sign-in buttons stay disabled while isResetting is true, so a cancel that left it set
  // locked the dialog until a reload.
  assert.equal(stored.at(-1).isResetting, false);
  assert.equal(stored.at(-1).status, 'canceled');
  assert.equal(stored.at(-1).message, sentence('signalr.dbReset.canceled'));
  assert.deepEqual(justCompleted, [true]);
});

test('the sign-in dialog banner says how the database reset ended', async () => {
  await translator.changeLanguage('zh');
  const completed = renderResetBanner(
    { isResetting: false, percentComplete: 100, message: 'M1', status: 'completed' },
    true
  );
  assert.ok(completed.includes(sentence('modals.auth.databaseReset.complete')));
  assert.ok(completed.includes('M1'));
  assert.ok(completed.includes('bg-success'));
  assert.ok(completed.includes('data-icon="CheckCircle"'));
  assert.ok(!completed.includes('canLoginNow'));

  // A failed reset is red; a canceled one is gray, as canceled runs are everywhere else.
  const ended = {
    canceled: {
      message: sentence('signalr.dbReset.canceled'),
      titleKey: 'modals.auth.databaseReset.canceled',
      otherTitleKey: 'modals.auth.databaseReset.notFinished',
      shows: ['bg-themed-tertiary', 'text-themed-primary', 'data-icon="Info"'],
      hides: ['bg-error', 'text-error', 'AlertCircle']
    },
    failed: {
      message: translator.t('signalr.dbReset.failed', { errorDetail: 'disk full' }),
      titleKey: 'modals.auth.databaseReset.notFinished',
      otherTitleKey: 'modals.auth.databaseReset.canceled',
      shows: ['bg-error', 'text-error-text', 'data-icon="AlertCircle"'],
      hides: ['bg-themed-tertiary', 'data-icon="Info"']
    }
  };
  for (const [status, { message, titleKey, otherTitleKey, shows, hides }] of Object.entries(
    ended
  )) {
    const shown = renderResetBanner(
      { isResetting: false, percentComplete: 0, message, status },
      true
    );
    assert.ok(shown.includes(message), status);
    for (const present of shows) {
      assert.ok(shown.includes(present), `${status} banner lacks ${present}`);
    }
    for (const absent of [...hides, 'bg-success', 'text-success', 'CheckCircle']) {
      assert.ok(!shown.includes(absent), `${status} banner shows ${absent}`);
    }
    assert.ok(shown.includes(sentence(titleKey)), status);
    for (const absentKey of [otherTitleKey, 'modals.auth.databaseReset.complete']) {
      assert.ok(!shown.includes(sentence(absentKey)), `${status} banner shows ${absentKey}`);
    }
  }

  const running = renderResetBanner(
    { isResetting: true, percentComplete: 10, message: 'M2', status: 'running' },
    false
  );
  assert.ok(running.includes(sentence('modals.auth.databaseReset.inProgress')));
  assert.ok(running.includes('M2'));
  assert.ok(running.includes('bg-warning'));
});

test('a reset that starts within 5 s of the last one ending shows its own progress', async () => {
  await translator.changeLanguage('zh');
  for (const firstEnd of ['completed', 'failed', 'canceled']) {
    let resetStatus = { isResetting: false, percentComplete: 0, message: '', status: '' };
    let resetJustCompleted = false;
    let pendingHide = null;
    const deliver = subscribeResetHandler({
      setResetStatus: (value) => (resetStatus = value),
      setResetJustCompleted: (value) => (resetJustCompleted = value),
      scheduleResetBannerHide: (callback) => (pendingHide = callback)
    });
    const assertShowsProgress = (when) => {
      const shown = renderResetBanner(resetStatus, resetJustCompleted);
      assert.ok(shown.includes(sentence('modals.auth.databaseReset.inProgress')), when);
      assert.ok(shown.includes('bg-warning'), when);
      assert.ok(shown.includes('data-icon="Database"'), when);
      for (const absent of [
        'bg-error',
        'bg-success',
        sentence('modals.auth.databaseReset.notFinished'),
        sentence('modals.auth.databaseReset.complete')
      ]) {
        assert.ok(!shown.includes(absent), `${when}: banner shows ${absent}`);
      }
    };

    deliver({
      status: firstEnd,
      percentComplete: firstEnd === 'completed' ? 100 : 0,
      stageKey: `signalr.dbReset.${firstEnd === 'completed' ? 'complete' : firstEnd}`,
      context: { errorDetail: 'disk full' }
    });
    const staleHide = pendingHide;
    // The queue starts a parked reset as soon as the first one ends, inside its 5 s banner.
    deliver({
      status: 'running',
      percentComplete: 10,
      stageKey: 'signalr.dbReset.clearingLogEntries',
      context: { deleted: 5, total: 50, percent: 10 }
    });
    assertShowsProgress(`${firstEnd} then running`);

    staleHide();
    assertShowsProgress(`${firstEnd} then running, after the first reset's hide timer`);

    deliver({
      status: 'completed',
      percentComplete: 100,
      stageKey: 'signalr.dbReset.complete',
      context: {}
    });
    const ended = renderResetBanner(resetStatus, resetJustCompleted);
    assert.ok(ended.includes(sentence('modals.auth.databaseReset.complete')), firstEnd);
    assert.ok(ended.includes('bg-success'), firstEnd);
  }
});

test('the guest button failure is not picked by its wording', () => {
  const modal = read('components/modals/auth/AuthenticationModal.tsx');
  assert.ok(!modal.includes(".includes('disabled')"));
});

// ---------------------------------------------------------------------------
// Status Check failure and cancel text
// ---------------------------------------------------------------------------

const statusCheckPath = 'src/components/features/management/status-check/StatusCheckSection.tsx';
const verdictCardPath = 'src/components/features/management/status-check/VerdictCard.tsx';

/**
 * The text and Alert color each of the card's two failure boxes shows for a stored run error, drawn
 * through the shipped expressions in the translator's current language.
 */
const drawnRunError = (runError) => {
  const card = parseSource(verdictCardPath);
  const alerts = collectNodes(
    card,
    (node) => ts.isJsxExpression(node) && node.getText(card).startsWith('{runErrorText &&')
  );
  assert.equal(alerts.length, 2, 'the empty card and the verdict card each draw the failure');
  const textDeclaration = findSoleNode(
    card,
    'runErrorText declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(card) === 'runErrorText'
  );
  const runErrorText = bindLifted(`() => (${textDeclaration.initializer.getText(card)})`, {
    runError,
    getErrorMessage,
    t: (key, options) => translator.t(key, options)
  })();
  const colorDeclaration = findSoleNode(
    card,
    'runErrorColor declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(card) === 'runErrorColor'
  );
  const runErrorColor = bindLifted(`() => (${colorDeclaration.initializer.getText(card)})`, {
    runError
  })();
  const drawn = alerts.map((alert) => {
    const colors = [];
    const text = renderToStaticMarkup(
      bindLifted(
        `() => (<>${alert.getText(card)}</>)`,
        {
          React,
          runErrorText,
          runErrorColor,
          Alert: ({ children, color }) => {
            colors.push(color);
            return React.createElement('div', null, children);
          }
        },
        { jsx: ts.JsxEmit.React }
      )()
    ).replace(/<[^>]+>/g, '');
    return { text, color: colors[0] };
  });
  assert.deepEqual(drawn[0], drawn[1]);
  return drawn[0];
};

test('a failed or canceled Status Check reads with one prefix in the reader language', async () => {
  await translator.changeLanguage('zh');
  const handlers = new Map();
  const shown = [];
  bindLifted(liftHookCallback(statusCheckPath, 'useEffect', 'handleComplete'), {
    on: (name, handler) => handlers.set(name, handler),
    off: () => undefined,
    lastFinishedOperationRef: { current: null },
    finishedOperationIdsRef: { current: new Set() },
    currentOperationRef: { current: null },
    statusVersionRef: { current: 0 },
    setIsRunning: () => undefined,
    setProgress: () => undefined,
    setRunError: (value) => shown.push(drawnRunError(value)),
    setStatus: () => undefined,
    reloadDomains: () => undefined,
    t: (key, options) => translator.t(key, options),
    keys: 'management.sections.statusCheck'
  })();
  const deliver = handlers.get('StatusCheckComplete');

  deliver({
    operationId: 'x',
    success: false,
    error: 'boom',
    result: null,
    stageKey: 'signalr.statusCheck.failed',
    context: { errorDetail: 'boom' },
    canceled: false
  });
  assert.equal(
    shown.at(-1).text,
    sentence('signalr.statusCheck.failed').replace('{{errorDetail}}', 'boom')
  );
  assert.equal(shown.at(-1).text.split('检查失败').length - 1, 1);
  assert.equal(shown.at(-1).color, 'red');

  // A cancel is not an error: its sentence sits in the gray box, not the red one.
  deliver({
    operationId: 'y',
    success: false,
    error: 'Canceled',
    result: null,
    stageKey: 'signalr.statusCheck.canceled',
    context: null,
    canceled: true
  });
  assert.equal(shown.at(-1).text, sentence('signalr.statusCheck.canceled'));
  assert.equal(shown.at(-1).color, 'neutral');

  // The card draws the sentence it was handed; the prefix lives with the writers only.
  assert.ok(
    !read('components/features/management/status-check/VerdictCard.tsx').includes('sweepFailed')
  );
  const section = read('components/features/management/status-check/StatusCheckSection.tsx');
  assert.equal(section.split('sweepFailed').length - 1, 2);
  assert.equal(section.split('resolverMode.saveFailed').length - 1, 1);
  assert.ok(!section.includes('sweepDidNotFinish'));
  assert.ok(!section.includes('setRunError(getErrorMessage('));
});

test('a failed resolver choice save says the choice was not saved, not that the check failed', async () => {
  await translator.changeLanguage('zh');
  const shown = [];
  const handleResolverModeChange = bindLifted(
    liftHookCallback(statusCheckPath, 'useCallback', 'setStatusCheckResolverMode'),
    {
      resolverMode: 'auto',
      isRunning: false,
      statusVersionRef: { current: 0 },
      handleRun: () => assert.fail('a failed save starts no check'),
      setRunError: (value) => shown.push(drawnRunError(value).text),
      setStatus: () => undefined,
      ApiService: {
        setStatusCheckResolverMode: async () => {
          throw new Error('boom');
        },
        getStatusCheck: async () => ({ resolverMode: 'auto' })
      },
      getErrorMessage,
      t: (key, options) => translator.t(key, options),
      keys: 'management.sections.statusCheck'
    }
  );
  await handleResolverModeChange('host');

  assert.equal(
    shown.at(-1),
    sentence('management.sections.statusCheck.resolverMode.saveFailed').replace('{{error}}', 'boom')
  );
  assert.ok(!shown.at(-1).includes('检查失败'));
});

test('a Status Check failure box follows a language switch, reason included', async () => {
  await translator.changeLanguage('zh');
  const stored = [];
  const setRunError = (value) => stored.push(value);
  const t = (key, options) => translator.t(key, options);
  const keys = 'management.sections.statusCheck';
  // A refusal the API names with a key, so its reason is itself translated text.
  const limited = () =>
    refusal({ error: 'Too many requests', stageKey: 'errors.rateLimit.tooManyRequests' });

  const handlers = new Map();
  bindLifted(liftHookCallback(statusCheckPath, 'useEffect', 'handleComplete'), {
    on: (name, handler) => handlers.set(name, handler),
    off: () => undefined,
    lastFinishedOperationRef: { current: null },
    finishedOperationIdsRef: { current: new Set() },
    currentOperationRef: { current: null },
    statusVersionRef: { current: 0 },
    setIsRunning: () => undefined,
    setProgress: () => undefined,
    setRunError,
    setStatus: () => undefined,
    reloadDomains: () => undefined,
    t,
    keys
  })();
  handlers.get('StatusCheckComplete')({
    operationId: 'x',
    success: false,
    error: 'boom',
    result: null,
    stageKey: 'signalr.statusCheck.failed',
    context: { errorDetail: 'boom' },
    canceled: false
  });

  const runWith = (getStatusCheck) =>
    bindLifted(liftHookCallback(statusCheckPath, 'useCallback', 'runStatusCheck'), {
      setRunError,
      setStatus: () => undefined,
      setIsRunning: () => undefined,
      setProgress: () => undefined,
      currentOperationRef: { current: null },
      lastFinishedOperationRef: { current: null },
      finishedOperationIdsRef: { current: new Set() },
      statusVersionRef: { current: 0 },
      ApiError,
      ApiService: {
        runStatusCheck: async () => {
          throw await limited();
        },
        getStatusCheck
      },
      getErrorMessage,
      t,
      keys
    });
  await runWith(async () => ({ isRunning: false, operationId: null, resolverMode: 'auto' }))();
  await runWith(async () => {
    throw new Error('resync failed');
  })();

  await bindLifted(liftHookCallback(statusCheckPath, 'useCallback', 'setStatusCheckResolverMode'), {
    resolverMode: 'auto',
    isRunning: false,
    statusVersionRef: { current: 0 },
    handleRun: () => assert.fail('a failed save starts no check'),
    setRunError,
    setStatus: () => undefined,
    ApiService: {
      setStatusCheckResolverMode: async () => {
        throw await limited();
      },
      getStatusCheck: async () => ({ resolverMode: 'auto' })
    },
    getErrorMessage,
    t,
    keys
  })('host');

  await translator.changeLanguage('en');
  const reason = sentence('errors.rateLimit.tooManyRequests');
  const checkFailed = sentence(`${keys}.sweepFailed`).replace('{{error}}', reason);
  // No Run and no resolver choice clears the box at click time; each new error replaces the last.
  assert.deepEqual(
    stored.map((value) => drawnRunError(value).text),
    [
      sentence('signalr.statusCheck.failed').replace('{{errorDetail}}', 'boom'),
      checkFailed,
      checkFailed,
      sentence(`${keys}.resolverMode.saveFailed`).replace('{{error}}', reason)
    ]
  );
});

test('a Run that another sweep refuses or outruns never shows a failure or a spinner it should not', async (t) => {
  const keys = 'management.sections.statusCheck';
  const conflict = () => failedWith(409, JSON.stringify({ error: 'A check is already running' }));
  const failure = {
    operationId: 'x',
    success: false,
    error: 'boom',
    result: null,
    stageKey: 'signalr.statusCheck.failed',
    context: { errorDetail: 'boom' },
    canceled: false
  };

  /** Run and the completion handler sharing one pair of operation refs, as in the component. */
  const section = (ApiService) => {
    const calls = { runError: [], isRunning: [], status: [] };
    const refs = {
      currentOperationRef: { current: null },
      lastFinishedOperationRef: { current: null },
      finishedOperationIdsRef: { current: new Set() },
      statusVersionRef: { current: 0 }
    };
    const setters = {
      setRunError: (value) => calls.runError.push(value),
      setIsRunning: (value) => calls.isRunning.push(value),
      setStatus: (value) => calls.status.push(value),
      setProgress: () => undefined
    };
    const handlers = new Map();
    bindLifted(liftHookCallback(statusCheckPath, 'useEffect', 'handleComplete'), {
      ...refs,
      ...setters,
      on: (name, handler) => handlers.set(name, handler),
      off: () => undefined,
      reloadDomains: () => undefined
    })();
    const run = bindLifted(liftHookCallback(statusCheckPath, 'useCallback', 'runStatusCheck'), {
      ...refs,
      ...setters,
      ApiError,
      ApiService,
      getErrorMessage,
      keys
    });
    return {
      calls,
      refs,
      setters,
      run,
      deliver: handlers.get('StatusCheckComplete'),
      deliverProgress: handlers.get('StatusCheckProgress')
    };
  };

  await t.test('a 409 whose sweep is done shows its result, not "sweep failed"', async () => {
    const { calls, run } = section({
      runStatusCheck: async () => {
        throw await conflict();
      },
      getStatusCheck: async () => ({ isRunning: false, operationId: null, resolverMode: 'auto' })
    });
    await run();
    assert.deepEqual(calls.runError, [null]);
  });

  // Read before sweep `x` finished, answered after its completion arrived.
  const stale = { isRunning: true, operationId: 'x', resolverMode: 'auto' };

  await t.test(
    'a stale status answering a refused Run does not bring the spinner back',
    async () => {
      const { calls, refs, run } = section({
        runStatusCheck: async () => {
          throw await conflict();
        },
        getStatusCheck: async () => stale
      });
      refs.lastFinishedOperationRef.current = 'x';
      refs.finishedOperationIdsRef.current.add('x');
      await run();
      assert.ok(!calls.isRunning.includes(true));
      assert.deepEqual(calls.status, []);
    }
  );

  await t.test(
    'a stale status answering the section load does not bring the spinner back',
    async () => {
      const loaded = { isRunning: [], status: [], statusError: [] };
      const currentOperationRef = { current: null };
      await bindLifted(liftHookCallback(statusCheckPath, 'useCallback', 'Promise.allSettled'), {
        mockMode: false,
        setStatus: (value) => loaded.status.push(value),
        setDomainGroups: () => undefined,
        setReloadError: () => undefined,
        setIsRunning: (value) => loaded.isRunning.push(value),
        setStatusError: (value) => loaded.statusError.push(value),
        setIsLoading: () => undefined,
        ApiService: {
          getStatusCheck: async () => stale,
          getCacheDomains: async () => ({ services: [] })
        },
        currentOperationRef,
        lastFinishedOperationRef: { current: 'x' },
        finishedOperationIdsRef: { current: new Set(['x']) },
        statusVersionRef: { current: 0 },
        isAbortError,
        getErrorMessage
      })();
      assert.ok(!loaded.isRunning.includes(true));
      assert.deepEqual(loaded.status, []);
      assert.equal(currentOperationRef.current, null);
      assert.deepEqual(loaded.statusError, [null]);
    }
  );

  await t.test('a Run that starts clears the shown box after the request, not before', async () => {
    let storedAtRequest;
    const { calls, run } = section({
      runStatusCheck: async () => {
        storedAtRequest = calls.runError.length;
        return { operationId: 'z' };
      },
      getStatusCheck: async () => assert.fail('a started Run needs no resync')
    });
    await run();
    assert.equal(storedAtRequest, 0);
    assert.deepEqual(calls.runError, [null]);
    assert.deepEqual(calls.isRunning, [true]);
  });

  await t.test('a Run reply that lands after its own sweep ended keeps that ending', async () => {
    let reply;
    const { calls, run, deliver } = section({
      runStatusCheck: () =>
        new Promise((resolve) => {
          reply = resolve;
        }),
      getStatusCheck: async () => assert.fail('a started Run needs no resync')
    });
    const running = run();
    deliver(failure);
    reply({ operationId: 'x' });
    await running;
    assert.ok(!calls.isRunning.includes(true));
    assert.deepEqual(calls.runError.at(-1), {
      stageKey: 'signalr.statusCheck.failed',
      context: { errorDetail: 'boom' }
    });
  });

  await t.test('a sweep that ends while a 409 resync is out keeps the ending it drew', async () => {
    let answer;
    let markAsked;
    const asked = new Promise((resolve) => {
      markAsked = resolve;
    });
    const { calls, run, deliver } = section({
      runStatusCheck: async () => {
        throw await conflict();
      },
      getStatusCheck: () =>
        new Promise((resolve) => {
          answer = resolve;
          markAsked();
        })
    });
    const running = run();
    await asked;
    deliver({ ...failure, operationId: 'y' });
    answer({ isRunning: false, operationId: null, resolverMode: 'auto' });
    await running;
    assert.deepEqual(calls.runError.at(-1), {
      stageKey: 'signalr.statusCheck.failed',
      context: { errorDetail: 'boom' }
    });
  });

  /** A status read held open until the test answers it, and a promise that settles once it is sent. */
  const heldStatus = () => {
    let answer;
    let markAsked;
    const asked = new Promise((resolve) => {
      markAsked = resolve;
    });
    const getStatusCheck = () =>
      new Promise((resolve) => {
        answer = resolve;
        markAsked();
      });
    return { asked, getStatusCheck, answer: (value) => answer(value) };
  };

  /** Sweep `x` ends, then sweep `y` runs and ends, all while one status read is out. */
  const twoSweepsEnd = ({ deliver, deliverProgress }) => {
    deliver({ ...failure, operationId: 'x', success: true, result: { services: [] } });
    deliverProgress({ operationId: 'y', completedDomains: 1, totalDomains: 2 });
    deliver({ ...failure, operationId: 'y', success: true, result: { services: [] } });
  };

  await t.test(
    'a status read that names an older finished sweep does not bring the spinner back on load',
    async () => {
      const held = heldStatus();
      const shown = section({ getStatusCheck: held.getStatusCheck });
      const stale = { isRunning: true, operationId: 'x', resolverMode: 'auto' };
      // The held read is dropped for being older than the two completions and read again; the
      // second read names the same finished sweep, so the finished-sweep check is what drops it.
      let statusReads = 0;
      let load;
      load = bindLifted(liftHookCallback(statusCheckPath, 'useCallback', 'Promise.allSettled'), {
        ...shown.refs,
        ...shown.setters,
        mockMode: false,
        loadAll: (signal) => load(signal),
        setDomainGroups: () => undefined,
        setReloadError: () => undefined,
        setStatusError: () => undefined,
        setIsLoading: () => undefined,
        ApiService: {
          getStatusCheck: () => {
            statusReads += 1;
            return statusReads === 1 ? held.getStatusCheck() : Promise.resolve(stale);
          },
          getCacheDomains: async () => ({ services: [] })
        },
        isAbortError,
        getErrorMessage
      });
      const loading = load();
      await held.asked;
      twoSweepsEnd(shown);
      held.answer(stale);
      await loading;
      assert.equal(statusReads, 2, 'the older read is read again');
      assert.equal(shown.calls.isRunning.at(-1), false, 'no sweep is running');
      assert.equal(shown.refs.currentOperationRef.current, null);
      assert.ok(!shown.calls.status.some((value) => value?.isRunning === true));
    }
  );

  await t.test(
    'a status read that names an older finished sweep does not bring the spinner back after a 409',
    async () => {
      const held = heldStatus();
      const shown = section({
        runStatusCheck: async () => {
          throw await conflict();
        },
        getStatusCheck: held.getStatusCheck
      });
      const running = shown.run();
      await held.asked;
      twoSweepsEnd(shown);
      held.answer({ isRunning: true, operationId: 'x', resolverMode: 'auto' });
      await running;
      assert.equal(shown.calls.isRunning.at(-1), false, 'no sweep is running');
      assert.equal(shown.refs.currentOperationRef.current, null);
      assert.ok(!shown.calls.status.some((value) => value?.isRunning === true));
    }
  );

  await t.test(
    'a 409 whose resync shows a sweep running drops an older sweep error that arrived meanwhile',
    async () => {
      const held = heldStatus();
      const shown = section({
        runStatusCheck: async () => {
          throw await conflict();
        },
        getStatusCheck: held.getStatusCheck
      });
      const running = shown.run();
      await held.asked;
      // Sweep `a` failed before this click; its events reach the page only now.
      shown.deliverProgress({ operationId: 'a', completedDomains: 0, totalDomains: 2 });
      shown.deliver({ ...failure, operationId: 'a' });
      held.answer({ isRunning: true, operationId: 'b', resolverMode: 'auto' });
      await running;
      assert.equal(shown.calls.isRunning.at(-1), true, 'sweep b is shown running');
      assert.equal(shown.calls.runError.at(-1), null, 'the older failure line is gone');
    }
  );
});

test('no sign-in, reset or prefill site shows a server sentence in front of its own', () => {
  for (const path of [
    'components/initialization/AccessSetup.tsx',
    'components/features/auth/AuthenticateTab.tsx',
    'components/features/management/steam/AuthenticationManager.tsx',
    'components/modals/auth/AuthenticationModal.tsx'
  ]) {
    assert.ok(!read(path).includes('result.message ||'), path);
  }
  for (const path of [
    'services/auth.service.ts',
    'components/initialization/steps/AdminAccountStep.tsx'
  ]) {
    assert.doesNotMatch(read(path), /data\.error \|\|/, path);
    assert.doesNotMatch(read(path), /data\.errors\?\.\[0\]\?\.message \|\|/, path);
  }
  const modal = read('components/modals/auth/AuthenticationModal.tsx');
  assert.ok(!modal.includes('event.message'));
  assert.ok(!modal.includes('resetStatus.message ||'));
  assert.ok(
    !read('components/features/prefill/PrefillProgressCard.tsx').includes('progress.message')
  );
  for (const path of ['services/auth.service.ts', 'contexts/AuthContext.types.ts']) {
    assert.ok(!read(path).includes('success: boolean; message?: string'), path);
  }
});
