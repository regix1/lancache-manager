import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * A status call that never got an answer must not sign the service out. AuthContext already keeps
 * its own state when checkAuth answers reachable:false, but the service flags gate the SignalR
 * connection: the AUTH_SESSION_UPDATED event AuthContext dispatches in its finally makes the
 * SignalR provider stop a live socket whenever authService.isAuthenticated reads false, and after
 * a manual stop nothing restarts it while that flag stays false. So zeroing the flags on a timeout
 * or a dropped network turned one slow status call into a dead socket that only a page reload
 * could bring back. A call the server actually answered still signs the service out.
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

const SIGNED_IN_ADMIN = {
  isAuthenticated: true,
  authenticationEnabled: true,
  sessionType: 'admin',
  sessionId: 'session-under-test',
  guestAccessEnabled: false,
  guestDurationHours: 24
};

const loadAuthService = async () => {
  const authServiceUrl = await compileToUrl('../src/services/auth.service.ts', {
    '@/i18n': moduleUrl(`export default { t: (k) => k };`),
    '@utils/constants': moduleUrl(`export const getApiUrl = () => '';`),
    '@utils/antiforgery': moduleUrl(`export const antiforgeryHeaders = () => ({});`),
    '@utils/error': moduleUrl(`export const isAbortError = (e) => e?.name === 'AbortError';`),
    '@utils/userInteractionTracker': moduleUrl(
      `export const hasRecentUserInteraction = () => false;`
    ),
    './apiError': moduleUrl(`export const assertOk = async (response) => response;`)
  });
  const { default: authService } = await import(authServiceUrl);
  return authService;
};

const signIn = async (authService) => {
  globalThis.fetch = async () => ({
    ok: true,
    status: 200,
    json: async () => SIGNED_IN_ADMIN
  });
  await authService.checkAuth();
  assert.equal(authService.isAuthenticated, true, 'the session under test never signed in');
};

const assertSessionKept = (authService, answer, wayItFailed) => {
  assert.equal(answer.reachable, false, `${wayItFailed}: the answer claims the server answered`);
  assert.equal(
    authService.isAuthenticated,
    true,
    `${wayItFailed}: the service signed itself out over a call that was never answered`
  );
  assert.equal(authService.authMode, 'authenticated', `${wayItFailed}: authMode was zeroed`);
  assert.equal(authService.sessionType, 'admin', `${wayItFailed}: sessionType was zeroed`);
  assert.equal(authService.sessionId, 'session-under-test', `${wayItFailed}: sessionId was zeroed`);
};

test('a timed-out status call keeps the last-known session on the service', async () => {
  const authService = await loadAuthService();
  await signIn(authService);

  globalThis.fetch = async () => {
    throw Object.assign(new Error('This operation was aborted'), { name: 'AbortError' });
  };
  const answer = await authService.checkAuth();

  assertSessionKept(authService, answer, 'timeout');
  delete globalThis.fetch;
});

test('a dropped network keeps the last-known session on the service', async () => {
  const authService = await loadAuthService();
  await signIn(authService);

  globalThis.fetch = async () => {
    throw new TypeError('fetch failed');
  };
  const answer = await authService.checkAuth();

  assertSessionKept(authService, answer, 'network drop');
  delete globalThis.fetch;
});

test('a status call the server answered with a failure still signs the service out', async () => {
  const authService = await loadAuthService();
  await signIn(authService);

  globalThis.fetch = async () => {
    throw new Error('HTTP 500');
  };
  const answer = await authService.checkAuth();

  assert.equal(answer.reachable, true, 'an answered call reads as unanswered');
  assert.equal(authService.isAuthenticated, false, 'a refused session is still signed in');
  assert.equal(authService.authMode, 'unauthenticated', 'a refused session keeps its authMode');
  assert.equal(authService.sessionType, null, 'a refused session keeps its sessionType');
  assert.equal(authService.sessionId, null, 'a refused session keeps its sessionId');
  delete globalThis.fetch;
});
