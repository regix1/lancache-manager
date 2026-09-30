import assert from 'node:assert/strict';
import test, { mock } from 'node:test';
import typescript from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

test('the dashboard keeps activity after the selected range end', () => {
  const callback = liftHookCallback(
    'src/components/features/dashboard/Dashboard.tsx',
    'useMemo',
    'lastActivityUtc'
  );
  const startTime = 1_000;
  const endTime = 2_000;
  const clientStats = [
    { id: 'later', lastActivityUtc: new Date(3_000 * 1000).toISOString() },
    { id: 'before', lastActivityUtc: new Date(900 * 1000).toISOString() }
  ];
  const filtered = bindLifted(callback, {
    clientStats,
    timeRange: '24h',
    getTimeRangeParams: () => ({ startTime, endTime })
  })();
  assert.deepEqual(
    filtered.map((client) => client.id),
    ['later']
  );
});

test('the session time filter keeps downloads active since its cutoff', () => {
  mock.timers.enable({ apis: ['Date'], now: new Date('2026-09-30T04:00:00Z') });
  const sourceFile = parseSource('src/components/features/downloads/useSessionFilters.ts');
  const declaration = findSoleNode(
    sourceFile,
    'applyFilters declaration',
    (node) => typescript.isFunctionDeclaration(node) && node.name?.text === 'applyFilters'
  );
  const applyFilters = bindLifted(
    `() => { ${declaration.getText(sourceFile)}; return applyFilters; }`,
    { TIME_RANGE_MS: { '1h': 60 * 60 * 1000 } }
  )();
  const base = {
    clientIp: '1.2.3.4',
    cacheHitBytes: 1,
    cacheHitPercent: 100,
    totalBytes: 1,
    isEvicted: false
  };
  const downloads = [
    {
      ...base,
      id: 'ended-inside',
      startTimeUtc: '2026-09-30T02:00:00Z',
      endTimeUtc: '2026-09-30T03:50:00Z'
    },
    {
      ...base,
      id: 'ended-before',
      startTimeUtc: '2026-09-30T01:00:00Z',
      endTimeUtc: '2026-09-30T02:00:00Z'
    },
    {
      ...base,
      id: 'active-new',
      startTimeUtc: '2026-09-30T03:30:00Z',
      endTimeUtc: null
    },
    {
      ...base,
      id: 'active-old',
      startTimeUtc: '2026-09-30T02:00:00Z',
      endTimeUtc: null
    }
  ];
  const result = applyFilters(downloads, {
    clientIps: [],
    cacheStatus: 'all',
    timeRange: '1h',
    sortBy: 'newest',
    sessionsPerPage: 5,
    itemsPerSession: 10
  });
  assert.deepEqual(result.map((download) => download.id).sort(), ['active-new', 'ended-inside']);
  mock.timers.reset();
});

test('the Downloads row uses the later end or start as its last activity', () => {
  const callback = liftHookCallback(
    'src/components/features/downloads/DownloadsTab.tsx',
    'useCallback',
    'lastSeen:'
  );
  const toDownloadGroup = bindLifted(callback, {
    t: (key) => key,
    getServiceDisplayName: (service) => service,
    getServiceFilterKey: (service) => service,
    getGameDisplayName: (name) => name
  });
  const row = {
    id: 'group',
    appName: 'Game',
    service: 'steam',
    groupType: 'game',
    downloadIds: ['1'],
    totalBytes: 1,
    cacheHitBytes: 1,
    cacheMissBytes: 0,
    clientIps: ['1.2.3.4'],
    startTimeUtc: '2026-09-30T09:00:00Z',
    lastStartTimeUtc: '2026-09-30T10:00:00Z',
    endTimeUtc: '2026-09-30T15:00:00Z',
    requestCount: 1,
    isEvicted: false,
    isPartiallyEvicted: false,
    hasRealGameName: true
  };
  assert.equal(toDownloadGroup(row, []).lastSeen, row.endTimeUtc);
  const unfinished = { ...row, endTimeUtc: '2026-09-30T08:00:00Z' };
  assert.equal(toDownloadGroup(unfinished, []).lastSeen, row.lastStartTimeUtc);
});
