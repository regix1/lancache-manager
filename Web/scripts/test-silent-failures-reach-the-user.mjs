import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import { collectNodes, compileToUrl, moduleUrl, parseSource } from './transpile-module.mjs';

/**
 * Three failures used to end at a console line nobody reads: a logout the LANCache server never
 * received, a theme list that came back short, and a preference that would not apply. Each one left
 * the screen looking like nothing had gone wrong. These check that the same failures now reach the
 * user (an action as a notification, a load as the error box of the view that asked for it), that
 * success paths stay quiet, and that LANCache logout does not mutate a separately managed Steam
 * sign-in.
 */

/** Collects every show-toast the code under test raises, so a test can count them. */
const captureToasts = () => {
  const raised = [];
  globalThis.window = {
    dispatchEvent: (event) => {
      raised.push(event.detail);
      return true;
    }
  };
  return raised;
};

const releaseWindow = () => {
  delete globalThis.window;
  delete globalThis.fetch;
};

const loadAuthService = () =>
  compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': moduleUrl(`export default { t: (k) => k };`),
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

test('a logout the server never received says so, and still signs this device out', async () => {
  const toasts = captureToasts();
  const authService = await loadAuthService();
  globalThis.fetch = async () => {
    throw new TypeError('fetch failed');
  };

  await authService.logout();

  assert.deepEqual(
    toasts,
    [{ type: 'error', message: 'auth.errors.logoutIncomplete' }],
    'a logout the server never received passed without a word to the user'
  );
  assert.equal(authService.isAuthenticated, false, 'this device was left signed in');
  assert.equal(authService.authMode, 'unauthenticated', 'authMode survived the logout');
  releaseWindow();
});

test('a logout the server accepted stays quiet', async () => {
  const toasts = captureToasts();
  const authService = await loadAuthService();
  globalThis.fetch = async () => ({ ok: true, status: 200 });

  await authService.logout();

  assert.deepEqual(toasts, [], 'a logout that worked still bothered the user');
  releaseWindow();
});

test('LANCache logout stays scoped to the LANCache session', () => {
  const file = parseSource('src/services/auth.service.ts');
  const logout = collectNodes(
    file,
    (node) => typescript.isMethodDeclaration(node) && node.name.getText(file) === 'logout'
  );

  assert.equal(logout.length, 1, 'auth.service should declare logout exactly once');
  const body = logout[0].body?.getText(file) ?? '';
  assert.match(body, /\/api\/auth\/logout/);
  assert.match(body, /auth\.errors\.logoutIncomplete/);
  assert.doesNotMatch(body, /steam/i, 'LANCache logout must not clear Steam authentication');
});

const loadThemeService = () =>
  compileToUrl('../src/services/theme.service.ts', {
    '@/i18n': moduleUrl(`export default { t: (k) => k };`),
    '../utils/constants': moduleUrl(`export const API_BASE = '/api';`),
    '@utils/constants': moduleUrl(`export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`),
    '@utils/error': moduleUrl(`export const getErrorMessage = (error) => error.message;`),
    '@utils/storage': moduleUrl(
      `export const storage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };`
    ),
    './auth.service': moduleUrl(`export default { authMode: 'authenticated' };`),
    './preferences.service': moduleUrl(
      `export default { setPreference: async () => true, loadPreferences: async () => ({}) };`
    ),
    toml: moduleUrl(`export const parse = () => ({});`),
    './themeSchema': moduleUrl(
      `export const parseThemeColors = () => ({});
       export const hexToRgba = () => '';
       export const readableTextColor = () => '';
       export const indicatorColor = () => '';`
    ),
    './apiError': moduleUrl(
      `export const assertOk = async (response) => {
         if (!response.ok) throw new Error(\`HTTP \${response.status}\`);
         return response;
       };`
    )
  }).then(async (url) => (await import(url)).default);

const THEME_LIST = [{ id: 'custom-one', format: 'toml' }];

test('a theme list the server could not answer says the list is short', async () => {
  const toasts = captureToasts();
  const themeService = await loadThemeService();
  globalThis.fetch = async () => {
    throw new TypeError('fetch failed');
  };

  const { themes, loadError } = await themeService.loadThemes();

  assert.equal(
    loadError,
    'fetch failed',
    'a theme list that never arrived looked the same as one with no custom themes in it'
  );
  assert.deepEqual(toasts, [], 'a failed load raised a popup beside its error box');
  assert.ok(themes.length > 0, 'the built-in themes stopped being returned');
  releaseWindow();
});

test('a theme list the server refused says the list is short', async () => {
  const toasts = captureToasts();
  const themeService = await loadThemeService();
  globalThis.fetch = async () => ({ ok: false, status: 500 });

  const { loadError } = await themeService.loadThemes();

  assert.equal(loadError, 'HTTP 500', 'a refused theme list passed without a word to the user');
  assert.deepEqual(toasts, [], 'a failed load raised a popup beside its error box');
  releaseWindow();
});

test('one theme that would not load says the list is short', async () => {
  const toasts = captureToasts();
  const themeService = await loadThemeService();
  globalThis.fetch = async (url) => {
    if (url.endsWith('/themes')) {
      return { ok: true, status: 200, json: async () => THEME_LIST };
    }
    throw new TypeError('fetch failed');
  };

  const { loadError } = await themeService.loadThemes();

  assert.equal(
    loadError,
    'fetch failed',
    'a theme that failed to load looked exactly like a theme nobody had created'
  );
  assert.deepEqual(toasts, [], 'a failed load raised a popup beside its error box');
  releaseWindow();
});

test('a theme list that arrived whole stays quiet', async () => {
  const toasts = captureToasts();
  const themeService = await loadThemeService();
  globalThis.fetch = async (url) => {
    if (url.endsWith('/themes')) {
      return { ok: true, status: 200, json: async () => [] };
    }
    throw new TypeError('no other call was expected');
  };

  const { loadError } = await themeService.loadThemes();

  assert.equal(loadError, null, 'a theme list that loaded still reported a failure');
  assert.deepEqual(toasts, [], 'a theme list that loaded still bothered the user');
  releaseWindow();
});

/**
 * The remaining sites sit behind a React render or a window event listener, neither of which these
 * scripts can drive, so each is checked where it is written: the catch that used to end at the
 * console now also calls the sink that puts a message on the screen.
 */
const catchBodyHolding = (relativePath, marker) => {
  const scriptKind = relativePath.endsWith('.tsx')
    ? typescript.ScriptKind.TSX
    : typescript.ScriptKind.TS;
  const sourceFile = parseSource(relativePath, scriptKind);
  const clauses = collectNodes(
    sourceFile,
    (node) => typescript.isCatchClause(node) && node.block.getText().includes(marker)
  );
  assert.equal(
    clauses.length,
    1,
    `expected exactly one catch holding ${marker} in ${relativePath}`
  );
  return clauses[0].block.getText();
};

const componentSinks = [
  {
    what: 'log counts that failed to load',
    file: 'src/components/features/management/log-processing/LogRemovalManager.tsx',
    marker: 'Failed to load log data:',
    sink: 'setLoadError(getErrorMessage(err))',
    load: true
  },
  {
    what: 'a theme the user picked that would not stick',
    file: 'src/components/features/management/theme/ThemeManager.tsx',
    marker: 'Failed to change theme:',
    sink: "notifyError(t('management.themes.notifications.failedToSave')"
  },
  {
    what: 'a preference the user just changed that would not apply',
    file: 'src/services/theme.service.ts',
    marker: 'Error handling preference change for',
    sink: "notifyThemeError('management.themes.notifications.preferenceChangeFailed')"
  }
];

for (const site of componentSinks) {
  test(`${site.what} reaches the user`, () => {
    const body = catchBodyHolding(site.file, site.marker);
    assert.ok(body.includes(site.sink), `${site.file}: the catch still ends at the console`);
    if (site.load) {
      // A failed load shows only its view's error box, never a popup beside it.
      assert.doesNotMatch(body, /onError|notifyError/, `${site.file}: the load raises a popup`);
    }
  });
}

test('the theme list a management tab could not load reaches the user', () => {
  const source = readFileSync(
    new URL('../src/components/features/management/theme/ThemeManager.tsx', import.meta.url),
    'utf8'
  );

  // The service answers a short list with its reason instead of throwing.
  assert.match(
    source,
    /const \{ themes: data, loadError: themesError \} = await themeService\.loadThemes\(\);[\s\S]*?setThemes\(data\);\s*setLoadError\(themesError\);/
  );
  assert.match(
    source,
    /<ErrorBlock\s+title=\{t\('management\.themes\.notifications\.loadFailed'\)\}\s+message=\{loadError\}/
  );
  assert.doesNotMatch(source, /notifyError\(t\('management\.themes\.notifications\.loadFailed'\)/);
});

test('session load failures distinguish initial retry from a preserved refresh snapshot', () => {
  const source = readFileSync(
    new URL('../src/components/features/user/ActiveSessions.tsx', import.meta.url),
    'utf8'
  );

  assert.match(
    source,
    /<ErrorBlock\s+title=\{t\('activeSessions\.initialLoadFailed'\)\}\s+message=\{loadError\}\s+retryLabel=\{t\('common\.retry'\)\}/
  );
  assert.match(
    source,
    /const loadFailedWithoutSnapshot =\s*loadError !== null && sessions\.length === 0 && historySessions\.length === 0;/
  );
  assert.match(source, /!loading && !loadFailedWithoutSnapshot && activeSessions\.length === 0 &&/);
  assert.doesNotMatch(source, /setRefreshFailed|activeSessions\.refreshFailed/);
});

test('every message these failures show is written in both languages', () => {
  const keys = [
    ['auth', 'errors', 'logoutIncomplete'],
    ['management', 'logRemoval', 'errors', 'loadFailed'],
    ['management', 'themes', 'notifications', 'loadFailed'],
    ['management', 'themes', 'notifications', 'preferenceChangeFailed'],
    ['activeSessions', 'initialLoadFailed'],
    ['common', 'retry']
  ];

  for (const locale of ['en', 'zh']) {
    const messages = JSON.parse(
      readFileSync(new URL(`../src/i18n/locales/${locale}.json`, import.meta.url), 'utf8')
    );
    for (const path of keys) {
      const message = path.reduce((node, segment) => node?.[segment], messages);
      assert.equal(
        typeof message,
        'string',
        `${locale}.json has no message at ${path.join('.')}, so the user would see the key`
      );
    }
  }
});
