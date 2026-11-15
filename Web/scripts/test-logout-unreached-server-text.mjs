import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * A sign-out the server did not end leaves this tab signed in, so the notice shown then is all the
 * user has to go on. A 500 is the server saying it could not end the session,
 * with the session cookie cleared or, when the failure came before the sign-out ran, still set. When
 * the request never reaches the server the cookie stays and the next page load signs the user back
 * in. The notice for an unreached server has to say so and tell the user to sign out again; the
 * notice for a refusal must not claim the server could not be reached.
 */

const locale = (language) =>
  JSON.parse(
    readFileSync(new URL(`../src/i18n/locales/${language}.json`, import.meta.url), 'utf8')
  );

/** The service with a translator that reads the shipped `language` file, as the user would. */
const loadAuthService = (language) =>
  compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': moduleUrl(
      `const texts = ${JSON.stringify(locale(language))};
      export default {
        t: (key) => {
          const text = key.split('.').reduce((node, part) => node?.[part], texts);
          return typeof text === 'string' ? text : 'no ${language} text for ' + key;
        }
      };`
    ),
    '@utils/constants': moduleUrl(
      `export const getApiUrl = () => ''; export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`
    ),
    '@utils/antiforgery': moduleUrl(`export const antiforgeryHeaders = () => ({});`),
    '@utils/error': moduleUrl(`export const isAbortError = (e) => e?.name === 'AbortError';`),
    '@utils/userInteractionTracker': moduleUrl(
      `export const hasRecentUserInteraction = () => false;`
    ),
    './apiError': moduleUrl(
      `export class ApiError extends Error {}
      export const assertOk = async (response) => response;`
    )
  }).then(async (url) => (await import(url)).default);

const answers = {
  unreached: async () => {
    throw new TypeError('fetch failed');
  },
  refused: async () => ({ ok: false, status: 500, json: async () => ({}) })
};

/** The notices one sign-out raises in `language` when the server `answer`s as named. */
const signOutNotices = async (language, answer) => {
  const raised = [];
  globalThis.window = {
    dispatchEvent: (event) => {
      raised.push(event.detail);
      return true;
    }
  };
  globalThis.fetch = answers[answer];
  const originalError = console.error;
  console.error = () => undefined;
  try {
    const authService = await loadAuthService(language);
    await authService.logout();
  } finally {
    console.error = originalError;
    delete globalThis.window;
    delete globalThis.fetch;
  }
  return raised;
};

test('a sign-out that never reached the server tells the user to sign out again', async () => {
  const unreached = await signOutNotices('en', 'unreached');
  const refused = await signOutNotices('en', 'refused');

  assert.equal(unreached.length, 1, 'an unreached sign-out raised no notice');
  assert.equal(refused.length, 1, 'a refused sign-out raised no notice');
  assert.equal(unreached[0].type, 'error');
  assert.match(
    unreached[0].message,
    /could not be reached/i,
    `a sign-out that never reached the server shows "${unreached[0].message}", which does not say the server could not be reached`
  );
  assert.match(
    unreached[0].message,
    /sign out again/i,
    `a sign-out that never reached the server shows "${unreached[0].message}", which does not tell the user to sign out again while the session cookie is still live`
  );
  assert.doesNotMatch(
    refused[0].message,
    /could not be reached/i,
    `a sign-out the server answered 500 shows "${refused[0].message}", which says the server could not be reached`
  );
});

test('the two sign-out notices read differently in Chinese too', async () => {
  const [unreached] = await signOutNotices('zh', 'unreached');
  const [refused] = await signOutNotices('zh', 'refused');

  assert.doesNotMatch(unreached.message, /^no zh text/, unreached.message);
  assert.doesNotMatch(refused.message, /^no zh text/, refused.message);
  assert.notEqual(
    unreached.message,
    refused.message,
    `a sign-out that never reached the server and one the server refused both show "${refused.message}"`
  );
});
