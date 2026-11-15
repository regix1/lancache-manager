import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * A tab left open past its session's end (the guest time ran out, or an admin revoked the session)
 * still sends the antiforgery token issued for that session. The server answers the sign-out with
 * 400 and `errors.antiforgery.tokenMismatch`. The server was reached, so the notice must not tell
 * the user it could not be reached and to wait until they are back online.
 */

const languageTexts = (language) =>
  JSON.parse(
    readFileSync(new URL(`../src/i18n/locales/${language}.json`, import.meta.url), 'utf8')
  );

/** The service with a translator that reads the shipped `language` file, as the user would. */
const loadSignOutService = (language) =>
  compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': moduleUrl(
      `const texts = ${JSON.stringify(languageTexts(language))};
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

const signOutReplies = {
  unreached: async () => {
    throw new TypeError('fetch failed');
  },
  antiforgeryRefused: async () => ({
    ok: false,
    status: 400,
    json: async () => ({
      error: 'The antiforgery token is missing or does not match this session',
      stageKey: 'errors.antiforgery.tokenMismatch'
    })
  })
};

/** The notices one sign-out raises in `language` when the server replies as `reply` names. */
const noticesAfterSignOut = async (language, reply) => {
  const raised = [];
  globalThis.window = {
    dispatchEvent: (event) => {
      raised.push(event.detail);
      return true;
    }
  };
  globalThis.fetch = signOutReplies[reply];
  const originalError = console.error;
  console.error = () => undefined;
  try {
    const authService = await loadSignOutService(language);
    await authService.logout();
  } finally {
    console.error = originalError;
    delete globalThis.window;
    delete globalThis.fetch;
  }
  return raised;
};

test('a sign-out the server refused with 400 does not say the server could not be reached', async () => {
  const refused = await noticesAfterSignOut('en', 'antiforgeryRefused');

  assert.equal(refused.length, 1, 'a sign-out the server refused with 400 raised no notice');
  assert.equal(refused[0].type, 'error');
  assert.doesNotMatch(
    refused[0].message,
    /^no en text/,
    `a sign-out the server refused with 400 shows a missing text: "${refused[0].message}"`
  );
  assert.doesNotMatch(
    refused[0].message,
    /could not be reached|back online/i,
    `a sign-out the server answered with 400 shows "${refused[0].message}", which says the server could not be reached`
  );
});

test('in Chinese, a sign-out refused with 400 does not show the unreached-server notice', async () => {
  const [refused] = await noticesAfterSignOut('zh', 'antiforgeryRefused');
  const [unreached] = await noticesAfterSignOut('zh', 'unreached');

  assert.ok(refused, 'a sign-out the server refused with 400 raised no notice');
  assert.doesNotMatch(refused.message, /^no zh text/, refused.message);
  assert.notEqual(
    refused.message,
    unreached.message,
    `a sign-out the server answered with 400 shows "${refused.message}", the notice for a server that could not be reached`
  );
});
