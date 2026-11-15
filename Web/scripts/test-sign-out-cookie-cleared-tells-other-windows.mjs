import assert from 'node:assert/strict';
import test from 'node:test';
import {
  mountAuthWindow,
  signedInAnswer,
  signOutNotice,
  unmountAuthWindow
} from './auth-provider-window.mjs';

/**
 * With authentication on and the database down, a sign-out cannot end the session, but the server
 * clears the browser's cookie and answers 500 with the code `sessionCookieCleared`. No window of the
 * browser holds a cookie any more, so the window that signed out shows signed out and tells the
 * browser's other windows, which then read their own status. It still warns that the sign-out did
 * not finish and shows no success. A 500 with no code came from a failure before the sign-out ran,
 * so the cookie is still set and the window stays signed in without telling anyone.
 */

test('with authentication on, a sign-out that cleared the cookie but could not end the session signs every window out', async () => {
  try {
    const tab = await mountAuthWindow(signedInAnswer('live-session', true), 500);
    const harnessFetch = globalThis.fetch;
    globalThis.fetch = async (url) =>
      url.endsWith('/api/auth/logout')
        ? {
            ok: false,
            status: 500,
            headers: new Headers(),
            json: async () => ({
              error: 'Signed out on this device, but the session could not be ended on the server.',
              code: 'sessionCookieCleared'
            })
          }
        : harnessFetch(url);
    assert.equal(tab.socketOpen(), true, 'the window does not start with a live connection');
    const postedBefore = tab.posted.length;

    assert.equal(
      await tab.auth().logout(),
      false,
      'a sign-out that could not end the session was reported as ended, so Settings shows a success'
    );
    const after = tab.auth();
    assert.deepEqual(
      tab.posted.slice(postedBefore),
      [null],
      "a sign-out that cleared the cookie did not tell the browser's other windows, so they stay signed in with live connections"
    );
    assert.equal(
      after.authMode,
      'unauthenticated',
      `a sign-out that cleared the cookie left the window ${after.authMode}`
    );
    assert.equal(after.sessionId, null, 'a sign-out that cleared the cookie kept the session id');
    assert.equal(
      tab.socketOpen(),
      false,
      'a sign-out that cleared the cookie left the live connection open'
    );
    assert.deepEqual(
      tab.toasts,
      [signOutNotice('logoutIncomplete')],
      'a sign-out that could not end the session raised the wrong notices'
    );
  } finally {
    unmountAuthWindow();
  }
});

test('with authentication on, a 500 with no code keeps the window signed in and tells no other window', async () => {
  try {
    const tab = await mountAuthWindow(signedInAnswer('live-session', true), 500);
    const harnessFetch = globalThis.fetch;
    globalThis.fetch = async (url) =>
      url.endsWith('/api/auth/logout')
        ? {
            ok: false,
            status: 500,
            headers: new Headers(),
            json: async () => ({ error: 'The session could not be ended on the server.' })
          }
        : harnessFetch(url);
    const postedBefore = tab.posted.length;

    assert.equal(await tab.auth().logout(), false, 'a 500 sign-out was reported as ended');
    const after = tab.auth();
    assert.deepEqual(
      tab.posted.slice(postedBefore),
      [],
      'a 500 that left the cookie set told the other windows they signed out'
    );
    assert.equal(
      after.authMode,
      'authenticated',
      `a 500 with no code left the window ${after.authMode}`
    );
    assert.equal(tab.socketOpen(), true, 'a 500 with no code stopped the live connection');
    assert.deepEqual(
      tab.toasts,
      [signOutNotice('logoutIncomplete')],
      'a 500 sign-out raised the wrong notices'
    );
  } finally {
    unmountAuthWindow();
  }
});
