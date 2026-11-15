import assert from 'node:assert/strict';
import test from 'node:test';
import { mountAuthWindow, signedInAnswer, unmountAuthWindow } from './auth-provider-window.mjs';

/**
 * A User sessions reset tells every window `UserSessionsCleared` once the delete is committed
 * (`Api/LancacheManager/Infrastructure/Services/System/DatabaseService.cs`, after the completion
 * report). With sign-in on, an account window's session is gone, so it leaves the signed-in screen
 * and stops its live connection. With sign-in off, every browser shares one session that the server
 * makes again on the next request, so that window stays as it is.
 */

/**
 * What the reset sends, naming the sessions it deleted; the guests-only clear is the only sender that
 * names a session type. Sign-in on or off, the reset deletes the window's session row.
 */
const RESET_SENT = {
  stageKey: 'signalr.dbReset.sessionsCleared.logout',
  context: {},
  clearCookies: true,
  sessionIds: ['admin-session', 'shared-session'],
  timestamp: '2026-11-14T15:00:00Z'
};

test('with sign-in on, a reset signs the admin window out and stops its live connection', async () => {
  try {
    const tab = await mountAuthWindow(signedInAnswer('admin-session', true), 200);
    assert.equal(tab.auth().authMode, 'authenticated', 'the admin window does not start signed in');
    assert.equal(tab.socketOpen(), true, 'the admin window has no live connection to stop');

    tab.signalRHandlers.get('UserSessionsCleared')(RESET_SENT);

    assert.equal(
      tab.auth().authMode,
      'unauthenticated',
      'the admin window still shows the signed-in screen for a session the reset deleted'
    );
    assert.equal(
      tab.socketOpen(),
      false,
      'the admin window keeps its live connection for a session the reset deleted'
    );
  } finally {
    unmountAuthWindow();
  }
});

test('with sign-in off, a reset leaves the shared session window signed in', async () => {
  try {
    const tab = await mountAuthWindow(signedInAnswer('shared-session', false), 200);
    assert.equal(tab.auth().authMode, 'authenticated', 'the window does not start signed in');

    tab.signalRHandlers.get('UserSessionsCleared')(RESET_SENT);

    assert.equal(
      tab.auth().authMode,
      'authenticated',
      'with sign-in off the window left for a sign-in screen that has nothing to sign in with'
    );
    assert.equal(tab.socketOpen(), true, 'with sign-in off the window stopped its live connection');
  } finally {
    unmountAuthWindow();
  }
});
