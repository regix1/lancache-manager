import assert from 'node:assert/strict';
import test from 'node:test';
import { mountAuthWindow, signedInAnswer, unmountAuthWindow } from './auth-provider-window.mjs';

/**
 * With authentication on, a sign-out the server accepted ended the session and cleared the cookie,
 * so the window shows signed out as soon as the answer lands, without waiting for a status read. Its
 * live connection stops, and the browser's other windows are told, so they read their own status.
 * The refused answers, and every answer with authentication off, are covered by
 * `test-logout-refused-keeps-window-signed-in.mjs` and `test-logout-with-authentication-off.mjs`.
 */

test('with authentication on, a sign-out the server accepted signs the window out at once', async () => {
  try {
    const tab = await mountAuthWindow(signedInAnswer('live-session', true), 200);
    assert.equal(tab.socketOpen(), true, 'the window does not start with a live connection');
    const postedBefore = tab.posted.length;

    assert.equal(await tab.auth().logout(), true, 'the accepted sign-out was reported as refused');
    const after = tab.auth();
    assert.equal(
      after.authMode,
      'unauthenticated',
      `an accepted sign-out left the window ${after.authMode}`
    );
    assert.equal(after.sessionId, null, 'an accepted sign-out kept the session id');
    assert.equal(tab.statusReads.length, 0, 'the sign-out started a status read of its own');
    assert.equal(tab.socketOpen(), false, 'an accepted sign-out left the live connection open');
    assert.deepEqual(
      tab.posted.slice(postedBefore),
      [null],
      "an accepted sign-out did not tell the browser's other windows"
    );
    assert.deepEqual(tab.toasts, [], 'an accepted sign-out raised a notice');
  } finally {
    unmountAuthWindow();
  }
});
