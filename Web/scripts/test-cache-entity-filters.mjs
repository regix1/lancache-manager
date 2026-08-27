import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const { getActiveGames, getActiveServices, getEvictedGames, getEvictedServices } = await import(
  await compileToUrl('../src/components/features/management/game-detection/cacheEntityFilters.ts')
);

const game = (overrides = {}) => ({
  game_app_id: 730,
  game_name: 'Counter-Strike 2',
  cache_files_found: 900,
  total_size_bytes: 3_000_000,
  depot_ids: [],
  sample_urls: [],
  cache_file_paths: [],
  datasources: ['default'],
  ...overrides
});

const service = (overrides = {}) => ({
  service_name: 'steam',
  cache_files_found: 1234,
  total_size_bytes: 5_000_000,
  sample_urls: [],
  cache_file_paths: [],
  datasources: ['default'],
  ...overrides
});

test('a game holding attributed bytes is listed', () => {
  assert.deepEqual(
    getActiveGames([game()]).map((g) => g.game_name),
    ['Counter-Strike 2']
  );
});

test('a game whose files a sibling claimed first is still listed', () => {
  // Attribution gives shared cache paths to whichever entity walks them first, so the second game
  // scores zero bytes while its files are still on disk and still removable. Filtering games on
  // bytes the way services are filtered would hide a title the user owns.
  assert.deepEqual(
    getActiveGames([game({ total_size_bytes: 0 })]).map((g) => g.game_name),
    ['Counter-Strike 2']
  );
});

test('a service holding attributed bytes is listed as unmapped', () => {
  assert.deepEqual(
    getActiveServices([service()]).map((s) => s.service_name),
    ['steam']
  );
});

test('a service whose files were all claimed by a game is not listed as unmapped', () => {
  // Attribution leaves the scan-time file count in place and awards the bytes to the game that
  // claimed the shared cache paths first, so this row would otherwise render as "1,234 files, 0 B".
  assert.deepEqual(getActiveServices([service({ total_size_bytes: 0 })]), []);
});

test('an evicted service is not listed as unmapped', () => {
  assert.deepEqual(getActiveServices([service({ is_evicted: true })]), []);
});

test('the evicted list still reaches a service that holds no attributed bytes', () => {
  const evicted = service({ total_size_bytes: 0, evicted_downloads_count: 3 });
  assert.deepEqual(
    getEvictedServices([evicted]).map((s) => s.service_name),
    ['steam']
  );
});

// Games and services run through one shared eviction predicate, so each entity type is pinned on
// both of the states that predicate accepts: a partial eviction counted in downloads, and a full
// one flagged on the row.
test('a partially evicted game is listed as evicted', () => {
  assert.deepEqual(
    getEvictedGames([game({ evicted_downloads_count: 2 })]).map((g) => g.game_name),
    ['Counter-Strike 2']
  );
});

test('a fully evicted game is listed as evicted', () => {
  assert.deepEqual(
    getEvictedGames([game({ is_evicted: true })]).map((g) => g.game_name),
    ['Counter-Strike 2']
  );
});

test('a fully evicted service is listed as evicted', () => {
  assert.deepEqual(
    getEvictedServices([service({ is_evicted: true })]).map((s) => s.service_name),
    ['steam']
  );
});

test('an entity with nothing evicted is absent from both evicted lists', () => {
  assert.deepEqual(getEvictedGames([game()]), []);
  assert.deepEqual(getEvictedServices([service()]), []);
});
