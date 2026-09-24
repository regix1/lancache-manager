import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import typescript from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * While the connection banner is up it is the page's one message, so a dashboard section that is
 * still marked failed shows nothing of its own. Connected, one failure shows one message: Cache
 * Growth's red box speaks for the card, so its gray capacity line stays out of the way.
 *
 * The stat cards and the card are built inside the components and never exported, so these lift
 * the code that ships and run it.
 */

const DASHBOARD = 'src/components/features/dashboard/Dashboard.tsx';
const CACHE_GROWTH = 'src/components/features/dashboard/widgets/CacheGrowthTrend.tsx';

test('every failed stat card subtitle reads the outage-aware text', () => {
  const statCards = (failedToLoadSubtitle) =>
    bindLifted(
      liftHookCallback(DASHBOARD, 'useMemo', "key: 'totalCache'"),
      {
        React,
        t: (key) => key,
        failedToLoadSubtitle,
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
        Zap: 'Zap'
      },
      { jsx: typescript.JsxEmit.React }
    )();
  const failedCards = (cards) =>
    Object.values(cards)
      .filter((card) => card.subtitle === 'common.failedToLoad')
      .map((card) => card.key)
      .sort();

  assert.deepEqual(
    failedCards(statCards(undefined)),
    [],
    'no card says it failed under the banner'
  );
  assert.deepEqual(failedCards(statCards('common.failedToLoad')), [
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

test('the stat card failure text is blank while the connection is lost', () => {
  const sourceFile = parseSource(DASHBOARD, typescript.ScriptKind.TSX);
  const declaration = findSoleNode(
    sourceFile,
    'failedToLoadSubtitle declaration',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'failedToLoadSubtitle'
  );
  const subtitle = (connectionLost) =>
    bindLifted(`() => (${declaration.initializer.getText(sourceFile)})`, {
      connectionLost,
      t: (key) => key
    })();

  assert.equal(subtitle(true), undefined);
  assert.equal(subtitle(false), 'common.failedToLoad');
});

test('Cache Growth shows one failure message: the box when connected, nothing under the banner', () => {
  const cacheGrowth = ({ connectionLost, snapshotFailed }) => {
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
        useStats: () => ({ failedSections: { cache: true } }),
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

  assert.deepEqual(cacheGrowth({ connectionLost: false, snapshotFailed: true }), {
    grayLine: false,
    box: true
  });
  assert.equal(cacheGrowth({ connectionLost: true, snapshotFailed: false }).grayLine, false);
  assert.deepEqual(cacheGrowth({ connectionLost: false, snapshotFailed: false }), {
    grayLine: true,
    box: false
  });
});
