import assert from 'node:assert/strict';
import test from 'node:test';
import { pruneMissingEventIds } from '../src/contexts/TimeFilterContext.utils.ts';

const event = (overrides = {}) => ({
  id: 1,
  name: 'Event',
  startTimeUtc: '2026-08-03T00:00:00Z',
  endTimeUtc: '2026-08-03T01:00:00Z',
  startTimeLocal: '2026-08-03T00:00:00',
  endTimeLocal: '2026-08-03T01:00:00',
  colorIndex: 1,
  createdAtUtc: '2026-08-03T00:00:00Z',
  ...overrides
});

test('every event gone removes every selected id', () => {
  const result = pruneMissingEventIds([1, 2], []);
  assert.deepEqual(result, []);
});

test('a partial removal keeps the survivors in their original order', () => {
  const result = pruneMissingEventIds([3, 1], [event({ id: 1 }), event({ id: 3 })]);
  assert.deepEqual(result, [3, 1]);
});

test('nothing removed returns the identical array reference', () => {
  const input = [2, 3];
  const result = pruneMissingEventIds(input, [event({ id: 2 }), event({ id: 3 })]);
  assert.strictEqual(result, input);
});

test('an empty selection is a no-op and returns the identical reference', () => {
  const input = [];
  const result = pruneMissingEventIds(input, [event({ id: 2 })]);
  assert.strictEqual(result, input);
});
