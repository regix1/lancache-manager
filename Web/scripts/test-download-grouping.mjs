import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

// The helper imports a path-aliased module, which a bare data-URL import cannot resolve, so the
// dependency is compiled first and its data URL substituted for the alias.
const serviceDisplayNameUrl = await compileToUrl('../src/utils/serviceDisplayName.ts');
const liveDownloadPreviewsUrl = await compileToUrl(
  '../src/components/features/downloads/liveDownloadPreviews.ts'
);
const { cacheHitPercent, toGroup } = await import(
  await compileToUrl('../src/components/features/downloads/downloadGrouping.ts', {
    '@utils/serviceDisplayName': serviceDisplayNameUrl,
    './liveDownloadPreviews': liveDownloadPreviewsUrl
  })
);

const download = (overrides = {}) => ({
  id: 42,
  service: 'steam',
  clientIp: '10.0.0.7',
  startTimeUtc: '2026-08-08T10:00:00Z',
  endTimeUtc: '2026-08-08T10:05:00Z',
  startTimeLocal: '2026-08-08T05:00:00',
  endTimeLocal: '2026-08-08T05:05:00',
  cacheHitBytes: 250,
  cacheMissBytes: 750,
  totalBytes: 1000,
  cacheHitPercent: 25,
  isActive: false,
  gameName: 'Team Fortress 2',
  averageBytesPerSecond: 100,
  isEvicted: false,
  ...overrides
});

test('wraps one download in the group shape the renderers read', () => {
  const item = download();
  const group = toGroup(item);

  assert.deepEqual(group, {
    id: 'individual-42',
    name: 'Team Fortress 2',
    type: 'game',
    service: 'steam',
    downloads: [item],
    downloadIds: [42],
    totalBytes: 1000,
    totalDownloaded: 1000,
    cacheHitBytes: 250,
    cacheMissBytes: 750,
    clientsSet: new Set(['10.0.0.7']),
    firstSeen: '2026-08-08T10:00:00Z',
    lastSeen: '2026-08-08T10:00:00Z',
    count: 1,
    isEvicted: false,
    isPartiallyEvicted: false,
    hasRealGameName: true
  });
  assert.equal(group.downloads[0], item);
});

test('answers the membership questions from the one row it wraps', () => {
  const evicted = toGroup(download({ isEvicted: true }));
  assert.equal(evicted.isEvicted, true);
  assert.equal(evicted.isPartiallyEvicted, false);

  const unnamed = toGroup(download({ gameName: 'steam' }));
  assert.equal(unnamed.hasRealGameName, false);
});

test('falls back to the service display name when the game is unidentified', () => {
  assert.equal(toGroup(download({ gameName: undefined })).name, 'steam');
  assert.equal(toGroup(download({ gameName: '', service: 'xboxlive' })).name, 'Xbox');
});

test('passes a real group straight through instead of wrapping it again', () => {
  const item = download();
  const group = toGroup(item);

  assert.equal(toGroup(group), group);
  assert.equal(toGroup(group).id, 'individual-42');
  assert.equal(toGroup(group).count, 1);
});

test('reports the cache hit share for download and group totals', () => {
  const item = download();
  const group = toGroup(item);

  assert.equal(cacheHitPercent(item.cacheHitBytes, item.totalBytes), 25);
  assert.equal(cacheHitPercent(group.cacheHitBytes, group.totalBytes), 25);
  assert.equal(cacheHitPercent(1000, 1000), 100);
});

test('reports zero percent for a zero total instead of dividing by zero', () => {
  assert.equal(cacheHitPercent(0, 0), 0);
  assert.equal(cacheHitPercent(500, 0), 0);
});
