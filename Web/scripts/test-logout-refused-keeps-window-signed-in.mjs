import assert from 'node:assert/strict';
import test from 'node:test';
import {
  logOutAndWatch,
  mountAuthWindow,
  signedInAnswer,
  signOutNotice,
  unmountAuthWindow
} from './auth-provider-window.mjs';

/**
 * With authentication on, a sign-out the server refused never reached the sign-out: a 400 from the
 * antiforgery check, or a proxy's 502, 503 or 504 while the app restarts. A sign-out that got no
 * answer at all did not reach it either. The cookie stays, the session stays live, and the status
 * read that follows answers signed in on it, or gets no answer when the outage lasts. The app returns
 * the sign-in screen whenever the auth mode reads signed out (`App.tsx:653-674`), which unmounts the
 * page the person is on, so the auth mode must not pass through signed out on the way. The live
 * connection stops whenever the auth service reads signed out, with nothing to start it again while
 * the window stays signed in, so it must stay open; and the notice must be the one for that answer.
 */

const REFUSED = [
  { answer: '400 from the antiforgery check', status: 400, notice: 'logoutUnconfirmed' },
  { answer: '502 from a proxy', status: 502, notice: 'logoutUnconfirmed' },
  { answer: '503 from a proxy', status: 503, notice: 'logoutUnconfirmed' },
  { answer: '504 from a proxy', status: 504, notice: 'logoutUnconfirmed' },
  { answer: 'no answer', status: null, notice: 'logoutUnreachable' }
];

const READS = [
  { answer: 'signed in', readAnswer: signedInAnswer('live-session', true) },
  { answer: 'no answer', readAnswer: null }
];

for (const { answer, status, notice } of REFUSED) {
  for (const read of READS) {
    test(`with authentication on, a Log Out that got ${answer}, then a status read that got ${read.answer}, keeps the window signed in with its live connection`, async () => {
      try {
        const tab = await mountAuthWindow(signedInAnswer('live-session', true), status);
        assert.equal(tab.auth().authMode, 'authenticated', 'the window does not start signed in');
        assert.equal(tab.socketOpen(), true, 'the window does not start with a live connection');

        const shown = await logOutAndWatch(tab, read.readAnswer);

        const signedOutAt = shown.indexOf('unauthenticated');
        assert.equal(
          signedOutAt,
          -1,
          `a Log Out that got ${answer} showed the window signed out (step ${signedOutAt} of ${shown.join(', ')}) while its session is still live, so the page is replaced by the sign-in screen until the status read answers`
        );
        assert.equal(tab.auth().authMode, 'authenticated', 'the window does not end signed in');
        assert.equal(
          tab.socketOpen(),
          true,
          `a Log Out that got ${answer}, then a status read that got ${read.answer}, stopped the live connection of a window that stays signed in, and nothing starts it again`
        );
        assert.deepEqual(
          tab.toasts,
          [signOutNotice(notice)],
          `a Log Out that got ${answer} raised the wrong notices`
        );
      } finally {
        unmountAuthWindow();
      }
    });
  }
}
