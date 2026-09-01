import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const { applyDashboardBatchResponse, buildRangeKey } = await import(
  await compileToUrl('../src/contexts/DashboardDataContext/applyBatchResponse.ts')
);

const slices = (overrides = {}) => ({
  cacheInfo: null,
  clientStats: [],
  serviceStats: [],
  dashboardStats: null,
  latestDownloads: [],
  downloadTotals: null,
  filteredDownloadTotals: null,
  serviceOptions: [],
  clientOptions: [],
  sparklines: null,
  hourlyActivity: null,
  cacheSnapshot: null,
  ...overrides
});

// A batch where every sub-query succeeded. null on any key means that
// sub-query failed server-side; [] / hasData:false are successful results.
const fullBatch = (overrides = {}) => ({
  cache: { totalCacheSize: 1000 },
  clients: [{ clientIp: '10.0.0.1' }],
  services: [{ service: 'steam' }],
  dashboard: { period: { duration: 'live' } },
  downloadTotals: { cacheHitBytes: 900, cacheMissBytes: 100, count: 2 },
  filteredDownloadTotals: { cacheHitBytes: 400, cacheMissBytes: 50, count: 1 },
  serviceOptions: [{ service: 'steam', hasLargeFiles: true }],
  clientOptions: ['10.0.0.1'],
  recentDownloads: [{ id: 10 }, { id: 11 }],
  detection: { hasCachedResults: false },
  sparklines: { intervals: [] },
  hourlyActivity: { hours: [] },
  cacheSnapshot: { hasData: true },
  ...overrides
});

const LIVE_KEY = buildRangeKey(undefined, undefined, undefined);
const DAY_KEY = buildRangeKey(1_700_000_000, 1_700_086_400, undefined);

test('failed downloads section keeps the previous list within the same range', () => {
  const prevDownloads = [{ id: 1 }, { id: 2 }];
  const prev = slices({ latestDownloads: prevDownloads });
  const { next, hadPartialFailure, failedSectionKeys } = applyDashboardBatchResponse(
    prev,
    fullBatch({ recentDownloads: null }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.latestDownloads, prevDownloads);
  assert.equal(hadPartialFailure, true);
  assert.ok(failedSectionKeys.includes('recentDownloads'));
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
    hourlyActivity: { hours: [1] }
  });
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    prev,
    fullBatch({
      recentDownloads: null,
      downloadTotals: null,
      filteredDownloadTotals: null,
      serviceOptions: null,
      clientOptions: null,
      clients: null,
      dashboard: null,
      sparklines: null,
      hourlyActivity: null
    }),
    { rangeKey: LIVE_KEY, previousRangeKey: DAY_KEY }
  );
  assert.deepEqual(next.latestDownloads, []);
  assert.equal(next.downloadTotals, null);
  assert.equal(next.filteredDownloadTotals, null);
  assert.deepEqual(next.serviceOptions, []);
  assert.deepEqual(next.clientOptions, []);
  assert.deepEqual(next.clientStats, []);
  assert.equal(next.dashboardStats, null);
  assert.equal(next.sparklines, null);
  assert.equal(next.hourlyActivity, null);
  assert.equal(hadPartialFailure, true);
});

test('successful empty lists apply over previous data', () => {
  const prev = slices({
    latestDownloads: [{ id: 1 }],
    clientStats: [{ clientIp: '10.0.0.9' }]
  });
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    prev,
    fullBatch({ recentDownloads: [], clients: [] }),
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

test('a completed cache-file scan replaces cache info in a historical range', () => {
  const prevCache = {
    hasCacheScan: true,
    totalFiles: 100,
    cacheScanTotalBytes: 1,
    cacheScanTimestampUtc: '2026-08-23T00:00:00Z'
  };
  const nextCache = {
    hasCacheScan: true,
    totalFiles: 598763,
    cacheScanTotalBytes: 388_470_000_000,
    cacheScanTimestampUtc: '2026-08-24T03:00:00Z'
  };
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    slices({ cacheInfo: prevCache }),
    fullBatch({ cache: nextCache }),
    { rangeKey: DAY_KEY, previousRangeKey: DAY_KEY }
  );
  assert.equal(next.cacheInfo, nextCache);
  assert.equal(hadPartialFailure, false);
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
  assert.equal(next.latestDownloads, batch.recentDownloads);
  assert.equal(next.downloadTotals, batch.downloadTotals);
  assert.equal(next.filteredDownloadTotals, batch.filteredDownloadTotals);
  assert.equal(next.serviceOptions, batch.serviceOptions);
  assert.equal(next.clientOptions, batch.clientOptions);
  assert.equal(next.sparklines, batch.sparklines);
  assert.equal(next.hourlyActivity, batch.hourlyActivity);
  assert.equal(next.cacheSnapshot, batch.cacheSnapshot);
  assert.equal(hadPartialFailure, false);
  assert.deepEqual(failedSectionKeys, []);
});

test('failed download totals and filter options keep the previous ones in the same range', () => {
  const prevTotals = { cacheHitBytes: 5, cacheMissBytes: 5, count: 1 };
  const prevServices = [{ service: 'epic', hasLargeFiles: false }];
  const prevClients = ['10.0.0.9'];
  const { next, failedSectionKeys } = applyDashboardBatchResponse(
    slices({
      downloadTotals: prevTotals,
      serviceOptions: prevServices,
      clientOptions: prevClients
    }),
    fullBatch({
      downloadTotals: null,
      filteredDownloadTotals: null,
      serviceOptions: null,
      clientOptions: null
    }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.downloadTotals, prevTotals);
  assert.equal(next.serviceOptions, prevServices);
  assert.equal(next.clientOptions, prevClients);
  assert.ok(failedSectionKeys.includes('downloadTotals'));
  assert.ok(failedSectionKeys.includes('filteredDownloadTotals'));
  assert.ok(failedSectionKeys.includes('serviceOptions'));
  assert.ok(failedSectionKeys.includes('clientOptions'));
});

test('zeroed download totals and empty option lists apply as successful results', () => {
  const emptyTotals = { cacheHitBytes: 0, cacheMissBytes: 0, count: 0 };
  const { next, hadPartialFailure } = applyDashboardBatchResponse(
    slices({
      downloadTotals: { cacheHitBytes: 5, cacheMissBytes: 5, count: 1 },
      serviceOptions: [{ service: 'epic', hasLargeFiles: false }],
      clientOptions: ['10.0.0.9']
    }),
    fullBatch({
      downloadTotals: emptyTotals,
      filteredDownloadTotals: emptyTotals,
      serviceOptions: [],
      clientOptions: []
    }),
    { rangeKey: LIVE_KEY, previousRangeKey: LIVE_KEY }
  );
  assert.equal(next.downloadTotals, emptyTotals);
  assert.equal(next.filteredDownloadTotals, emptyTotals);
  assert.deepEqual(next.serviceOptions, []);
  assert.deepEqual(next.clientOptions, []);
  assert.equal(hadPartialFailure, false);
});

test('buildRangeKey is stable for live mode and distinct across windows', () => {
  assert.equal(buildRangeKey(undefined, undefined, undefined), buildRangeKey());
  assert.notEqual(LIVE_KEY, DAY_KEY);
  assert.notEqual(buildRangeKey(1, 2, undefined), buildRangeKey(1, 2, 7));
});
