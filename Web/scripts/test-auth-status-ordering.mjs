import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

const response = (body) => ({
  ok: true,
  status: 200,
  json: async () => body
});

const status = (overrides) => ({
  isAuthenticated: false,
  authenticationEnabled: true,
  sessionType: null,
  sessionId: null,
  expiresAt: null,
  accountId: null,
  isMainAdmin: false,
  hasData: true,
  hasBeenInitialized: true,
  hasDataLoaded: true,
  guestAccessEnabled: true,
  guestDurationHours: 6,
  prefillEnabled: false,
  prefillExpiresAt: null,
  steamPrefillEnabled: false,
  steamPrefillExpiresAt: null,
  epicPrefillEnabled: false,
  epicPrefillExpiresAt: null,
  battlenetPrefillEnabled: false,
  battlenetPrefillExpiresAt: null,
  riotPrefillEnabled: false,
  riotPrefillExpiresAt: null,
  xboxPrefillEnabled: false,
  xboxPrefillExpiresAt: null,
  ...overrides
});

/**
 * Auth checks come from several independent effects. A check started for an expired guest can
 * therefore overlap the account check made by a successful sign-in. Keeping them in request order
 * prevents both its state and its Set-Cookie header from arriving after the account response.
 */
test('an older guest status answer cannot overwrite a newer account status', async (t) => {
  const requests = [];
  globalThis.fetch = () =>
    new Promise((resolve) => {
      requests.push(resolve);
    });
  t.after(() => {
    delete globalThis.fetch;
  });

  const authServiceUrl = await compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': moduleUrl(`export default { t: (key) => key };`),
    '@utils/constants': moduleUrl(
      `export const getApiUrl = () => ''; export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`
    ),
    '@utils/antiforgery': moduleUrl(`export const antiforgeryHeaders = () => ({});`),
    '@utils/error': moduleUrl(`export const isAbortError = () => false;`),
    '@utils/userInteractionTracker': moduleUrl(
      `export const hasRecentUserInteraction = () => false;`
    ),
    './apiError': moduleUrl(`export const assertOk = async (value) => value;`)
  });
  const { default: authService } = await import(authServiceUrl);

  const olderGuestCheck = authService.checkAuth();
  const newerAccountCheck = authService.checkAuth();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(requests.length, 1, 'the newer auth check overlapped the older request');

  requests[0](response(status({})));
  await olderGuestCheck;
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(
    requests.length,
    2,
    'the newer auth check never ran after the older request settled'
  );

  requests[1](
    response(
      status({
        isAuthenticated: true,
        sessionType: 'admin',
        sessionId: 'account-session',
        accountId: 'account'
      })
    )
  );
  await newerAccountCheck;

  assert.equal(
    authService.authMode,
    'authenticated',
    'the expired guest answer signed out the account that completed after it started'
  );
  assert.equal(authService.sessionType, 'admin');
  assert.equal(authService.sessionId, 'account-session');
});

test('a status probe cannot log out the session returned by the following refresh', () => {
  const source = readFileSync(
    new URL(
      '../src/components/features/management/steam/AuthenticationManager.tsx',
      import.meta.url
    ),
    'utf8'
  );
  const sourceFile = ts.createSourceFile(
    'AuthenticationManager.tsx',
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX
  );
  let checkAuth;
  const visit = (node) => {
    if (
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'checkAuth' &&
      node.initializer
    ) {
      checkAuth = node.initializer;
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);

  assert.ok(checkAuth, 'the settings auth probe was not found');
  assert.ok(
    !checkAuth.getText(sourceFile).includes('authService.logout('),
    'the older probe can revoke the newer session returned by refreshAuth'
  );
});
