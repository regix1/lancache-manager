import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * A name listed twice in SIGNALR_EVENTS registers two dispatchers for one event, both of which
 * resolve the same handler set, so every subscriber's handler runs twice per emission. That is
 * invisible in the list itself and shows up as duplicated work at the far end, which is why the
 * list is counted here rather than read.
 */

const { SIGNALR_EVENTS } = await import(
  await compileToUrl('../src/contexts/SignalRContext/types.ts')
);

test('no event name is listed twice', () => {
  const seen = new Set();
  const duplicated = SIGNALR_EVENTS.filter((name) => {
    if (seen.has(name)) return true;
    seen.add(name);
    return false;
  });

  assert.deepEqual(duplicated, [], `listed twice: ${duplicated.join(', ')}`);
  assert.equal(seen.size, SIGNALR_EVENTS.length);
});
