import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * The clock a reader picks stays on screen until the save behind it answers, so the save has to
 * answer. A request left on a connection that is never reset has no end of its own: the socket sits
 * open, the promise never settles, and the picked clock is held for as long as the tab lives, over
 * every later change the server sends. The deadline on the request is what makes the failure
 * arrive.
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

const CLOCK = { useUtcTimezone: true, useLocalTimezone: false, use24HourFormat: true };

/** The service, wired to stubs for everything it imports that is not the clock save itself. */
const loadPreferencesService = async () => {
  const constants = moduleUrl(
    `export const API_BASE = '/api';
     export const APP_EVENTS = { PREFERENCES_RESET: 'preferences-reset' };`
  );
  const url = await compileToUrl('../src/services/preferences.service.ts', {
    '../utils/constants': constants,
    '@utils/constants': constants,
    './api.service': moduleUrl(
      `export default class ApiService {
         static getJsonFetchOptions(body, options = {}) {
           return { ...options, body: JSON.stringify(body) };
         }
       }`
    ),
    '@/types/userPreferences': await compileToUrl('../src/types/userPreferences.ts')
  });
  const { default: preferencesService } = await import(url);
  return preferencesService;
};

/** Turns of the event loop, to tell an answer that is slow from one that is never coming. */
const noAnswer = async () => {
  for (let turn = 0; turn < 5; turn += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  return 'no answer';
};

test('a clock save that is answered carries a deadline and reports what the server said', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });

  const signals = [];
  globalThis.fetch = async (url, options) => {
    signals.push(options.signal);
    return { ok: true };
  };

  const preferencesService = await loadPreferencesService();
  const answer = await Promise.race([preferencesService.setClockPreferences(CLOCK), noAnswer()]);

  assert.ok(signals[0], 'the request has to carry the deadline, or nothing can end it');
  assert.equal(
    answer,
    'saved',
    'the deadline must not get in the way of a save the server answered'
  );

  delete globalThis.fetch;
});

/**
 * A refusal and a silence are different news. Told apart, the reader can be given a message that
 * claims only what the client knows; told the same, a save that is still committing is reported as
 * one that went nowhere and the broadcast then moves the clock anyway.
 */
test('a clock save the server refuses is told apart from one it never answered', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });

  globalThis.fetch = async () => ({ ok: false, status: 403 });

  const preferencesService = await loadPreferencesService();
  const answer = await Promise.race([preferencesService.setClockPreferences(CLOCK), noAnswer()]);

  assert.equal(answer, 'failed', 'a status the server sent back is an answer, not a silence');

  delete globalThis.fetch;
});

/**
 * Runs second, because the save it leaves outstanding is one every later clock save queues behind.
 */
test('a clock save nothing ever answers ends by itself', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });

  const requests = [];
  globalThis.fetch = (url, options) => {
    requests.push(String(url));
    // The connection the browser never gets an answer on and is never told about.
    return new Promise((_, reject) => {
      options.signal?.addEventListener('abort', () => reject(new Error('aborted')));
    });
  };

  const preferencesService = await loadPreferencesService();
  const saving = preferencesService.setClockPreferences(CLOCK);
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(
    requests.length,
    1,
    'the save has to have gone out before the deadline is waited on'
  );
  t.mock.timers.tick(10000);

  const answer = await Promise.race([saving, noAnswer()]);
  assert.equal(
    answer,
    'noAnswer',
    'a save with no answer has to end, and has to end as the silence it was'
  );

  delete globalThis.fetch;
});
