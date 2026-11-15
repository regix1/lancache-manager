import assert from 'node:assert/strict';
import test from 'node:test';
import { mountAuthWindow, signedInAnswer, unmountAuthWindow } from './auth-provider-window.mjs';

/**
 * A User sessions reset on the Data page commits its delete, then waits for each deleted session's
 * prefill containers to stop, and only after that sends `UserSessionsCleared` to every window
 * (`Api/LancacheManager/Infrastructure/Services/System/DatabaseService.cs:1184`, `:1234`, `:1308`).
 * Nothing holds sign-in back during those waits, so a person can sign in with a password or as a
 * guest while the reset is still stopping containers. Their new session was never part of the
 * reset, so the reset's event, which names the sessions it deleted, must leave that window signed in.
 *
 * The window runs the shipped auth provider (`auth-provider-window.mjs`) on an install with sign-in
 * on. The sign-in ends in the provider's own status read (`login` and `startGuestSession` both call
 * `fetchAuth`, `AuthContext.tsx:154-171`), which `refreshAuth` starts here, and the server answers
 * every status read with the new session, which it still holds. The steps run in every order the
 * sign-in and the reset's event can take. One more order covers a second tab of the same browser:
 * it still shows the deleted session when the tab that signed in tells it to read its status, and
 * the reset's event lands right after that read's answer, before the tab draws again.
 */

const DELETED_SESSION = 'deleted-session';
const NEW_SESSION = 'session-made-during-the-reset';

/** What the reset sends every window once it has stopped the deleted sessions' prefill. */
const RESET_SENT = {
  stageKey: 'signalr.dbReset.sessionsCleared.logout',
  context: {},
  clearCookies: true,
  sessionIds: [DELETED_SESSION],
  timestamp: '2026-11-14T10:00:00Z'
};

/** The status answer once the reset has deleted the window's earlier session. */
const signedOutAnswer = {
  ...signedInAnswer(DELETED_SESSION, true),
  isAuthenticated: false,
  sessionType: null,
  sessionId: null,
  accountId: null
};

/**
 * The status answer for a session of `sessionType`.
 *
 * @param {string} sessionId The session the cookie names.
 * @param {'admin' | 'user' | 'guest'} sessionType The kind of session it is.
 * @returns {Record<string, unknown>} The answer the server gives a status read for it.
 */
const sessionAnswer = (sessionId, sessionType) => ({
  ...signedInAnswer(sessionId, true),
  sessionType,
  accountId: sessionType === 'guest' ? null : `account-of-${sessionId}`
});

const SIGNED_IN_MODE = { admin: 'authenticated', user: 'authenticated', guest: 'guest' };

const STARTS = 'sign-in read starts';
const ANSWERS = 'sign-in read answers';
const EVENT = 'reset event arrives';

/**
 * Every order of the sign-in's status read and the reset's event, the read starting first. `held`
 * marks a step after which the window does not draw before the next step runs.
 */
const ORDERS = [
  { start: 'signed out', steps: [[STARTS], [ANSWERS], [EVENT]] },
  { start: 'signed out', steps: [[STARTS], [EVENT], [ANSWERS]] },
  { start: 'signed out', steps: [[EVENT], [STARTS], [ANSWERS]] },
  { start: 'on the deleted session', steps: [[STARTS], [ANSWERS, 'held'], [EVENT]] }
];

const turn = () => new Promise((resolve) => setImmediate(resolve));

test('a sign-in made while a User sessions reset stops prefill stays signed in after the reset event', async () => {
  const wrong = [];
  try {
    for (const sessionType of ['admin', 'user', 'guest']) {
      for (const { start, steps } of ORDERS) {
        const tab = await mountAuthWindow(
          start === 'signed out' ? signedOutAnswer : sessionAnswer(DELETED_SESSION, sessionType),
          200
        );
        const answer = sessionAnswer(NEW_SESSION, sessionType);
        const answerHeldReads = () => {
          while (tab.statusReads.length > 0) tab.statusReads.shift()(answer);
        };

        let signIn = null;
        for (const [step, held] of steps) {
          if (step === STARTS) signIn = tab.auth().refreshAuth();
          if (step === ANSWERS) {
            answerHeldReads();
            await signIn;
          }
          if (step === EVENT) {
            const handler = tab.signalRHandlers.get('UserSessionsCleared');
            assert.ok(handler, 'the window does not listen for UserSessionsCleared');
            handler(RESET_SENT);
          }
          if (held) continue;
          for (let draw = 0; draw < 4; draw += 1) {
            await turn();
            tab.auth();
          }
        }

        // Every later status read the window makes is answered with the session the server holds.
        for (let draw = 0; draw < 12; draw += 1) {
          answerHeldReads();
          await turn();
          tab.auth();
        }

        const after = tab.auth();
        if (after.authMode !== SIGNED_IN_MODE[sessionType] || after.sessionId !== NEW_SESSION)
          wrong.push(
            `${sessionType} sign-in, window ${start}, ${steps.map(([step, held]) => (held ? `${step} (no draw)` : step)).join(' then ')}: the window shows ${after.authMode} on session ${after.sessionId}`
          );
        unmountAuthWindow();
      }
    }
  } finally {
    unmountAuthWindow();
  }

  assert.deepEqual(
    wrong,
    [],
    `the reset's UserSessionsCleared signed out a window whose session the reset never deleted: ${wrong.join('; ')}`
  );
});
