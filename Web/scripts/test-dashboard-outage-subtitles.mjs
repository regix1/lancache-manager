import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * One failure shows one message. While the connection banner is up it is the page's one message,
 * so a dashboard section that is still marked failed shows nothing of its own. Connected, a batch
 * whose every section failed gets one box at the top of the page, and each section stays blank
 * under it; a single failed section keeps its own box, and Cache Growth's red box speaks for that
 * card, so its gray capacity line stays out of the way.
 *
 * The stat cards and the card are built inside the components and never exported, so these lift
 * the code that ships and run it.
 */

const DASHBOARD = 'src/components/features/dashboard/Dashboard.tsx';
const CACHE_GROWTH = 'src/components/features/dashboard/widgets/CacheGrowthTrend.tsx';

/** The Dashboard's stat-card memo, run with every section failed unless an override says not. */
const statCards = (overrides) =>
  bindLifted(
    liftHookCallback(DASHBOARD, 'useMemo', "key: 'totalCache'"),
    {
      React,
      t: (key) => key,
      failedToLoadSubtitle: undefined,
      failedSections: { cache: true, clients: true, services: true, dashboard: true },
      cacheSnapshotFailed: true,
      detectionFailed: true,
      cacheInfo: null,
      cacheSnapshot: null,
      gamesOnDiskStats: null,
      hasCacheScan: false,
      stats: {},
      loading: false,
      isHistoricalView: false,
      cacheFilesValue: 'files',
      isEditMode: false,
      handleCacheFilesValueChange: () => undefined,
      formattedCacheScanTime: null,
      formattedLastDetectionTime: null,
      unmappedCacheBytes: null,
      cardVisibility: {},
      statTooltips: {},
      periodBadge: undefined,
      liveBadge: undefined,
      staleScanBadge: () => undefined,
      formatBytes: String,
      formatCount: String,
      formatPercent: String,
      SegmentedControl: 'SegmentedControl',
      Badge: 'Badge',
      Activity: 'Activity',
      Boxes: 'Boxes',
      Database: 'Database',
      Download: 'Download',
      Files: 'Files',
      HardDrive: 'HardDrive',
      Server: 'Server',
      TrendingUp: 'TrendingUp',
      Users: 'Users',
      Zap: 'Zap',
      ...overrides
    },
    { jsx: typescript.JsxEmit.React }
  )();

test('every failed stat card subtitle reads the outage-aware text', () => {
  const failedCards = (cards) =>
    Object.values(cards)
      .filter((card) => card.subtitle === 'common.failedToLoad')
      .map((card) => card.key)
      .sort();

  assert.deepEqual(
    failedCards(statCards({ failedToLoadSubtitle: undefined })),
    [],
    'no card says it failed under the banner'
  );
  assert.deepEqual(failedCards(statCards({ failedToLoadSubtitle: 'common.failedToLoad' })), [
    'addedToCache',
    'bandwidthSaved',
    'cacheFiles',
    'cacheHitRatio',
    'gamesOnDisk',
    'servicesOnDisk',
    'totalCache',
    'totalServed',
    'usedSpace'
  ]);
});

test('the stat card failure text is blank under the banner and under the whole-batch box', () => {
  const sourceFile = parseSource(DASHBOARD, typescript.ScriptKind.TSX);
  const declaration = findSoleNode(
    sourceFile,
    'failedToLoadSubtitle declaration',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'failedToLoadSubtitle'
  );
  const subtitle = (connectionLost, batchFailed) =>
    bindLifted(`() => (${declaration.initializer.getText(sourceFile)})`, {
      connectionLost,
      batchFailed,
      t: (key) => key
    })();

  assert.equal(subtitle(true, false), undefined);
  assert.equal(subtitle(false, true), undefined, 'the one box at the top speaks for every card');
  assert.equal(subtitle(false, false), 'common.failedToLoad');
});

test('the header stale dot lights only while a failed section still shows a confirmed value', () => {
  const published = (unconfirmedSectionKeys) =>
    bindLifted(
      liftHookCallback(
        'src/contexts/DashboardDataContext/index.tsx',
        'useMemo',
        'failedSectionKeys: connectionLost'
      ),
      {
        ...Object.fromEntries(
          [
            'cacheInfo',
            'cacheSnapshot',
            'clientOptions',
            'clientStats',
            'dashboardStats',
            'downloadGroups',
            'downloadTotals',
            'filteredDownloadTotals',
            'gameDetectionByName',
            'gameDetectionByService',
            'gameDetectionData',
            'gameDetectionLookup',
            'hourlyActivity',
            'latestDownloads',
            'serviceOptions',
            'serviceStats',
            'sparklines',
            'refreshData',
            'updateData',
            'setDownloadFilters'
          ].map((name) => [name, null])
        ),
        loading: false,
        isRefreshing: false,
        error: 'The server could not complete the request.',
        connectionLost: false,
        dataStale: true,
        failedSectionKeys: ['cache', 'clients', 'dashboard'],
        unconfirmedSectionKeys
      }
    )();

  assert.equal(
    published(['cache', 'clients', 'dashboard']).dataStale,
    false,
    'nothing confirmed was kept, so the page box speaks alone and no last good value is showing'
  );
  assert.equal(published([]).dataStale, true);
});

test('a refresh whose whole batch failed after good data leaves the dot to the page box', () => {
  const stats = (failedSectionKeys) =>
    bindLifted(liftConstArrow('src/contexts/DashboardDataContext/hooks.ts', 'useStats'), {
      useMemo: (compute) => compute(),
      useDashboardDataContext: () => ({
        failedSectionKeys,
        // The provider still reports the data stale here: every section kept a confirmed value.
        dataStale: true
      })
    })();

  const wholeBatch = stats(['cache', 'clients', 'services', 'dashboard', 'recentDownloads']);
  assert.equal(wholeBatch.batchFailed, true);
  assert.equal(wholeBatch.dataStale, false, 'the one box at the top reports this failure');

  const oneSection = stats(['clients']);
  assert.equal(oneSection.batchFailed, false);
  assert.equal(oneSection.dataStale, true);
});

test('no ErrorBlock sits alone in a wrapper that stays behind when the box hides', () => {
  // ErrorBlock renders nothing under the connection banner. Spacing passed as its className hides
  // with it; a wrapper around it keeps its margin, padding or full-screen height as a blank area.
  const leftovers = [];
  for (const file of [
    'src/components/features/clients/ClientsTab.tsx',
    'src/components/features/dashboard/ServiceAnalyticsChart/ServiceAnalyticsChart.tsx',
    'src/components/features/dashboard/TopClientsTable.tsx',
    'src/components/features/dashboard/RecentDownloadsPanel.tsx',
    'src/components/features/dashboard/widgets/BandwidthTrend.tsx',
    'src/components/features/dashboard/widgets/CacheGrowthTrend.tsx',
    'src/components/features/dashboard/widgets/EventCompareChart.tsx',
    'src/components/features/dashboard/widgets/PeakUsageHours.tsx',
    'src/components/features/memory/MemoryDiagnostics.tsx'
  ]) {
    const sourceFile = parseSource(file, typescript.ScriptKind.TSX);
    const onlyTheBox = (child) =>
      (typescript.isJsxSelfClosingElement(child) &&
        child.tagName.getText(sourceFile) === 'ErrorBlock') ||
      (typescript.isJsxExpression(child) &&
        child.expression !== undefined &&
        typescript.isIdentifier(child.expression) &&
        child.expression.text.endsWith('ErrorBlock'));
    for (const parent of collectNodes(sourceFile, (node) => typescript.isJsxElement(node))) {
      const children = parent.children.filter(
        (child) => !(typescript.isJsxText(child) && child.containsOnlyTriviaWhiteSpaces)
      );
      if (
        children.length === 1 &&
        onlyTheBox(children[0]) &&
        !parent.openingElement.getText(sourceFile).includes('empty:hidden')
      ) {
        const { line } = sourceFile.getLineAndCharacterOfPosition(parent.getStart(sourceFile));
        leftovers.push(`${file}:${line + 1}`);
      }
    }
  }
  assert.deepEqual(leftovers, []);
});

test('an error box that can end its card drops its bottom margin there', () => {
  // With nothing kept, these boxes are the last thing in a padded card or well, and a bottom
  // margin there cannot collapse into the padding, so it shows as a gap under the box.
  const missing = [];
  for (const file of [
    'src/components/features/clients/ClientsTab.tsx',
    'src/components/features/dashboard/ServiceAnalyticsChart/ServiceAnalyticsChart.tsx',
    'src/components/features/dashboard/TopClientsTable.tsx',
    'src/components/features/dashboard/widgets/BandwidthTrend.tsx',
    'src/components/features/dashboard/widgets/CacheGrowthTrend.tsx',
    'src/components/features/dashboard/widgets/EventCompareChart.tsx'
  ]) {
    const sourceFile = parseSource(file, typescript.ScriptKind.TSX);
    for (const box of collectNodes(
      sourceFile,
      (node) =>
        typescript.isJsxSelfClosingElement(node) &&
        node.tagName.getText(sourceFile) === 'ErrorBlock'
    )) {
      const classes = box.attributes.properties
        .find((attribute) => attribute.name?.getText(sourceFile) === 'className')
        ?.initializer?.getText(sourceFile);
      if (classes?.includes('mb-') && !classes.includes('last:mb-0')) {
        const { line } = sourceFile.getLineAndCharacterOfPosition(box.getStart(sourceFile));
        missing.push(`${file}:${line + 1}`);
      }
    }
  }
  assert.deepEqual(missing, []);
});

test('a past range says "live only" once on the live-only cards, not again in the subtitle', () => {
  const cards = statCards({ isHistoricalView: true });

  for (const key of ['activeDownloads', 'activeClients']) {
    assert.equal(cards[key].value, 'dashboard.cards.disabled');
    assert.equal(cards[key].subtitle, undefined);
  }
});

test('Cache Growth shows one failure message: its box when only it failed, none under the banner or the page box', () => {
  const cacheGrowth = ({ connectionLost, snapshotFailed, statsFailed }) => {
    const card = bindLifted(
      liftHookCallback(CACHE_GROWTH, 'memo', 'dash-range-footer'),
      {
        React,
        useTranslation: () => ({ t: (key) => key }),
        useTimeFilter: () => ({ timeRange: '24h' }),
        useCacheSnapshot: () => ({
          cacheSnapshot: null,
          loading: false,
          error: snapshotFailed ? 'The server could not complete the request.' : null,
          failed: snapshotFailed,
          refetch: async () => undefined
        }),
        useStats: () => ({
          failedSections: {
            cache: true,
            clients: statsFailed,
            services: statsFailed,
            dashboard: statsFailed
          },
          batchFailed: statsFailed
        }),
        useConnectionLost: () => connectionLost,
        useFormattedDateTime: () => '',
        getCacheGrowth: () => null,
        getCacheGrowthEmptyState: () => 'noData',
        formatBytes: String,
        formatPercent: String,
        WidgetPanel: 'WidgetPanel',
        ErrorBlock: 'ErrorBlock',
        LoadingSpinner: 'LoadingSpinner',
        EmptyState: 'EmptyState',
        TrendingUp: 'TrendingUp',
        TrendingDown: 'TrendingDown'
      },
      { jsx: typescript.JsxEmit.React }
    );
    const texts = [];
    const types = [];
    const visit = (node) => {
      if (Array.isArray(node)) {
        node.forEach(visit);
        return;
      }
      if (typeof node === 'string') {
        texts.push(node);
        return;
      }
      if (!React.isValidElement(node)) return;
      types.push(node.type);
      visit(node.props.children);
    };
    visit(card({ usedCacheSize: 0, totalCacheSize: 0 }));
    return { grayLine: texts.includes('common.failedToLoad'), box: types.includes('ErrorBlock') };
  };

  assert.deepEqual(
    cacheGrowth({ connectionLost: false, snapshotFailed: true, statsFailed: false }),
    { grayLine: false, box: true }
  );
  assert.equal(
    cacheGrowth({ connectionLost: true, snapshotFailed: false, statsFailed: false }).grayLine,
    false
  );
  assert.deepEqual(
    cacheGrowth({ connectionLost: false, snapshotFailed: false, statsFailed: false }),
    { grayLine: true, box: false }
  );
  assert.deepEqual(
    cacheGrowth({ connectionLost: false, snapshotFailed: true, statsFailed: true }),
    { grayLine: false, box: false },
    'a batch whose every section failed shows only the box at the top of the page'
  );
});
