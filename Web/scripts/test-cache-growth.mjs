import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const { getCacheGrowth } = await import(
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
  assert.equal(getCacheGrowth('7d', false, snapshot({ isEstimate: true })), null);
  assert.equal(getCacheGrowth('7d', false, snapshot({ hasData: false })), null);
});

test('returns no rate or projection fields', () => {
  const growth = getCacheGrowth('7d', false, snapshot());

  assert.ok(growth);
  assert.deepEqual(Object.keys(growth).sort(), ['change', 'percent']);
});
