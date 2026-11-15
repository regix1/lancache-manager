import assert from 'node:assert/strict';
import test from 'node:test';
import {
  logOutAndWatch,
  mountAuthWindow,
  signedInAnswer,
  unmountAuthWindow
} from './auth-provider-window.mjs';

/**
 * With authentication off, every browser shares one admin session, and the next request after a
 * sign-out gets that shared session again (`SessionAuthenticationHandler.cs:42-79`). So when the
 * admin clicks Log Out in Settings, the window stays signed in: the status read that follows answers
 * signed in, or gets no answer at all when the server is down. Settings watches the auth mode and
 * reports "session revoked" and moves to the Dashboard the moment it goes from signed in to signed
 * out (`AuthenticationManager.tsx:115-123`, `App.tsx:142-146`), so the auth mode the provider hands
 * the app must never pass through signed out on the way, whatever the sign-out answered. The live
 * connection stops whenever the auth service reads signed out, so it must stay open as well.
 */

for (const { answer, status, readAnswer } of [
  {
    answer: '200, the shared session kept',
    status: 200,
    readAnswer: signedInAnswer('shared-session', false)
  },
  {
    answer: '200, then a status read that got no answer',
    status: 200,
    readAnswer: null
  },
  {
    answer: '400 from the antiforgery check',
    status: 400,
    readAnswer: signedInAnswer('shared-session', false)
  },
  {
    answer: '502 from a proxy while the app restarts',
    status: 502,
    readAnswer: signedInAnswer('shared-session', false)
  },
  {
    answer: '500 from a server error',
    status: 500,
    readAnswer: signedInAnswer('shared-session', false)
  }
]) {
  test(`with authentication off, Log Out answered ${answer} keeps the window signed in`, async () => {
    try {
      const tab = await mountAuthWindow(signedInAnswer('shared-session', false), status);
      assert.equal(tab.auth().authMode, 'authenticated', 'the window does not start signed in');

      const shown = await logOutAndWatch(tab, readAnswer);

      const signedOutAt = shown.indexOf('unauthenticated');
      assert.equal(
        signedOutAt,
        -1,
        `with authentication off, Log Out answered ${answer} showed the window signed out (step ${signedOutAt} of ${shown.join(', ')}) over the live shared session, so Settings reports "session revoked" and moves to the Dashboard`
      );
      assert.equal(tab.auth().authMode, 'authenticated', 'the window does not end signed in');
      assert.equal(
        tab.socketOpen(),
        true,
        `with authentication off, Log Out answered ${answer} stopped the live connection of a window that stays signed in`
      );
    } finally {
      unmountAuthWindow();
    }
  });
}
