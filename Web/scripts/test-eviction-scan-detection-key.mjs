import assert from 'node:assert/strict';
import test from 'node:test';
import { loadNotificationModules, moduleUrl } from './transpile-module.mjs';

/**
 * The eviction scan card names its failed game detection. A detection that ended on its own reason
 * key (a timeout, a refusal) reaches the card as `detectionStageKey`, which the card translates by
 * itself; only a detection that failed with plain error text sends `detectionError`, which the card
 * prints inside "Game cache detection failed: {{errorDetail}}". Printing a key inside that sentence
 * showed the reader "Game cache detection failed: signalr.gameDetect.error.timedOut".
 */

const { detectionErrorDetail } = await loadNotificationModules(
  moduleUrl(`
export default {
  t: (key, options) => (options ? key + ' ' + JSON.stringify(options) : key)
};`)
);

test('a timed-out detection reaches the scan card as its own translated key', () => {
  assert.equal(
    detectionErrorDetail({ context: { detectionStageKey: 'signalr.gameDetect.error.timedOut' } }),
    'signalr.gameDetect.error.timedOut'
  );
});

test('a detection that failed with error text keeps the text inside the failed sentence', () => {
  assert.equal(
    detectionErrorDetail({ context: { detectionError: 'Cache index could not be read' } }),
    'signalr.gameDetect.error.fatal {"errorDetail":"Cache index could not be read"}'
  );
});

test('a scan whose detection did not fail shows no detection line', () => {
  assert.equal(detectionErrorDetail({ context: { totalProcessed: 3 } }), undefined);
});
