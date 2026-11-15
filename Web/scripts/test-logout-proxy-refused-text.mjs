import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * A sign-out that the app never handled leaves the session and its cookie live, so the next page
 * load signs this browser back in. A reverse proxy answers 502, 503 or 504 while the app restarts,
 * and the app's own filters answer 400, 401, 403 or 404 before the sign-out runs. A 500 is the
 * server saying it could not end the session, with the cookie cleared or still set, and has its own
 * notice. For every other refused answer the notice must tell the user to sign out again.
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

/** The notices one sign-out raises in `language` when the answer has `status` and no JSON body. */
const noticesAfterRefusedSignOut = async (language, status) => {
  const raised = [];
  globalThis.window = {
    dispatchEvent: (event) => {
      raised.push(event.detail);
      return true;
    }
  };
  globalThis.fetch = async () => ({
    ok: false,
    status,
    json: async () => {
      throw new SyntaxError('Unexpected token < in JSON');
    },
    text: async () => `<html><body>${status}</body></html>`
  });
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

const REFUSED = [
  { status: 502, by: 'a proxy while the app restarts' },
  { status: 503, by: 'a proxy while the app restarts' },
  { status: 504, by: 'a proxy that timed out' },
  { status: 400, by: 'the antiforgery filter' },
  { status: 401, by: 'the session check' },
  { status: 403, by: 'the access check' },
  { status: 404, by: 'a proxy with no route' }
];

for (const { status, by } of REFUSED) {
  test(`a sign-out answered ${status} by ${by} tells the user to sign out again`, async () => {
    const english = await noticesAfterRefusedSignOut('en', status);
    assert.equal(english.length, 1, `a sign-out answered ${status} raised no notice`);
    assert.equal(english[0].type, 'error');
    assert.doesNotMatch(english[0].message, /^no en text/, english[0].message);
    assert.match(
      english[0].message,
      /sign out again/i,
      `a sign-out answered ${status} shows "${english[0].message}", which does not tell the user to sign out again while the session is still live`
    );

    const [chinese] = await noticesAfterRefusedSignOut('zh', status);
    assert.ok(chinese, `in Chinese, a sign-out answered ${status} raised no notice`);
    assert.doesNotMatch(chinese.message, /^no zh text/, chinese.message);
    assert.match(
      chinese.message,
      /再次登出/,
      `in Chinese, a sign-out answered ${status} shows "${chinese.message}", which does not tell the user to sign out again`
    );
  });
}
