import assert from 'node:assert/strict';
import test from 'node:test';
import {
  applyDashboardBatchResponse,
  buildRangeKey
} from '../src/contexts/DashboardDataContext/applyBatchResponse.ts';

const slices = (overrides = {}) => ({
  cacheInfo: null,
  clientStats: [],
  serviceStats: [],
  dashboardStats: null,
  latestDownloads: [],
  sparklines: null,
  hourlyActivity: null,
  cacheSnapshot: null,
  cacheGrowth: null,
  ...overrides
});

// A batch where every sub-query succeeded. null on any key means that
// sub-query failed server-side; [] / hasData:false are successful results.
const fullBatch = (overrides = {}) => ({
  cache: { totalCacheSize: 1000 },
  clients: [{ clientIp: '10.0.0.1' }],
  services: [{ service: 'steam' }],
  dashboard: { period: { duration: 'live' } },
  downloads: [{ id: 10 }, { id: 11 }],
  detection: { hasCachedResults: false },
  sparklines: { intervals: [] },
  hourlyActivity: { hours: [] },
  cacheSnapshot: { hasData: true },
  cacheGrowth: { points: [] },
  ...overrides
});

const LIVE_KEY = buildRangeKey(undefined, undefined, undefined);
const DAY_KEY = buildRangeKey(1_700_000_000, 1_700_086_400, undefined);

test('failed downloads section keeps the previous list within the same range', () => {
  const prevDownloads = [{ id: 1 }, { id: 2 }];
  const prev = slices({ latestDownloads: prevDownloads });
  const { next, hadPartialFailure, failedSectionKeys } = applyDashboardBatchResponse(
    prev,
    fullBatch({ downloads: null }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.latestDownloads, prevDownloads);
  assert.equal(hadPartialFailure, true);
  assert.ok(failedSectionKeys.includes('downloads'));
});

test('failed clients and services sections keep previous within the same range', () => {
  const prevClients = [{ clientIp: '10.0.0.9' }];
  const prevServices = [{ service: 'epic' }];
  const prev = slices({ clientStats: prevClients, serviceStats: prevServices });
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    prev,
    fullBatch({ clients: null, services: null }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.clientStats, prevClients);
  assert.equal(next.serviceStats, prevServices);
  assert.equal(hadPartialFailure, true);
});

test('failed sections clear on a range change instead of keeping foreign-range data', () => {
  const prev = slices({
    latestDownloads: [{ id: 1 }],
    clientStats: [{ clientIp: '10.0.0.9' }],
    dashboardStats: { period: { duration: '24h' } },
    sparklines: { intervals: [1] },
    hourlyActivity: { hours: [1] },
    cacheGrowth: { points: [1] }
  });
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    prev,
    fullBatch({
      downloads: null,
      clients: null,
      dashboard: null,
      sparklines: null,
      hourlyActivity: null,
      cacheGrowth: null
    }),
    { rangeKey: LIVE_KEY, previousRangeKey: DAY_KEY }
  );
  assert.deepEqual(next.latestDownloads, []);
  assert.deepEqual(next.clientStats, []);
  assert.equal(next.dashboardStats, null);
  assert.equal(next.sparklines, null);
  assert.equal(next.hourlyActivity, null);
  assert.equal(next.cacheGrowth, null);
  assert.equal(hadPartialFailure, true);
});

test('successful empty lists apply over previous data', () => {
  const prev = slices({
    latestDownloads: [{ id: 1 }],
    clientStats: [{ clientIp: '10.0.0.9' }]
  });
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    prev,
    fullBatch({ downloads: [], clients: [] }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.deepEqual(next.latestDownloads, []);
  assert.deepEqual(next.clientStats, []);
  assert.equal(hadPartialFailure, false);
});

test('live cacheSnapshot with hasData false applies as a successful result', () => {
  const prev = slices({ cacheSnapshot: { hasData: true, files: 5 } });
  const snapshot = { hasData: false };
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    prev,
    fullBatch({ cacheSnapshot: snapshot }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.cacheSnapshot, snapshot);
  assert.equal(hadPartialFailure, false);
});

test('failed cacheSnapshot keeps previous in the same range and clears on range change', () => {
  const prevSnapshot = { hasData: true, files: 5 };
  const sameRange = applyDashboardBatchResponse(
    slices({ cacheSnapshot: prevSnapshot }),
    fullBatch({ cacheSnapshot: null }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(sameRange.next.cacheSnapshot, prevSnapshot);
  assert.ok(sameRange.failedSectionKeys.includes('cacheSnapshot'));
  const rangeChange = applyDashboardBatchResponse(
    slices({ cacheSnapshot: prevSnapshot }),
    fullBatch({ cacheSnapshot: null }),
    { rangeKey: DAY_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(rangeChange.next.cacheSnapshot, null);
});

test('cache info keeps previous on failure even across a range change', () => {
  const prevCache = { totalCacheSize: 42 };
  const { next, failedSectionKeys } = applyDashboardBatchResponse(
    slices({ cacheInfo: prevCache }),
    fullBatch({ cache: null }),
    { rangeKey: DAY_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.cacheInfo, prevCache);
  assert.ok(failedSectionKeys.includes('cache'));
});

test('failed detection section is reported without touching the slices', () => {
  const prev = slices({ latestDownloads: [{ id: 1 }] });
  const { next, hadPartialFailure, failedSectionKeys } = applyDashboardBatchResponse(
    prev,
    fullBatch({ detection: null }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.ok(failedSectionKeys.includes('detection'));
  assert.equal(hadPartialFailure, true);
  assert.deepEqual(next.latestDownloads, [{ id: 10 }, { id: 11 }]);
});

test('fully successful batch applies every section and reports no failure', () => {
  const batch = fullBatch();
  const { next, hadPartialFailure, failedSectionKeys } = applyDashboardBatchResponse(
    slices({ latestDownloads: [{ id: 1 }] }),
    batch,
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.cacheInfo, batch.cache);
  assert.equal(next.clientStats, batch.clients);
  assert.equal(next.serviceStats, batch.services);
  assert.equal(next.dashboardStats, batch.dashboard);
  assert.equal(next.latestDownloads, batch.downloads);
  assert.equal(next.sparklines, batch.sparklines);
  assert.equal(next.hourlyActivity, batch.hourlyActivity);
  assert.equal(next.cacheSnapshot, batch.cacheSnapshot);
  assert.equal(next.cacheGrowth, batch.cacheGrowth);
  assert.equal(hadPartialFailure, false);
  assert.deepEqual(failedSectionKeys, []);
});

test('buildRangeKey is stable for live mode and distinct across windows', () => {
  assert.equal(buildRangeKey(undefined, undefined, undefined), buildRangeKey());
  assert.notEqual(LIVE_KEY, DAY_KEY);
  assert.notEqual(buildRangeKey(1, 2, undefined), buildRangeKey(1, 2, 7));
});
