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
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});

const { buildApiError, ApiError } = await import(apiErrorUrl);

const { getErrorMessage } = await import(
  await compileToUrl('../src/utils/error.ts', {
    '@/i18n': i18nStub,
    '../services/apiError': apiErrorUrl
  })
);

const { translateStageKeyMessage } = await import(
  await compileToUrl('../src/utils/stageKeyMessage.ts', { '@/i18n': i18nStub })
);

/** The failed response the browser gets, with the body the API wrote into it. */
const refusal = (body) =>
  buildApiError({
    status: 400,
    statusText: 'Bad Request',
    text: async () => JSON.stringify(body)
  });

/** What one refusal body reads as, for a reader of that language. */
const shownFor = async (body, language) => {
  await translator.changeLanguage(language);
  return getErrorMessage(await refusal(body));
};

test('the typed error is the one being read, not its string form', async () => {
  // Every check below would still see the sentence if `getErrorMessage` fell through to String(),
  // because the class name and the message are both in there. This one cannot: only the ApiError
  // branch answers `HTTP 500`.
  const empty = new ApiError({ message: '', status: 500, kind: 'http', body: null });
  assert.equal(getErrorMessage(empty), 'HTTP 500');
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

test('a key this build has no words for falls back to the sentence, not the key path', async () => {
  const newer = {
    error: 'A newer server refused this for a reason this build has no words for',
    stageKey: 'errors.http.somethingAddedLater'
  };

  assert.equal(await shownFor(newer, 'zh'), newer.error);
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

test('a notification card translates a refusal key instead of printing it', async () => {
  await translator.changeLanguage('zh');

  assert.equal(translateStageKeyMessage('errors.validation.failed'), zh.errors.validation.failed);
  assert.equal(translateStageKeyMessage('errors.http.timeout'), zh.errors.http.timeout);
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

/** Source text of the two expressions that screen builds its message from, as it ships. */
const sentenceExpression = findSoleNode(
  accountStep,
  'sentence declaration',
  (node) => ts.isVariableDeclaration(node) && node.name.getText(accountStep) === 'sentence'
).initializer.getText(accountStep);

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
  const sentence = bindLifted(`() => (${sentenceExpression})`, { data, t, recovering: false })();
  return bindLifted(`() => (${submitErrorExpression})`, { data, t, sentence })();
};

test('the account screen reads a refusal it has words for in the reader language', async () => {
  await translator.changeLanguage('zh');

  const refused = {
    error: 'An account already exists on this installation',
    stageKey: 'errors.accountSetup.accountExists'
  };

  assert.equal(accountStepShows(refused), zh.errors.accountSetup.accountExists);
});

test('the account screen names the password rule, not the refusal heading', async () => {
  await translator.changeLanguage('zh');

  // The whole body ValidationFilter sends when a password rule fails. The heading is now keyed, and
  // the key says no more than "Validation failed" in any language, so the operator would lose the
  // one sentence that tells them what to change if the key were preferred here.
  const brokenRule = {
    error: 'Validation failed',
    stageKey: 'errors.validation.failed',
    errors: [{ field: 'password', message: 'Password cannot exceed 256 characters' }]
  };

  assert.equal(accountStepShows(brokenRule), 'Password cannot exceed 256 characters');
});
