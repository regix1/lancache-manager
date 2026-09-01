import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

/**
 * Cache Files and Games on Disk are not time-range dependent. Their scan-complete
 * events must force a dashboard batch refetch in every range, not only 'live'.
 * The dedicatedHandlers map is the single edit site: keys there are excluded from
 * handleRefreshEvent's live-only list.
 */

const dashboardSource = readFileSync(
  new URL('../src/contexts/DashboardDataContext/index.tsx', import.meta.url),
  'utf8'
);
const typesSource = readFileSync(
  new URL('../src/contexts/SignalRContext/types.ts', import.meta.url),
  'utf8'
);

const file = ts.createSourceFile(
  'index.tsx',
  dashboardSource,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

const collect = (root, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(root, visit);
  return found;
};

const variableNamed = (name) =>
  collect(file, (node) => ts.isVariableDeclaration(node) && node.name.getText(file) === name)[0];

const objectLiteralOf = (declaration) => {
  const init = declaration?.initializer;
  if (!init) {
    return undefined;
  }
  if (ts.isObjectLiteralExpression(init)) {
    return init;
  }
  if (ts.isAsExpression(init) && ts.isObjectLiteralExpression(init.expression)) {
    return init.expression;
  }
  if (ts.isSatisfiesExpression(init) && ts.isObjectLiteralExpression(init.expression)) {
    return init.expression;
  }
  return undefined;
};

const dedicatedKeys = () => {
  const declaration = variableNamed('dedicatedHandlers');
  const literal = objectLiteralOf(declaration);
  assert.ok(literal, 'dedicatedHandlers is the map that bypasses the live-only gate');
  return new Set(
    literal.properties.filter(ts.isPropertyAssignment).map((property) => {
      const name = property.name;
      if (ts.isIdentifier(name) || ts.isStringLiteral(name)) {
        return name.text;
      }
      return name.getText(file).replaceAll(/['"]/g, '');
    })
  );
};

const handlerBody = (name) => {
  const declaration = variableNamed(name);
  assert.ok(declaration, `${name} must exist`);
  return declaration.getText(file);
};

test('scan-complete events stay on the SignalR refresh list', () => {
  assert.match(typesSource, /'GameDetectionComplete'/);
  assert.match(typesSource, /'CacheScanComplete'/);
  const refreshBlock = typesSource.slice(
    typesSource.indexOf('export const SIGNALR_REFRESH_EVENTS')
  );
  assert.match(refreshBlock, /'GameDetectionComplete'/);
  assert.match(refreshBlock, /'CacheScanComplete'/);
});

test('game detection and cache-file scan bypass the live-only refresh gate', () => {
  const keys = dedicatedKeys();
  assert.ok(
    keys.has('GameDetectionComplete'),
    'Games on Disk must refresh after GameDetectionComplete in every time range'
  );
  assert.ok(
    keys.has('CacheScanComplete'),
    'Cache Files must refresh after CacheScanComplete in every time range'
  );

  const throttled = variableNamed('throttledEvents');
  assert.ok(throttled, 'throttledEvents is the live-only list');
  assert.match(
    throttled.getText(file),
    /SIGNALR_REFRESH_EVENTS\.filter\(\(event\) => !\(event in dedicatedHandlers\)\)/
  );
});

test('both scan-complete handlers force a dashboard batch refetch', () => {
  for (const name of ['handleGameDetectionComplete', 'handleCacheScanComplete']) {
    const body = handlerBody(name);
    assert.match(body, /fetchAllData\(/);
    assert.match(body, /forceRefresh:\s*true/);
    assert.doesNotMatch(body, /!== 'live'/, `${name} must not re-introduce the live-only gate`);
  }

  assert.match(handlerBody('handleRefreshEvent'), /currentTimeRangeRef\.current !== 'live'/);
});

test('Games on Disk display stats follow the latest detection payload', async () => {
  const { buildGamesOnDiskDisplayStats } = await import(
    await compileToUrl('../src/utils/gameDetection.ts')
  );
  const before = buildGamesOnDiskDisplayStats({
    hasCachedResults: true,
    games: [],
    services: [],
    games_on_disk_bytes: 1000,
    games_on_disk_count: 2
  });
  const after = buildGamesOnDiskDisplayStats({
    hasCachedResults: true,
    games: [],
    services: [],
    games_on_disk_bytes: 831_668_022,
    games_on_disk_count: 46
  });

  assert.equal(before?.totalSize, 1000);
  assert.equal(before?.gameCount, 2);
  assert.equal(after?.totalSize, 831_668_022);
  assert.equal(after?.gameCount, 46);
  assert.notEqual(before?.totalSize, after?.totalSize);
});

test('a completed cache-file scan replaces cache info in a historical range', async () => {
  const { applyDashboardBatchResponse, buildRangeKey } = await import(
    await compileToUrl('../src/contexts/DashboardDataContext/applyBatchResponse.ts')
  );
  const dayKey = buildRangeKey(1_700_000_000, 1_700_086_400, undefined);
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
    {
      cacheInfo: prevCache,
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
      cacheSnapshot: null
    },
    {
      cache: nextCache,
      clients: [],
      services: [],
      dashboard: { period: { duration: '24h' } },
      downloadTotals: { cacheHitBytes: 0, cacheMissBytes: 0, count: 0 },
      filteredDownloadTotals: { cacheHitBytes: 0, cacheMissBytes: 0, count: 0 },
      serviceOptions: [],
      clientOptions: [],
      recentDownloads: [],
      detection: { hasCachedResults: false },
      sparklines: { intervals: [] },
      hourlyActivity: { hours: [] },
      cacheSnapshot: { hasData: false }
    },
    { rangeKey: dayKey, previousRangeKey: dayKey }
  );
  assert.equal(next.cacheInfo, nextCache);
  assert.equal(hadPartialFailure, false);
});
