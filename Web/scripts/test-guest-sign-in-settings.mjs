import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

/**
 * The sign-in screen tells a visitor how long a guest session lasts and whether guest mode is
 * offered at all, and that visitor holds no session. Every guest config route requires one and
 * answers 401 to them, so both settings are read off the auth status call, which is the one auth
 * route open to a visitor.
 *
 * That makes this two links, checked one each. The service half runs: a status call carrying an
 * install that is locked and set to 24 hours has to leave those two values on the service, and the
 * guest route is answered 401 here so a test that quietly went back to it cannot pass. The provider
 * half is a React component and this repo has no component renderer, so it is read out of the
 * file's syntax tree instead.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, relativePath) =>
  ts.createSourceFile(
    fileName,
    readWebSource(relativePath),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX
  );

const collect = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

/** The initializer of a named `const`, as source text. */
const initializerOf = (sourceFile, name) => {
  const declarations = collect(
    sourceFile,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
  );
  assert.equal(declarations.length, 1, `expected exactly one ${name} declaration`);
  assert.ok(declarations[0].initializer, `${name} has no initializer`);
  return declarations[0].initializer.getText(sourceFile);
};

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

const jsonResponse = (status, body) => ({
  ok: status >= 200 && status < 300,
  status,
  json: async () => body
});

/** An install that has been locked to no guests at all and, when it opens, 24-hour sessions. */
const LOCKED_TWENTY_FOUR_HOUR_INSTALL = {
  isAuthenticated: false,
  authenticationEnabled: true,
  sessionType: null,
  sessionId: null,
  expiresAt: null,
  accountId: null,
  isMainAdmin: false,
  hasData: false,
  hasBeenInitialized: true,
  hasDataLoaded: false,
  guestAccessEnabled: false,
  guestDurationHours: 24,
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
  xboxPrefillExpiresAt: null
};

test('a visitor with no session still gets the guest settings the install is set to', async () => {
  const requested = [];

  globalThis.fetch = async (url) => {
    const path = String(url);
    requested.push(path);
    if (path.endsWith('/api/auth/status')) {
      return jsonResponse(200, LOCKED_TWENTY_FOUR_HOUR_INSTALL);
    }
    // Every guest config route requires a session, so this is what a visitor is answered.
    return jsonResponse(401, { error: 'Unauthorized' });
  };

  const authServiceUrl = await compileToUrl('../src/services/auth.service.ts', {
    '@utils/constants': moduleUrl(`export const getApiUrl = () => '';`),
    '@utils/antiforgery': moduleUrl(`export const antiforgeryHeaders = () => ({});`),
    '@utils/error': moduleUrl(`export const isAbortError = () => false;`),
    '@utils/userInteractionTracker': moduleUrl(
      `export const hasRecentUserInteraction = () => false;`
    ),
    './apiError': moduleUrl(
      `export const assertOk = async (response) => {
         if (!response.ok) {
           throw new Error('HTTP ' + response.status);
         }
         return response;
       };`
    )
  });
  const { default: authService } = await import(authServiceUrl);

  const status = await authService.checkAuth();

  assert.equal(status.isAuthenticated, false, 'the visitor is meant to have no session here');
  assert.deepEqual(requested, ['/api/auth/status'], 'a second call was made for the same settings');
  assert.equal(
    authService.guestDurationHours,
    24,
    'the sign-in screen would offer a 6-hour guest session on an install configured for 24'
  );
  assert.equal(
    authService.guestAccessEnabled,
    false,
    'the sign-in screen would offer guest mode on an install where it is locked'
  );

  delete globalThis.fetch;
});

test('the guest settings the screen shows come from the status call', () => {
  const contextSource = readWebSource('src/contexts/GuestConfigContext.tsx');
  const contextFile = parse('GuestConfigContext.tsx', 'src/contexts/GuestConfigContext.tsx');

  assert.ok(
    !contextSource.includes('/api/auth/guest/status'),
    'that route requires a session and answers a visitor 401, so the screen would keep its fallbacks'
  );

  const listeners = collect(
    contextFile,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(contextFile) === 'window.addEventListener'
  ).map((node) => node.arguments.map((argument) => argument.getText(contextFile)));

  assert.deepEqual(
    listeners,
    [['APP_EVENTS.AUTH_SESSION_UPDATED', 'applyAuthStatus']],
    'nothing carries a fresh status answer into the screen'
  );

  const handler = initializerOf(contextFile, 'applyAuthStatus');
  assert.ok(
    handler.includes('setGuestDurationHours(authService.guestDurationHours)'),
    'the duration shown is not the one the status call reported'
  );
  assert.ok(
    handler.includes('setGuestModeLocked(!authService.guestAccessEnabled)'),
    'the guest button is not gated on the lock state the status call reported'
  );
});
