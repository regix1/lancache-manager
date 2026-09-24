import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import {
  bindLifted,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * Management cards whose reads can overlap (mount, reconnect, hub events, Retry, typing) and whose
 * failures now stay on screen as an error box. An older answer that lands after a newer one must
 * not replace the newer rows or bring back an error the newer read already cleared, and a refused
 * save must not leave the control on a value the server never took.
 */

const CATALOG = 'src/components/features/management/game-mappings/GameMappingsCatalog.tsx';
const GRAFANA = 'src/components/features/management/grafana/GrafanaEndpoints.tsx';
const ANONYMOUS_DAEMON =
  'src/components/features/management/daemon-status/AnonymousDaemonStatus.tsx';
const STEAM_WEB_API = 'src/components/features/management/steam/SteamWebApiStatus.tsx';

/** A request the test answers by hand, in whatever order it wants. */
const heldRequests = () => {
  const held = [];
  const ask = () =>
    new Promise((resolve, reject) => {
      held.push({ resolve, reject });
    });
  return { held, ask };
};

/**
 * The catalog's search handler and whole-catalog load, sharing one request counter, with their
 * React state as recorders. The 300 ms debounce runs on the test's mocked `setTimeout`, so the
 * caller enables `mock.timers` before typing.
 */
const liftSearch = ({ searchMappings, loadMappings, loadStats = async () => null }) => {
  const state = { mappings: [], errors: [], stats: [] };
  const shared = {
    catalogRequestRef: { current: 0 },
    setMappings: (value) => state.mappings.push(value),
    setStats: (value) => state.stats.push(value),
    setError: (value) => state.errors.push(value),
    getErrorMessage: (error) => `reason: ${error.message}`,
    loadStats
  };
  const loadCatalog = bindLifted(liftHookCallback(CATALOG, 'useCallback', 'loadMappings()'), {
    ...shared,
    mockMode: false,
    loadMappings
  });
  const handleSearch = bindLifted(liftConstArrow(CATALOG, 'handleSearch'), {
    ...shared,
    setSearchQuery: () => undefined,
    searchTimeoutRef: { current: undefined },
    searchMappings,
    loadData: loadCatalog
  });
  return { state, handleSearch };
};

test('a failed game-library search clears the rows and shows its reason', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { state, handleSearch } = liftSearch({
    searchMappings: async () => {
      throw new Error('refused with 502');
    }
  });

  handleSearch('ab');
  t.mock.timers.tick(300);
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(state.errors, ['reason: refused with 502']);
  assert.deepEqual(
    state.mappings,
    [[]],
    'the rows on screen answered an earlier query, not this one'
  );
});

/** Types "ab" then "abc", answers "abc" first, then settles "ab" with `settleOlder`. */
const searchOutOfOrder = async (t, settleOlder) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const requests = heldRequests();
  const search = liftSearch({ searchMappings: requests.ask });
  search.handleSearch('ab');
  t.mock.timers.tick(300);
  search.handleSearch('abc');
  t.mock.timers.tick(300);
  requests.held[1].resolve(['abc row']);
  await new Promise((resolve) => setImmediate(resolve));
  settleOlder(requests.held[0]);
  await new Promise((resolve) => setImmediate(resolve));
  return search.state;
};

test('an older search that answers after a newer one leaves the newer rows', async (t) => {
  const state = await searchOutOfOrder(t, (older) => older.resolve(['ab row']));

  assert.deepEqual(state.mappings, [['abc row']], 'only the query in the box writes the rows');
  assert.deepEqual(state.errors, [null]);
});

test('an older search that fails after a newer one answered shows no error box', async (t) => {
  const state = await searchOutOfOrder(t, (older) => older.reject(new Error('refused with 502')));

  assert.deepEqual(state.mappings, [['abc row']]);
  assert.deepEqual(state.errors, [null], 'a stale failure never reaches the box');
});

test('the catalog box retries the query in the box and hides "No results" while it shows', () => {
  const sourceFile = parseSource(CATALOG, ts.ScriptKind.TSX);
  const errorBlock = findSoleNode(
    sourceFile,
    'ErrorBlock element',
    (node) => ts.isJsxSelfClosingElement(node) && node.tagName.getText(sourceFile) === 'ErrorBlock'
  );
  const onRetry = errorBlock.attributes.properties.find(
    (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(sourceFile) === 'onRetry'
  );
  assert.equal(
    onRetry.initializer.expression.getText(sourceFile),
    '() => handleSearch(searchQuery)',
    'a failed search is repeated, not replaced by the unfiltered list'
  );

  const noResults = findSoleNode(
    sourceFile,
    'noResults empty state',
    (node) =>
      ts.isJsxSelfClosingElement(node) && node.getText(sourceFile).includes('labels.noResults')
  );
  assert.equal(
    noResults.parent.getText(sourceFile).startsWith('error === null &&'),
    true,
    '"No results" under an error box would claim the failed search found nothing'
  );
});

/** Empties the box (the whole catalog is asked for), searches "abc", answers the search first. */
const clearThenSearch = async (t, settleCatalog) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const catalog = heldRequests();
  const searches = heldRequests();
  const search = liftSearch({ searchMappings: searches.ask, loadMappings: catalog.ask });
  search.handleSearch('');
  search.handleSearch('abc');
  t.mock.timers.tick(300);
  searches.held[0].resolve(['abc row']);
  await new Promise((resolve) => setImmediate(resolve));
  settleCatalog(catalog.held[0]);
  await new Promise((resolve) => setImmediate(resolve));
  return search.state;
};

test('a whole-catalog read that answers after a newer search leaves the search rows', async (t) => {
  const state = await clearThenSearch(t, (catalog) => catalog.resolve(['every row']));

  assert.deepEqual(
    state.mappings,
    [['abc row']],
    'the box says "abc", so only the rows that match it belong under it'
  );
});

test('a whole-catalog read that fails after a newer search answered shows no error box', async (t) => {
  const state = await clearThenSearch(t, (catalog) =>
    catalog.reject(new Error('refused with 502'))
  );

  assert.deepEqual(state.mappings, [['abc row']]);
  assert.equal(state.errors.at(-1), null, 'a stale failure never reaches the box');
});

test('an update refresh during a search also refreshes the game count in the header', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const search = liftSearch({
    searchMappings: async () => ['abc row', 'new abc row'],
    loadStats: async () => ({ totalGames: 2 })
  });
  const refreshCatalog = bindLifted(liftConstArrow(CATALOG, 'refreshCatalog'), {
    handleSearch: search.handleSearch,
    searchQuery: 'abc'
  });

  refreshCatalog();
  t.mock.timers.tick(300);
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(search.state.mappings, [['abc row', 'new abc row']]);
  assert.equal(
    search.state.stats.at(-1)?.totalGames,
    2,
    'the server now holds two games, so the header must stop saying one'
  );

  const sourceFile = parseSource(CATALOG, ts.ScriptKind.TSX);
  const accordion = findSoleNode(
    sourceFile,
    'AccordionSection element',
    (node) =>
      ts.isJsxOpeningElement(node) && node.tagName.getText(sourceFile) === 'AccordionSection'
  );
  const count = accordion.attributes.properties.find(
    (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(sourceFile) === 'count'
  );
  assert.equal(
    count.initializer.expression.getText(sourceFile),
    'stats?.totalGames',
    'the header count is the stats this refresh writes'
  );
});

test('hub updates and reconnects refresh the query in the box, not the whole catalog', () => {
  const sourceFile = parseSource(CATALOG, ts.ScriptKind.TSX);
  const reconnect = findSoleNode(
    sourceFile,
    'useReconnectRefetch call',
    (node) =>
      ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'useReconnectRefetch'
  );
  assert.equal(reconnect.arguments[1].getText(sourceFile), 'refreshCatalog');
  assert.equal(liftConstArrow(CATALOG, 'refreshCatalog'), '() => handleSearch(searchQuery)');

  const hubEffect = findSoleNode(
    sourceFile,
    'hub subscription effect',
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useEffect' &&
      node.arguments[0].getText(sourceFile).includes('on(updateEvent')
  );
  assert.ok(
    hubEffect.arguments[0].getText(sourceFile).includes('refreshCatalogRef.current()'),
    'an update event while "abc" is in the box must not put the whole catalog under it'
  );
  const latestRefresh = findSoleNode(
    sourceFile,
    'refreshCatalogRef assignment',
    (node) =>
      ts.isBinaryExpression(node) && node.left.getText(sourceFile) === 'refreshCatalogRef.current'
  );
  assert.equal(latestRefresh.right.getText(sourceFile), 'refreshCatalog');
});

test('a refused refresh-rate save puts the control back on the saved value', async () => {
  const shown = [];
  const reported = [];
  const saveRefreshRate = bindLifted(liftConstArrow(GRAFANA, 'handleDataRefreshChange'), {
    dataRefreshRate: '15',
    setDataRefreshRate: (value) => shown.push(value),
    fetch: async () => ({ ok: false, status: 502 }),
    ApiService: { getFetchOptions: (options) => options },
    assertOk: async (response) => {
      if (!response.ok) throw new Error(`refused with ${response.status}`);
      return response;
    },
    notifyError: (message) => reported.push(message),
    t: (key) => key
  });

  await saveRefreshRate('60');

  assert.equal(shown.at(-1), '15', 'the server kept 15 seconds, so the control shows it again');
  assert.deepEqual(reported, ['management.grafana.errors.updateRefreshRate']);
});

test('an older metrics read that fails after a newer one answered shows no error box', async () => {
  const security = heldRequests();
  const state = { security: [], errors: [] };
  const loadStatus = bindLifted(liftHookCallback(GRAFANA, 'useCallback', 'getMetricsSecurity'), {
    statusRequestRef: { current: 0 },
    ApiService: { getMetricsSecurity: security.ask, getFetchOptions: (options) => options },
    fetch: async (url) => ({
      ok: true,
      status: 200,
      json: async () => (url.endsWith('interval') ? { interval: 30 } : { gameLimit: 100 })
    }),
    assertOk: async (response) => response,
    setMetricsSecurity: (value) => state.security.push(value),
    setDataRefreshRate: () => undefined,
    setTopGames: () => undefined,
    setLoadError: (value) => state.errors.push(value),
    isAbortError: () => false,
    getErrorMessage: (error) => `reason: ${error.message}`
  });

  const older = loadStatus();
  const newer = loadStatus();
  security.held[1].resolve({ requiresAuthentication: true });
  await newer;
  security.held[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.security, [{ requiresAuthentication: true }]);
  assert.deepEqual(state.errors, [null], 'the box would hide controls the server just filled');
});

/** The daemon card's `loadStatus` and its mount effect, with the card's React state as recorders. */
const liftDaemonStatus = () => {
  const reads = heldRequests();
  const state = { status: [], errors: [], loading: [] };
  const setLoading = (value) => state.loading.push(value);
  const loadStatus = bindLifted(
    liftHookCallback(ANONYMOUS_DAEMON, 'useCallback', 'service.loadStatus()'),
    {
      statusRequestRef: { current: 0 },
      mockMode: false,
      OFFLINE_STATUS: { dockerAvailable: false },
      service: { loadStatus: reads.ask },
      setStatus: (value) => state.status.push(value),
      setLoadError: (value) => state.errors.push(value),
      setLoading,
      getErrorMessage: (error) => `reason: ${error.message}`
    }
  );
  const sourceFile = parseSource(ANONYMOUS_DAEMON, ts.ScriptKind.TSX);
  const mountEffect = findSoleNode(
    sourceFile,
    'mount effect',
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useEffect' &&
      node.arguments[1]?.getText(sourceFile) === '[loadStatus]'
  );
  const mount = bindLifted(mountEffect.arguments[0].getText(sourceFile), {
    loadStatus,
    setLoading
  });
  return { reads, state, loadStatus, mount };
};

test('an older daemon status read that fails after a newer one answered shows no error box', async () => {
  const { reads, state, loadStatus } = liftDaemonStatus();

  const older = loadStatus();
  const newer = loadStatus();
  reads.held[1].resolve({ dockerAvailable: true });
  await newer;
  reads.held[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.status, [{ dockerAvailable: true }]);
  assert.deepEqual(state.errors, [null], 'the box would hide a status the daemon just reported');
});

test('a mount read that fails while a newer read is out keeps the daemon card loading', async () => {
  const { reads, state, loadStatus, mount } = liftDaemonStatus();

  mount();
  const reconnect = loadStatus();
  reads.held[0].reject(new Error('mount read failed'));
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(
    state.loading,
    [],
    'no read has answered yet, so a zero-session, disconnected readout would be invented'
  );

  reads.held[1].resolve({ dockerAvailable: true, activeSessions: 2 });
  await reconnect;

  assert.deepEqual(
    state.loading,
    [false],
    'the newer read ends the loading the mount read started'
  );
  assert.deepEqual(state.status, [{ dockerAvailable: true, activeSessions: 2 }]);
  assert.deepEqual(state.errors, [null]);
});

/**
 * The Steam Web API panel rendered to markup with a status hook that reports `webApi`. Shared
 * controls render as plain tags that carry their text, so the markup shows what the user would read.
 */
const renderSteamWebApiStatus = (webApi) => {
  const sourceFile = parseSource(STEAM_WEB_API, ts.ScriptKind.TSX);
  const component = findSoleNode(
    sourceFile,
    'SteamWebApiStatus component',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'SteamWebApiStatus' &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  );
  const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);
  const SteamWebApiStatus = bindLifted(
    component.initializer.getText(sourceFile),
    {
      React,
      useState: React.useState,
      useRef: React.useRef,
      useEffect: React.useEffect,
      useTranslation: () => ({ t: (key) => key }),
      useSteamWebApiStatus: () => ({ refresh: async () => undefined, ...webApi }),
      useAuth: () => ({ authenticationEnabled: false, isLoading: false }),
      usePicsProgress: () => ({ updateProgress: () => undefined }),
      useNotifications: () => ({}),
      useFormattedDateTime: () => 'checked',
      getIntegrationReasonKey: (reason) => reason,
      getErrorMessage: (error) => String(error),
      ApiService: {},
      Button: ({ children }) => React.createElement('button', null, children),
      Alert: ({ title, children }) => React.createElement('div', null, title, children),
      ErrorBlock: ({ title, message, retryLabel }) =>
        React.createElement('section', null, title, message, retryLabel),
      LoadingSpinner: () => null,
      HelpPopover: () => null,
      HelpSection: passthrough,
      HelpNote: passthrough,
      HelpDefinition: () => null,
      SteamWebApiKeyModal: () => null,
      ConfirmationModal: () => null
    },
    { jsx: ts.JsxEmit.React }
  );
  return renderToStaticMarkup(React.createElement(SteamWebApiStatus));
};

test('a failed Steam Web API read shows only its box, with no status line or second Refresh', () => {
  const markup = renderSteamWebApiStatus({
    status: null,
    loading: false,
    error: 'The server could not complete the request.'
  });

  assert.ok(markup.includes('management.steamWebApi.loadError'), 'the box names the failure');
  assert.ok(
    !markup.includes('management.steamWebApi.unknownStatus'),
    'one failure, one message: the box already says the status could not be read'
  );
  assert.ok(
    !markup.includes('common.refresh'),
    "the box's Retry is the way back; a second Refresh beside it repeats the same read"
  );
  assert.ok(!markup.includes('mgmt-list'), 'no empty row frame is left behind');
});

test('a failed Steam Web API refresh keeps the key row it read before, under the box', () => {
  const markup = renderSteamWebApiStatus({
    status: { hasApiKey: true, isFullyOperational: true, version: 'V1WithKey', canManage: true },
    loading: false,
    error: 'The server could not complete the request.'
  });

  assert.ok(markup.includes('management.steamWebApi.loadError'));
  assert.ok(!markup.includes('management.steamWebApi.state.operational'));
  assert.ok(!markup.includes('common.refresh'));
  assert.ok(
    markup.includes('management.steamWebApi.keyConfigured'),
    'the key row from the last good read stays, as the other cards keep earlier data'
  );
});
