import { readFileSync } from 'node:fs';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

let loads = 0;

/**
 * A fresh copy of the shipped auth service and its error module, with a translator that reads the
 * shipped `language` file, as the user would. Each call compiles a new module, so the sign-in state
 * one test leaves behind never reaches the next.
 */
export const loadAuthService = async (language) => {
  const texts = JSON.parse(
    readFileSync(new URL(`../src/i18n/locales/${language}.json`, import.meta.url), 'utf8')
  );
  const aliases = {
    '@/i18n': moduleUrl(
      `// load ${++loads}
      const texts = ${JSON.stringify(texts)};
      export default {
        t: (key) => {
          const text = key.split('.').reduce((node, part) => node?.[part], texts);
          return typeof text === 'string' ? text : 'no ${language} text for ' + key;
        }
      };`
    ),
    '@utils/constants': moduleUrl(
      `export const getApiUrl = () => '';
      export const APP_EVENTS = { SHOW_TOAST: 'show-toast', AUTH_STATE_CHANGED: 'auth-state-changed' };`
    ),
    '@utils/antiforgery': moduleUrl(`export const antiforgeryHeaders = () => ({});`),
    '@utils/error': moduleUrl(`export const isAbortError = (e) => e?.name === 'AbortError';`),
    '@utils/userInteractionTracker': moduleUrl(
      `export const hasRecentUserInteraction = () => false;`
    )
  };
  const apiError = await compileToUrl('../src/services/apiError.ts', aliases);
  const service = await compileToUrl('../src/services/auth.service.ts', {
    ...aliases,
    './apiError': apiError
  });
  return (await import(service)).default;
};

/** A fetch answer with `status` and no JSON body, as a proxy or a filter sends it. */
export const bareAnswer = (status) => ({
  ok: status >= 200 && status < 300,
  status,
  headers: new Headers(),
  json: async () => {
    throw new SyntaxError('Unexpected token < in JSON');
  },
  text: async () => `<html><body>${status}</body></html>`
});

/** What `fetch` does when the request gets no answer at all. */
export const noAnswer = () => {
  throw new TypeError('Failed to fetch');
};
