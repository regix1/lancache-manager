import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

// gameDetection.ts imports nothing but types, so it compiles to a data URL with no alias to substitute.
const { buildGamesOnDiskDisplayStats } = await import(
  await compileToUrl('../src/utils/gameDetection.ts')
);

// The Games on Disk card shows game bytes as its value, the Services on Disk card beside it shows
// service bytes, and the unmapped remainder is scan total minus identified bytes, so these three
// figures are the whole of what a reader is given to account for the cache scan total.
const CACHE_SCAN_TOTAL = 1000000;

const detection = (overrides = {}) => ({
  hasCachedResults: true,
  games_on_disk_bytes: 600000,
  games_on_disk_count: 12,
  identified_cache_bytes: 930000,
  identified_service_bytes: 330000,
  ...overrides
});

test('game, service and unmapped bytes account for the whole cache scan total', () => {
  const payload = detection();
  const stats = buildGamesOnDiskDisplayStats(payload);
  const unmapped = CACHE_SCAN_TOTAL - payload.identified_cache_bytes;

  assert.equal(stats.totalSize + stats.serviceSize, payload.identified_cache_bytes);
  assert.equal(stats.totalSize + stats.serviceSize + unmapped, CACHE_SCAN_TOTAL);
});

test('reports the service bucket straight from the payload', () => {
  const stats = buildGamesOnDiskDisplayStats(detection());

  assert.equal(stats.totalSize, 600000);
  assert.equal(stats.serviceSize, 330000);
});

// A cleared cache still carries a cached detection result, so every figure comes back zero. Zero is
// falsy, so the cards have to keep reading a size instead of vanishing, and the remainder has to
// land on zero rather than a negative number or NaN.
test('an empty cache reads as zero and leaves no unmapped remainder', () => {
  const payload = detection({
    games_on_disk_bytes: 0,
    games_on_disk_count: 0,
    identified_cache_bytes: 0,
    identified_service_bytes: 0
  });
  const stats = buildGamesOnDiskDisplayStats(payload);
  // The cache scan total is zero too when there is nothing on disk.
  const unmapped = 0 - payload.identified_cache_bytes;

  assert.equal(stats.totalSize, 0);
  assert.equal(stats.serviceSize, 0);
  assert.equal(stats.gameCount, 0);
  assert.equal(stats.totalSize + stats.serviceSize + unmapped, 0);
});

test('treats a missing service figure as zero so the services card still reads a size', () => {
  const payload = detection({
    identified_service_bytes: undefined,
    identified_cache_bytes: 600000
  });
  const stats = buildGamesOnDiskDisplayStats(payload);

  assert.equal(stats.serviceSize, 0);
  assert.equal(stats.totalSize + stats.serviceSize, payload.identified_cache_bytes);
});
