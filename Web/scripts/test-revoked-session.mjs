import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

/**
 * When an admin revokes a session the browser holding it is pushed UserSessionRevoked, and the
 * handler in AuthContext clears the auth state. Clearing the component's own state is only half of
 * it. Every other module asks the auth service whether it is signed in, and the SignalR connection
 * is torn down by the AUTH_SESSION_UPDATED event rather than by the server, which leaves an
 * already-open hub connection alone. So unless the handler updates the service and dispatches that
 * event, the revoked tab still reports itself signed in and keeps receiving download activity until
 * someone reloads the page.
 *
 * This repo has no component renderer, so the handler is taken out of the file's syntax tree and run
 * against the real auth service with the state setters stubbed.
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

const collect = (root, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(root, visit);
  return found;
};

/** The initializer of a named `const`, as a node. */
const initializerOf = (sourceFile, name) => {
  const declarations = collect(
    sourceFile,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
  );
  assert.equal(declarations.length, 1, `expected exactly one ${name} declaration`);
  assert.ok(declarations[0].initializer, `${name} has no initializer`);
  return declarations[0].initializer;
};

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/** The part of the status answer the auth service keeps: an admin on a 24-hour, guest-locked install. */
const SIGNED_IN_ADMIN = {
  isAuthenticated: true,
  authenticationEnabled: true,
  sessionType: 'admin',
  sessionId: 'session-under-test',
  guestAccessEnabled: false,
  guestDurationHours: 24
};

test('a revoked session stops reporting itself signed in and drops its live feed', async () => {
  globalThis.fetch = async () => ({
    ok: true,
    status: 200,
    json: async () => SIGNED_IN_ADMIN
  });

  const authServiceUrl = await compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': moduleUrl(`export default { t: (k) => k };`),
    '@utils/constants': moduleUrl(
      `export const getApiUrl = () => ''; export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };`
    ),
    '@utils/antiforgery': moduleUrl(`export const antiforgeryHeaders = () => ({});`),
    '@utils/error': moduleUrl(`export const isAbortError = () => false;`),
    '@utils/userInteractionTracker': moduleUrl(
      `export const hasRecentUserInteraction = () => false;`
    ),
    './apiError': moduleUrl(`export const assertOk = async (response) => response;`)
  });
  const { default: authService } = await import(authServiceUrl);

  await authService.checkAuth();
  assert.equal(authService.isAuthenticated, true, 'the session under test never signed in');

  const contextFile = parse('AuthContext.tsx', 'src/contexts/AuthContext.tsx');
  const handler = initializerOf(contextFile, 'clearAuthState');

  // Everything the handler calls by name is a state setter of the component it lives in, apart from
  // the event dispatch, which is what the SignalR teardown listens for.
  const dispatched = [];
  const calledNames = [
    ...new Set(
      collect(handler, (node) => ts.isCallExpression(node) && ts.isIdentifier(node.expression)).map(
        (node) => node.expression.text
      )
    )
  ];
  const clearAuthState = new Function(
    'authService',
    ...calledNames,
    `return (${handler.getText(contextFile)});`
  )(
    authService,
    ...calledNames.map((name) =>
      name === 'notifyAuthSessionUpdated' ? () => dispatched.push(name) : () => undefined
    )
  );

  clearAuthState();

  assert.equal(
    authService.isAuthenticated,
    false,
    'the revoked tab still reports itself signed in to every module that asks the service'
  );
  assert.equal(authService.authMode, 'unauthenticated', 'the revoked tab still reads as signed in');
  assert.equal(authService.sessionType, null, 'the revoked session type is still on the service');
  assert.equal(authService.sessionId, null, 'the revoked session id is still on the service');
  assert.deepEqual(
    dispatched,
    ['notifyAuthSessionUpdated'],
    'nothing tells the SignalR connection the session ended, so the revoked tab keeps receiving downloads'
  );
  assert.equal(
    authService.guestDurationHours,
    24,
    'the sign-in screen this lands on would offer a 6-hour guest session on an install set to 24'
  );
  assert.equal(
    authService.guestAccessEnabled,
    false,
    'the sign-in screen this lands on would offer guest mode on an install where it is locked'
  );

  delete globalThis.fetch;
});
