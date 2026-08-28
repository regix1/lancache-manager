import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow } from './transpile-module.mjs';

/**
 * A reset that could not sign a prefill daemon out has to say so on the completion card.
 *
 * The backend has always computed that list and shipped it as `persistentLoginFailures`, and the
 * card rendered `signalr.dbReset.complete` regardless, so a reset that left Xbox signed in read as
 * "Database reset completed successfully". The stage key the backend sends on completion is that
 * same success key, which is why the failure list has to be read BEFORE the stage key rather than
 * after it - every case below sends the success stage key, so a formatter that checked it first
 * would fail the first test and pass the other three.
 */

/**
 * i18next never runs in these scripts, so the stub answers with the key it was asked for, plus the
 * interpolated service list when there is one. Asserting the key instead of the rendered English
 * keeps this test off the locale files.
 */
const formatDatabaseResetCompleteMessage = bindLifted(
  liftConstArrow(
    'src/contexts/notifications/detailMessageFormatters.ts',
    'formatDatabaseResetCompleteMessage'
  ),
  {
    i18n: {
      t: (key, options) => (options?.services === undefined ? key : `${key}:${options.services}`)
    }
  }
);

test('a reset that left a prefill login active names the services instead of claiming success', () => {
  assert.equal(
    formatDatabaseResetCompleteMessage({
      stageKey: 'signalr.dbReset.complete',
      context: { persistentLoginFailures: ['Xbox', 'Epic'] }
    }),
    'signalr.dbReset.completeWithActiveLogins:Xbox, Epic'
  );
});

test('an empty failure list reports plain success', () => {
  assert.equal(
    formatDatabaseResetCompleteMessage({
      stageKey: 'signalr.dbReset.complete',
      context: { persistentLoginFailures: [] }
    }),
    'signalr.dbReset.complete'
  );
});

test('a context without the failure list reports plain success', () => {
  assert.equal(
    formatDatabaseResetCompleteMessage({
      stageKey: 'signalr.dbReset.complete',
      context: {}
    }),
    'signalr.dbReset.complete'
  );
});

test('no context at all reports plain success', () => {
  assert.equal(
    formatDatabaseResetCompleteMessage({ stageKey: 'signalr.dbReset.complete' }),
    'signalr.dbReset.complete'
  );
});
