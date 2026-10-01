import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildTrafficKey,
  filterLivePreviews,
  getGameDisplayName,
  isResolvedGameName,
  reconcileLivePreviews
} from '../src/components/features/downloads/liveDownloadPreviews.ts';

const FIRST_SEEN = '2026-09-30T12:00:00.000Z';
const LAST_SEEN = '2026-09-30T12:00:05.000Z';
const ACTIVE_UNTIL = '2026-09-30T12:00:20.000Z';
const MEASURED_UNTIL = '2026-09-30T12:00:07.000Z';

const source = (overrides = {}) => ({
  datasources: ['primary'],
  depotIds: [731],
  firstSeenUtc: FIRST_SEEN,
  lastSeenUtc: LAST_SEEN,
  activeUntilUtc: ACTIVE_UNTIL,
  measuredUntilUtc: MEASURED_UNTIL,
  bytesPerSecond: 1_000_000,
  totalBytes: 2_000_000,
  requestCount: 3,
  cacheHitBytes: 1_000_000,
  cacheMissBytes: 1_000_000,
  ...overrides
});

const game = (overrides = {}) => ({
  key: 'steam|10.0.0.1|app:730',
  depotId: 731,
  gameName: 'Counter-Strike 2',
  gameAppId: 730,
  service: 'steam',
  clientIp: '10.0.0.1',
  bytesPerSecond: 1_000_000,
  totalBytes: 2_000_000,
  requestCount: 3,
  cacheHitBytes: 1_000_000,
  cacheMissBytes: 1_000_000,
  cacheHitPercent: 50,
  isEvicted: false,
  firstSeenUtc: FIRST_SEEN,
  lastSeenUtc: LAST_SEEN,
  activeUntilUtc: ACTIVE_UNTIL,
  sources: [source()],
  ...overrides
});

const download = (overrides = {}) => ({
  id: 1,
  service: 'steam',
  clientIp: '10.0.0.1',
  startTimeUtc: '2026-09-30T11:00:00.000Z',
  endTimeUtc: '2026-09-30T11:30:00.000Z',
  cacheHitBytes: 0,
  cacheMissBytes: 1_000,
  totalBytes: 1_000,
  cacheHitPercent: 0,
  isActive: false,
  gameName: 'Counter-Strike 2',
  gameAppId: 730,
  depotId: 731,
  datasource: 'primary',
  averageBytesPerSecond: 0,
  isEvicted: false,
  ...overrides
});

const run = ({ gameSpeeds = [], downloads = [] } = {}) =>
  reconcileLivePreviews({ gameSpeeds, windowSeconds: 2, downloads });

test('uses the required server traffic key without rebuilding it', () => {
  const row = game({ key: 'server-owned-key' });
  assert.equal(buildTrafficKey(row), 'server-owned-key');
  assert.equal(run({ gameSpeeds: [row] })[0].key, 'server-owned-key');
});

test('keeps fixed slots when rates cross', () => {
  const rows = (firstFast) => [
    game({
      key: 'steam|10.0.0.1|app:1',
      gameName: 'Baldur',
      gameAppId: 1,
      bytesPerSecond: firstFast ? 9_000_000 : 1
    }),
    game({
      key: 'epic|10.0.0.2|name:fortnite',
      service: 'epic',
      clientIp: '10.0.0.2',
      gameName: 'Fortnite',
      gameAppId: undefined,
      depotId: 0,
      sources: [source({ datasources: ['secondary'], depotIds: [] })],
      bytesPerSecond: firstFast ? 1 : 9_000_000
    })
  ];
  assert.deepEqual(
    run({ gameSpeeds: rows(true) }).map((preview) => preview.key),
    run({ gameSpeeds: rows(false) }).map((preview) => preview.key)
  );
});

test('does not let a stale active row hide a new inferred session', () => {
  assert.equal(
    run({
      gameSpeeds: [game()],
      downloads: [download({ isActive: true, endTimeUtc: null })]
    }).length,
    1
  );
});

test('hands off in the same render when a matching recorded row reaches the session start', () => {
  assert.equal(
    run({
      gameSpeeds: [game()],
      downloads: [download({ startTimeUtc: FIRST_SEEN, endTimeUtc: null })]
    }).length,
    0
  );
  assert.equal(
    run({
      gameSpeeds: [game()],
      downloads: [download({ endTimeUtc: LAST_SEEN })]
    }).length,
    0
  );
});

test('keeps a preview for a newly inserted but backfilled old row', () => {
  assert.equal(run({ gameSpeeds: [game()], downloads: [download({ id: 99 })] }).length, 1);
});

test('requires a matching datasource alias before a row can take over', () => {
  const current = game({
    sources: [source({ datasources: ['Primary', 'Mirror'] })]
  });
  assert.equal(
    run({
      gameSpeeds: [current],
      downloads: [download({ datasource: 'other', startTimeUtc: FIRST_SEEN })]
    }).length,
    1
  );
  assert.equal(
    run({
      gameSpeeds: [current],
      downloads: [download({ datasource: 'mirror', startTimeUtc: FIRST_SEEN })]
    }).length,
    0
  );
});

test('has no browser carry-over after the accepted snapshot removes an identity', () => {
  assert.equal(run({ gameSpeeds: [game()] }).length, 1);
  assert.deepEqual(run({ gameSpeeds: [] }), []);
});

test('preserves named Xbox alias matching without matching generic traffic', () => {
  const named = game({
    key: 'xboxlive|10.0.0.1|name:halo',
    service: 'xboxlive',
    gameName: 'Halo',
    gameAppId: undefined,
    depotId: 0,
    sources: [source({ datasources: ['primary'], depotIds: [] })]
  });
  assert.equal(
    run({
      gameSpeeds: [named],
      downloads: [
        download({
          service: 'wsus',
          gameName: 'Halo',
          gameAppId: undefined,
          depotId: undefined,
          startTimeUtc: FIRST_SEEN
        })
      ]
    }).length,
    0
  );

  const generic = { ...named, key: 'xboxlive|10.0.0.1|service', gameName: 'Xbox Live' };
  assert.equal(
    run({
      gameSpeeds: [generic],
      downloads: [
        download({
          service: 'wsus',
          gameName: 'Windows Update',
          gameAppId: undefined,
          depotId: undefined,
          startTimeUtc: FIRST_SEEN
        })
      ]
    }).length,
    1
  );
});

test('keeps service labels and preview filters', () => {
  assert.equal(isResolvedGameName('Epic Games', 'epic'), false);
  assert.equal(getGameDisplayName('Halo', 'xboxlive', ''), 'Halo');

  const previews = run({
    gameSpeeds: [
      game(),
      game({
        key: 'epic|127.0.0.1|name:fortnite',
        service: 'epic',
        clientIp: '127.0.0.1',
        gameName: 'Fortnite',
        gameAppId: undefined,
        depotId: 0,
        sources: [source({ datasources: ['secondary'], depotIds: [] })]
      })
    ]
  });
  assert.deepEqual(
    filterLivePreviews(previews, { serviceFilterKey: 'steam' }).map((p) => p.service),
    ['steam']
  );
  assert.deepEqual(
    filterLivePreviews(previews, { hideLocalhost: true }).map((p) => p.clientIp),
    ['10.0.0.1']
  );
});
