import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const { getCacheGrowth, getCacheGrowthEmptyState } = await import(
  await compileToUrl('../src/components/features/dashboard/widgets/cacheGrowth.ts')
);

const snapshot = (overrides = {}) => ({
  hasData: true,
  startUsedSize: 100,
  endUsedSize: 150,
  averageUsedSize: 125,
  totalCacheSize: 1000,
  snapshotCount: 2,
  isEstimate: false,
  nextSnapshotUtc: null,
  ...overrides
});

test('reports bounded cache growth, shrinkage, and no change', () => {
  assert.deepEqual(getCacheGrowth('7d', false, snapshot()), { change: 50, percent: 50 });
  assert.deepEqual(getCacheGrowth('7d', false, snapshot({ endUsedSize: 50 })), {
    change: -50,
    percent: -50
  });
  assert.deepEqual(getCacheGrowth('7d', false, snapshot({ endUsedSize: 100 })), {
    change: 0,
    percent: 0
  });
});

test('keeps zero-size boundaries accurate without a fabricated percentage', () => {
  assert.deepEqual(getCacheGrowth('7d', false, snapshot({ startUsedSize: 0, endUsedSize: 0 })), {
    change: 0,
    percent: null
  });
  assert.deepEqual(getCacheGrowth('7d', false, snapshot({ endUsedSize: 0 })), {
    change: -100,
    percent: -100
  });
});

test('requires completed bounded history for the selected range', () => {
  assert.equal(getCacheGrowth('live', false, snapshot()), null);
  assert.equal(getCacheGrowth('7d', true, snapshot()), null);
  assert.equal(getCacheGrowth('7d', false, null), null);
  assert.equal(getCacheGrowth('7d', false, snapshot({ snapshotCount: 1 })), null);
  assert.equal(getCacheGrowth('7d', false, snapshot({ hasData: false })), null);
});

test('returns no rate or projection fields', () => {
  const growth = getCacheGrowth('7d', false, snapshot());

  assert.ok(growth);
  assert.deepEqual(Object.keys(growth).sort(), ['change', 'percent']);
});

test('picks the live message on the default range regardless of snapshot data', () => {
  assert.equal(getCacheGrowthEmptyState('live', snapshot(), true, false), 'live');
  assert.equal(getCacheGrowthEmptyState('live', null, false, false), 'live');
});

test('picks the empty-cache message only when the cache itself has no data', () => {
  assert.equal(getCacheGrowthEmptyState('7d', null, false, false), 'emptyCache');
  assert.equal(
    getCacheGrowthEmptyState('7d', snapshot({ snapshotCount: 1 }), false, false),
    'emptyCache'
  );
});

test('a failed cache section is not mistaken for an empty cache', () => {
  assert.equal(getCacheGrowthEmptyState('7d', null, false, true), 'waiting');
});

test('names the next reading time on a preset range that has one, never on custom', () => {
  const withNextSnapshot = snapshot({ snapshotCount: 1, nextSnapshotUtc: '2026-08-24T18:00:00Z' });

  assert.equal(
    getCacheGrowthEmptyState('7d', withNextSnapshot, true, false),
    'waitingWithNextSnapshot'
  );
  assert.equal(getCacheGrowthEmptyState('custom', withNextSnapshot, true, false), 'waiting');
});

test('a range with too few readings and no next-reading time just waits', () => {
  assert.equal(
    getCacheGrowthEmptyState(
      '7d',
      snapshot({ snapshotCount: 1, nextSnapshotUtc: null }),
      true,
      false
    ),
    'waiting'
  );
});
