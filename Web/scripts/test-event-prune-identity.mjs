import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback, moduleUrl } from './transpile-module.mjs';

/**
 * `pruneMissingEventIds` has to hand back the SAME array it was given when it removes nothing.
 *
 * The dashboard's prune effect (`EventContext.tsx:230`) watches `selectedEventIds` as well as
 * `events`, and decides whether to write by comparing references. A prune that built a fresh array
 * every time would pass that comparison on every pass: write, re-render, effect fires, write again,
 * forever. `EventCompareChart.tsx` leans on the same return, so the contract is not local to one
 * caller and cannot be checked by reading either one.
 *
 * Nothing in the function's signature says any of this, which is why it is pinned here.
 */

// The module's other export computes custom-range bounds and reaches for the timezone helpers.
// The prune under test touches neither, but the import still has to resolve.
const TIMEZONE_STUB = moduleUrl(`
  export const getDayBoundsInTimezone = () => ({ end: new Date(0) });
  export const getEffectiveTimezone = () => 'UTC';
`);

const { pruneMissingEventIds } = await import(
  await compileToUrl('../src/contexts/TimeFilterContext.utils.ts', {
    '@utils/timezone': TIMEZONE_STUB
  })
);

const EVENTS = [{ id: 1 }, { id: 2 }, { id: 3 }];

test('removing nothing hands back the same array', () => {
  const selected = [1, 3];
  assert.equal(pruneMissingEventIds(selected, EVENTS), selected);
});

test('an empty selection hands back the same array', () => {
  const selected = [];
  assert.equal(pruneMissingEventIds(selected, EVENTS), selected);
});

test('removing something hands back a different array holding the survivors', () => {
  const selected = [1, 99];
  const pruned = pruneMissingEventIds(selected, EVENTS);
  assert.notEqual(pruned, selected);
  assert.deepEqual(pruned, [1]);
});

test('a selection with nothing left hands back an empty array', () => {
  const selected = [99];
  const pruned = pruneMissingEventIds(selected, EVENTS);
  assert.notEqual(pruned, selected);
  assert.deepEqual(pruned, []);
});

/** The effect as EventContext runs it, over the real prune. */
const runPrune = ({ selectedEventIds, events, timeRange }) => {
  const writes = { selected: [], range: [] };

  bindLifted(
    liftHookCallback('src/contexts/EventContext.tsx', 'useEffect', 'pruneMissingEventIds'),
    {
      pruneMissingEventIds,
      selectedEventIds,
      events,
      timeRange,
      setSelectedEventIds: (next) => writes.selected.push(next),
      setTimeRange: (next) => writes.range.push(next)
    }
  )();

  return writes;
};

test('a prune that removes nothing writes no state, so the effect cannot re-fire itself', () => {
  const writes = runPrune({ selectedEventIds: [1, 3], events: EVENTS, timeRange: 'custom' });
  assert.deepEqual(writes.selected, []);
  assert.deepEqual(writes.range, []);
});

test('a prune that empties the selection returns the dashboard to the live view', () => {
  const writes = runPrune({ selectedEventIds: [99], events: EVENTS, timeRange: 'custom' });
  assert.deepEqual(writes.selected, [[]]);
  assert.deepEqual(writes.range, ['live']);
});

test('surviving events keep the chosen time range', () => {
  const writes = runPrune({ selectedEventIds: [1, 99], events: EVENTS, timeRange: 'custom' });
  assert.deepEqual(writes.selected, [[1]]);
  assert.deepEqual(writes.range, []);
});
