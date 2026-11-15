import assert from 'node:assert/strict';
import test from 'node:test';
import { bareAnswer, loadAuthService, noAnswer } from './sign-out-auth-service.mjs';

/**
 * With authentication on, the user clicks Log Out while the server restarts or the network is down.
 * The sign-out gets no answer or a refusal, and the status read that follows gets no answer either,
 * so the window stays drawn signed in on its live session. The main socket reads `authService` on
 * every session update (`Web/src/contexts/SignalRContext/index.tsx:288`) and stops when it reads
 * signed out, with nothing left to start it again; so after these answers the service must still
 * hold the session the window shows.
 */

const SESSION_ID = 'session-held-by-this-browser';

const signedInStatus = () => ({
  ok: true,
  status: 200,
  headers: new Headers(),
  json: async () => ({
    isAuthenticated: true,
    authenticationEnabled: true,
    authenticationSetupRequired: false,
    sessionType: 'admin',
    sessionId: SESSION_ID,
    guestAccessEnabled: true,
    guestDurationHours: 6
  })
});

const SIGN_OUT_ANSWERS = [
  { answer: 'no answer', respond: noAnswer },
  { answer: '400 from the antiforgery check', respond: () => bareAnswer(400) },
  { answer: '502 from a proxy', respond: () => bareAnswer(502) },
  { answer: '503 from a proxy', respond: () => bareAnswer(503) },
  { answer: '504 from a proxy', respond: () => bareAnswer(504) }
];

const STATUS_READ_ANSWERS = [
  { answer: 'no answer', respond: noAnswer },
  { answer: '503 from a proxy', respond: () => bareAnswer(503) }
];

for (const signOut of SIGN_OUT_ANSWERS) {
  for (const statusRead of STATUS_READ_ANSWERS) {
    test(`a sign-out that got ${signOut.answer}, then a status read that got ${statusRead.answer}, keeps the socket's session`, async () => {
      globalThis.window = { dispatchEvent: () => true };
      const answers = [signedInStatus, signOut.respond, statusRead.respond];
      globalThis.fetch = async () => answers.shift()();
      const originalError = console.error;
      const originalWarn = console.warn;
      console.error = () => undefined;
      console.warn = () => undefined;
      try {
        const authService = await loadAuthService('en');
        await authService.checkAuth();
        assert.equal(authService.isAuthenticated, true);

        assert.equal(await authService.logout(), 'unchanged');
        assert.equal((await authService.checkAuth()).reachable, false);

        const seenBySocket = {
          isAuthenticated: authService.isAuthenticated,
          authenticationSetupRequired: authService.authenticationSetupRequired,
          authMode: authService.authMode,
          sessionId: authService.sessionId
        };
        assert.deepEqual(
          seenBySocket,
          {
            isAuthenticated: true,
            authenticationSetupRequired: false,
            authMode: 'authenticated',
            sessionId: SESSION_ID
          },
          `after a sign-out that got ${signOut.answer} and a status read that got ${statusRead.answer}, the window stays signed in, but the auth service reads ${JSON.stringify(seenBySocket)}, so the next session update stops the live connection and nothing starts it again`
        );
      } finally {
        console.error = originalError;
        console.warn = originalWarn;
        delete globalThis.window;
        delete globalThis.fetch;
      }
    });
  }
}
