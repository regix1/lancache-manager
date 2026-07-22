import assert from 'node:assert/strict';
import test from 'node:test';
import {
  computeStickyTtlMs,
  filterLivePreviews,
  reconcileLivePreviews
} from '../src/components/features/downloads/liveDownloadPreviews.ts';

const NOW = Date.parse('2026-07-22T12:00:00Z');

const game = (overrides = {}) => ({
  depotId: 0,
  gameName: undefined,
  gameAppId: undefined,
  service: 'steam',
  clientIp: '10.0.0.1',
  bytesPerSecond: 1_000_000,
  totalBytes: 5_000_000,
  requestCount: 3,
  cacheHitBytes: 2_500_000,
  cacheMissBytes: 2_500_000,
  cacheHitPercent: 50,
  ...overrides
});

const download = (overrides = {}) => ({
  id: 1,
  service: 'steam',
  clientIp: '10.0.0.1',
  startTimeUtc: '2026-07-22T10:00:00Z',
  endTimeUtc: null,
  startTimeLocal: '',
  endTimeLocal: null,
  cacheHitBytes: 0,
  cacheMissBytes: 0,
  totalBytes: 1_000,
  cacheHitPercent: 0,
  isActive: false,
  averageBytesPerSecond: 0,
  isEvicted: false,
  ...overrides
});

const run = ({
  gameSpeeds = [],
  downloads = [],
  ledger = new Map(),
  now = NOW,
  windowSeconds = 2
}) => reconcileLivePreviews({ gameSpeeds, windowSeconds, downloads, ledger, now });

test('key priority: app id beats depot, name, and service', () => {
  const { previews } = run({
    gameSpeeds: [
      game({ gameAppId: 730, depotId: 731, gameName: 'Counter-Strike 2' }),
      game({ depotId: 881, gameName: 'Steam App 999', clientIp: '10.0.0.2' }),
      game({ service: 'epicgames', gameName: 'Fortnite', clientIp: '10.0.0.3' }),
      game({ service: 'wsus', gameName: 'Windows Update', clientIp: '10.0.0.4' })
    ]
  });

  const keys = previews.map((p) => p.key).sort();
  assert.deepEqual(keys, [
    'epicgames|10.0.0.3|name:fortnite',
    'steam|10.0.0.1|app:730',
    'steam|10.0.0.2|depot:881',
    'wsus|10.0.0.4|service'
  ]);
});

test('service-only traffic is never treated as a resolved game', () => {
  const { previews } = run({
    gameSpeeds: [game({ service: 'wsus', gameName: 'Windows Update', clientIp: '10.0.0.4' })]
  });

  assert.equal(previews.length, 1);
  assert.equal(previews[0].hasResolvedGame, false);
  assert.equal(previews[0].displayName, 'Windows Update');
});

test('same game on two clients produces two previews; same key upserts', () => {
  const first = run({
    gameSpeeds: [
      game({ gameAppId: 730, gameName: 'Counter-Strike 2', clientIp: '10.0.0.1' }),
      game({ gameAppId: 730, gameName: 'Counter-Strike 2', clientIp: '10.0.0.2' })
    ]
  });
  assert.equal(first.previews.length, 2);

  const second = run({
    gameSpeeds: [
      game({
        gameAppId: 730,
        gameName: 'Counter-Strike 2',
        clientIp: '10.0.0.1',
        bytesPerSecond: 42
      })
    ],
    ledger: first.ledger,
    now: NOW + 1000
  });
  const updated = second.previews.find((p) => p.clientIp === '10.0.0.1');
  assert.equal(updated.bytesPerSecond, 42);
  assert.equal(updated.firstSeenAt, NOW, 'firstSeenAt survives the upsert');
});

test('a fresh matching row suppresses the preview immediately', () => {
  const { previews } = run({
    gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })],
    downloads: [download({ gameAppId: 730, isActive: true })]
  });
  assert.equal(previews.length, 0);
});

test('a stale matching row does not hide live traffic until it advances', () => {
  const staleEnd = new Date(NOW - 60_000).toISOString();
  const staleRow = download({ id: 7, gameAppId: 730, endTimeUtc: staleEnd, totalBytes: 500 });

  const first = run({
    gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })],
    downloads: [staleRow]
  });
  assert.equal(first.previews.length, 1, 'stale row becomes baseline, preview stays');

  const unchanged = run({
    gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })],
    downloads: [staleRow],
    ledger: first.ledger,
    now: NOW + 1000
  });
  assert.equal(unchanged.previews.length, 1, 'unrelated refresh keeps the preview');

  const advanced = run({
    gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })],
    downloads: [{ ...staleRow, totalBytes: 999_999 }],
    ledger: unchanged.ledger,
    now: NOW + 2000
  });
  assert.equal(advanced.previews.length, 0, 'advanced fingerprint reconciles the preview');
});

test('a newly added matching row reconciles the preview', () => {
  const first = run({
    gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })],
    downloads: []
  });
  assert.equal(first.previews.length, 1);

  const second = run({
    gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })],
    downloads: [download({ id: 9, gameAppId: 730 })],
    ledger: first.ledger,
    now: NOW + 1000
  });
  assert.equal(second.previews.length, 0);
});

test('generic wsus never matches a named Xbox row; a named wsus title does', () => {
  const namedXboxRow = download({
    id: 3,
    service: 'xbox',
    gameName: 'Forza Horizon 5',
    isActive: true
  });

  const generic = run({
    gameSpeeds: [game({ service: 'wsus', gameName: 'Windows Update' })],
    downloads: [namedXboxRow]
  });
  assert.equal(generic.previews.length, 1, 'generic wsus preview stays despite the Xbox row');

  const named = run({
    gameSpeeds: [game({ service: 'wsus', gameName: 'Forza Horizon 5' })],
    downloads: [namedXboxRow]
  });
  assert.equal(named.previews.length, 0, 'same title reconciles across the wsus/xbox alias');
});

test('sticky TTL retains a briefly absent row, then drops it', () => {
  const first = run({ gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })] });
  const stickyMs = computeStickyTtlMs(2);
  assert.equal(stickyMs, 3000);

  const withinTtl = run({ gameSpeeds: [], ledger: first.ledger, now: NOW + stickyMs - 1 });
  assert.equal(withinTtl.previews.length, 1, 'row lingers within the sticky TTL');

  const pastTtl = run({ gameSpeeds: [], ledger: withinTtl.ledger, now: NOW + stickyMs + 1 });
  assert.equal(pastTtl.previews.length, 0, 'row expires after the sticky TTL');
  assert.equal(pastTtl.ledger.size, 0, 'expired identity leaves the ledger');
});

test('filters apply the view predicates to previews', () => {
  const { previews } = run({
    gameSpeeds: [
      game({ gameAppId: 730, gameName: 'Counter-Strike 2', clientIp: '10.0.0.1' }),
      game({
        service: 'xboxlive',
        gameName: 'Forza Horizon 5',
        clientIp: '10.0.0.2',
        cacheHitPercent: 90
      }),
      game({ service: 'wsus', gameName: 'Windows Update', clientIp: '127.0.0.1' }),
      game({ depotId: 881, clientIp: '10.0.0.3', cacheHitPercent: 10 })
    ]
  });

  const byService = filterLivePreviews(previews, { serviceFilterKey: 'xbox' });
  assert.deepEqual(
    byService.map((p) => p.clientIp),
    ['10.0.0.2']
  );

  const byClient = filterLivePreviews(previews, { clientFilter: { type: 'ip', ip: '10.0.0.1' } });
  assert.deepEqual(
    byClient.map((p) => p.clientIp),
    ['10.0.0.1']
  );

  const byGroup = filterLivePreviews(previews, {
    clientFilter: { type: 'group', memberIps: ['10.0.0.1', '10.0.0.2'] }
  });
  assert.equal(byGroup.length, 2);

  const bySearch = filterLivePreviews(previews, { searchQuery: 'forza' });
  assert.deepEqual(
    bySearch.map((p) => p.clientIp),
    ['10.0.0.2']
  );

  const noLocalhost = filterLivePreviews(previews, { hideLocalhost: true });
  assert.ok(noLocalhost.every((p) => p.clientIp !== '127.0.0.1'));

  const noUnknownSteam = filterLivePreviews(previews, { hideUnknownSteam: true });
  assert.ok(noUnknownSteam.every((p) => !(p.service === 'steam' && !p.hasResolvedGame)));

  const hits = filterLivePreviews(previews, { hitMissFilter: 'hit' });
  assert.ok(hits.every((p) => p.cacheHitPercent >= 50));
  assert.ok(!hits.some((p) => p.clientIp === '10.0.0.3'), 'window-miss rows drop from hit view');

  const misses = filterLivePreviews(previews, { hitMissFilter: 'miss' });
  assert.deepEqual(
    misses.map((p) => p.clientIp),
    ['10.0.0.3']
  );
});

test('previews carry no database identity and never mutate the recorded rows', () => {
  const rows = [download({ id: 5, gameAppId: 100 })];
  const frozenRow = Object.freeze({ ...rows[0] });
  const { previews } = run({
    gameSpeeds: [game({ gameAppId: 730, gameName: 'Counter-Strike 2' })],
    downloads: [frozenRow]
  });

  assert.equal(previews.length, 1);
  assert.ok(!('id' in previews[0]), 'previews are structurally distinct from Download');
  assert.equal(previews[0].status, 'in-progress');
  assert.deepEqual(frozenRow, { ...rows[0] }, 'recorded rows are untouched');
});
