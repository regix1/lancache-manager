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

const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);

/**
 * Renders the component `name` declared as `const name = (...) => ...` in `relativePath` to
 * markup with `props`, with `bindings` standing in for everything it imports. Shared controls are
 * bound to plain tags that carry their text, so the markup shows what the user would read.
 */
const renderComponent = (relativePath, name, bindings, props = {}) => {
  const sourceFile = parseSource(relativePath, ts.ScriptKind.TSX);
  const component = findSoleNode(
    sourceFile,
    `${name} component`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === name &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  );
  const Component = bindLifted(
    component.initializer.getText(sourceFile),
    { React, useTranslation: () => ({ t: (key) => key }), ...bindings },
    { jsx: ts.JsxEmit.React }
  );
  return renderToStaticMarkup(React.createElement(Component, props));
};

/** The Steam Web API panel with a status hook that reports `webApi`. */
const renderSteamWebApiStatus = (webApi) =>
  renderComponent(STEAM_WEB_API, 'SteamWebApiStatus', {
    useState: React.useState,
    useRef: React.useRef,
    useEffect: React.useEffect,
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
  });

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

test('both Steam Web API versions down reads as the status row alone, with its advice', () => {
  const markup = renderSteamWebApiStatus({
    status: { hasApiKey: true, isFullyOperational: false, version: 'BothFailed', canManage: true },
    loading: false,
    error: null
  });

  assert.ok(markup.includes('management.steamWebApi.state.down'), 'the row says the API is down');
  assert.ok(
    !markup.includes('management.steamWebApi.bothUnavailable.title'),
    'a red box restating the down row is the same failure said twice'
  );
  assert.ok(
    markup.includes('management.steamWebApi.bothUnavailable.description'),
    'the "try again later" advice is not said anywhere else, so the row keeps it'
  );
});

/** Stand-ins for an AccordionSection card: the header badge, then the body. */
const cardBindings = {
  AccordionSection: ({ badge, children }) =>
    React.createElement('div', null, React.createElement('header', null, badge), children),
  SectionHeaderChip: ({ children }) => React.createElement('span', null, children),
  SectionErrorChip: () => React.createElement('span', null, 'common.failedToLoad'),
  SectionHeaderActions: passthrough,
  ErrorBlock: ({ title }) => React.createElement('section', null, title),
  Button: ({ children }) => React.createElement('button', null, children),
  Alert: ({ children }) => React.createElement('div', null, children),
  HelpPopover: () => null,
  HelpSection: passthrough,
  HelpNote: passthrough,
  HelpDefinition: () => null,
  useAccordionGroupItem: () => undefined,
  useCallback: (callback) => callback
};

/** Every `useState` answers `[open, noop]`: in these cards it only holds whether a section is open. */
const openState = (open) => () => [open, () => undefined];

const renderDaemonCard = (open) =>
  renderComponent(
    'src/components/features/management/daemon-status/DaemonStatusCard.tsx',
    'DaemonStatusCard',
    { ...cardBindings, useState: openState(open), LoadingState: () => null },
    {
      title: 'Riot',
      description: 'about',
      help: { title: 'help', definitions: [], note: 'note' },
      loading: false,
      loadError: null,
      loadErrorTitle: 'Failed to load Riot status',
      onRetry: () => undefined,
      connected: false,
      connectedLabel: 'chip: connected',
      notConnectedLabel: 'chip: not connected',
      headline: 'headline: not connected',
      detail: 'detail'
    }
  );

test('an open daemon card says its status once, in its headline', () => {
  assert.ok(
    renderDaemonCard(false).includes('chip: not connected'),
    'closed, the chip is the status'
  );

  const open = renderDaemonCard(true);
  assert.ok(open.includes('headline: not connected'));
  assert.ok(!open.includes('chip: not connected'), 'open, the headline already says it');
});

test('an open API Authentication card does not repeat its state in the header', () => {
  const markup = renderComponent(
    'src/components/features/management/sections/SettingsSection.tsx',
    'SettingsSection',
    {
      ...cardBindings,
      useState: openState(true),
      useMockMode: () => ({ mockMode: true, setMockMode: () => undefined }),
      useAuth: () => ({ authenticationEnabled: false }),
      useSessionPreferences: () => ({ error: null }),
      useErrorHandler: () => ({ notifyError: () => undefined }),
      useNotifySuccess: () => ({ notifySuccess: () => undefined }),
      AccordionGroupToggle: () => null,
      GroupHeading: () => null,
      TabPanel: passthrough,
      AuthenticationManager: () => null,
      DisplayPreferences: () => null,
      Shield: () => null,
      Sparkles: () => null,
      Settings: () => null
    }
  );

  assert.ok(
    !markup.includes('management.sections.settings.disabled'),
    'the open card body says authentication is off, so a "Disabled" chip beside it repeats it'
  );
});

test('an open Steam card shows no header status chips; its body carries the status', () => {
  const markup = renderComponent(
    'src/components/features/management/steam/SteamIntegrationCard.tsx',
    'SteamIntegrationCard',
    {
      ...cardBindings,
      useState: openState(true),
      useSteamAuth: () => ({
        steamAuthMode: 'anonymous',
        error: null,
        refreshSteamAuth: async () => undefined
      }),
      useSteamWebApiStatus: () => ({
        status: { hasApiKey: true, isFullyOperational: false, version: 'BothFailed' },
        loading: false,
        error: null
      }),
      SteamIcon: () => null,
      SteamLoginManager: () => null,
      SteamWebApiStatus: () => null
    },
    {
      authMode: 'authenticated',
      mockMode: false,
      onError: () => undefined,
      onSuccess: () => undefined
    }
  );

  assert.ok(!markup.includes('management.steamAuth.anonymous'));
  assert.ok(!markup.includes('management.steamWebApi.badgeUnavailable'));
});

const STEAM_LOGIN_FLOW = 'src/hooks/useSteamLoginFlow.ts';

/**
 * The Management Steam sign-in's submit handler with its state as recorders; `fetch` answers the
 * sign-in request. The sign-in dialog shows `setError`'s value, and `notifyLoginFailure` was the
 * popup behind it.
 */
const liftSteamSignIn = (fetch) => {
  const state = {
    popups: [],
    errors: [],
    deadlines: [],
    successes: [],
    waits: [],
    prompts: [],
    loading: []
  };
  const attemptRef = { current: null };
  const shared = {
    identityRef: { current: undefined },
    integration: {
      identity: undefined,
      access: { canSignIn: true },
      refresh: async () => undefined
    },
    cancelledAttemptRef: { current: null },
    attemptRef,
    setError: (value) => state.errors.push(value),
    setLoading: (value) => state.loading.push(value),
    setWaitingForMobileConfirmation: () => undefined,
    setNeedsTwoFactor: (value) => state.prompts.push(value ? 'twoFactor' : null),
    setNeedsEmailCode: (value) => state.prompts.push(value ? 'email' : null),
    setLoginDeadline: (value) => state.deadlines.push(value),
    // React applies a function argument to the previous state.
    setEndingWait: (value) =>
      state.waits.push(typeof value === 'function' ? value(state.waits.at(-1) ?? null) : value),
    fetch,
    ApiService: {
      getJsonFetchOptions: (_body, options) => options,
      getFetchOptions: () => ({}),
      handleResponse: async (response) => response.json()
    },
    t: (key) => key,
    resetAuthForm: () => undefined,
    onSuccess: (message) => state.successes.push(message),
    onError: undefined
  };
  const readLoginEnding = bindLifted(liftConstArrow(STEAM_LOGIN_FLOW, 'readLoginEnding'), shared);
  const handleAuthenticate = bindLifted(liftConstArrow(STEAM_LOGIN_FLOW, 'handleAuthenticate'), {
    ...shared,
    readLoginEnding,
    endingWait: null,
    canAuthenticate: true,
    createUuid: () => 'attempt-a',
    busyRef: { current: false },
    needsTwoFactor: false,
    needsEmailCode: false,
    useManualCode: false,
    username: 'user',
    password: 'secret',
    requestRef: { current: 0 },
    setAttemptId: () => undefined,
    setAbortController: () => undefined,
    STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS: 120000,
    loginUrl: '/api/steam-auth/login',
    getExtraRequestBody: undefined,
    ApiError: class ApiError extends Error {},
    notifyLoginFailure: (message) => state.popups.push(message)
  });
  return { state, handleAuthenticate, readLoginEnding, attemptRef };
};

/** What GET /api/steam-auth/status answers: the caller's running attempt and the asked attempt's ending, nulls omitted. */
const steamStatus = (body) => ({
  ok: true,
  json: async () =>
    Object.fromEntries(
      Object.entries(body).filter(([, value]) => value !== null && value !== undefined)
    )
});

/** A fetch that answers the status read from a queue of reads and every other URL with `login`. */
const steamFetch = (login, statusReads) => {
  const statusUrls = [];
  const fetch = (url, options) => {
    if (!url.startsWith('/api/steam-auth/status')) return login(url, options);
    statusUrls.push(url);
    return statusReads.shift()();
  };
  return { fetch, statusUrls };
};

const abortsOnSignal = (_url, options) =>
  new Promise((_resolve, reject) => {
    options.signal.addEventListener('abort', () =>
      reject(Object.assign(new Error('aborted'), { name: 'AbortError' }))
    );
  });

const settle = () => new Promise((resolve) => setImmediate(resolve));

/** A saved sign-in as GET /api/steam-auth/status answers it: the account is signed in and the caller manages it. */
const completedEnding = {
  isAuthenticated: true,
  canManage: true,
  loginEnding: {
    attemptId: 'attempt-a',
    status: 'completed',
    stageKey: 'modals.steamAuth.success.authenticatedAs'
  }
};

test('a Steam sign-in whose connection dropped shows how it ended in the dialog only', async () => {
  const { fetch, statusUrls } = steamFetch(async () => {
    throw new TypeError('Failed to fetch');
  }, [
    async () =>
      steamStatus({
        loginEnding: {
          attemptId: 'attempt-a',
          status: 'failed',
          stageKey: 'errors.steam.serversBusy'
        }
      })
  ]);
  const { state, handleAuthenticate } = liftSteamSignIn(fetch);

  await handleAuthenticate();

  assert.equal(state.errors.at(-1), 'errors.steam.serversBusy');
  assert.deepEqual(statusUrls, ['/api/steam-auth/status?attemptId=attempt-a']);
  assert.deepEqual(
    state.popups,
    [],
    'the open dialog already shows it; a popup behind it repeats it'
  );
});

test('a Steam sign-in saved after the two-minute wait closes with success', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { fetch, statusUrls } = steamFetch(abortsOnSignal, [
    async () => steamStatus({ attemptId: 'attempt-a' }),
    async () => steamStatus(completedEnding)
  ]);
  const { state, handleAuthenticate, readLoginEnding } = liftSteamSignIn(fetch);

  const signIn = handleAuthenticate();
  t.mock.timers.tick(120000);
  await signIn;
  assert.deepEqual(
    state.successes,
    [],
    'the attempt is still running, so the dialog keeps waiting'
  );
  assert.deepEqual(
    state.waits.map((wait) => [wait.attemptId, wait.username]),
    [['attempt-a', 'user']],
    'the dialog waits for the server to announce the ending'
  );
  t.mock.timers.tick(5000);
  await settle();
  assert.equal(statusUrls.length, 1, 'no timer re-reads the status');

  assert.equal(await readLoginEnding('attempt-a', 'user', false), true);

  assert.deepEqual(state.successes, ['modals.steamAuth.success.authenticatedAs']);
  assert.deepEqual(
    state.errors.filter((message) => message !== null),
    []
  );
  assert.equal(state.deadlines.at(-1), null, 'the countdown is over once the wait has ended');
});

test('a Steam sign-in that ends after the wait shows its own reason', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { fetch } = steamFetch(abortsOnSignal, [
    async () =>
      steamStatus({
        loginEnding: {
          attemptId: 'attempt-a',
          status: 'cancelled',
          stageKey: 'errors.steam.signInCancelled'
        }
      })
  ]);
  const { state, handleAuthenticate } = liftSteamSignIn(fetch);

  const signIn = handleAuthenticate();
  t.mock.timers.tick(120000);
  await signIn;

  assert.equal(state.errors.at(-1), 'errors.steam.signInCancelled');
  assert.deepEqual(state.successes, []);
  assert.equal(
    state.deadlines.at(-1),
    null,
    'an expired countdown beside the reason says the same thing twice'
  );
});

test('a failed Steam status read is not read again on a timer', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { fetch, statusUrls } = steamFetch(abortsOnSignal, [
    () => Promise.reject(new TypeError('Failed to fetch')),
    async () => steamStatus(completedEnding)
  ]);
  const { state, handleAuthenticate, readLoginEnding } = liftSteamSignIn(fetch);

  const signIn = handleAuthenticate();
  t.mock.timers.tick(120000);
  await signIn;
  assert.equal(statusUrls.length, 1);
  assert.deepEqual(
    state.errors.filter((message) => message !== null),
    []
  );
  assert.deepEqual(state.successes, []);
  assert.equal(state.waits.length, 1, 'the dialog waits for a push, a reconnect or its deadline');
  const [wait] = state.waits;
  assert.ok(
    wait.deadline > Date.now() + 14 * 60 * 1000,
    'the deadline is the attempt window, not a retry delay'
  );
  t.mock.timers.tick(5000);
  await settle();
  assert.equal(statusUrls.length, 1, 'nothing reads again until the dialog is told to');

  assert.equal(await readLoginEnding(wait.attemptId, wait.username, false), true);
  assert.equal(statusUrls.length, 2);
  assert.deepEqual(state.successes, ['modals.steamAuth.success.authenticatedAs']);
});

test('a lost Steam answer that was a code prompt opens the code box', async () => {
  const { fetch } = steamFetch(
    async () => undefined,
    [
      async () =>
        steamStatus({
          attemptId: 'attempt-a',
          pendingPrompt: 'twoFactor',
          loginExpiresAtUtc: '2030-01-01T00:00:00Z'
        })
    ]
  );
  const { state, readLoginEnding, attemptRef } = liftSteamSignIn(fetch);
  attemptRef.current = 'attempt-a';

  const decided = await readLoginEnding('attempt-a', 'user', false);

  assert.equal(decided, false, 'the sign-in is not over, but the wait is');
  assert.deepEqual(state.prompts, ['twoFactor']);
  assert.deepEqual(state.waits, [null]);
  assert.equal(state.loading.at(-1), false, 'no spinner behind the code box');
  assert.equal(state.deadlines.at(-1), Date.parse('2030-01-01T00:00:00Z'));
  assert.deepEqual(state.successes, []);
  assert.deepEqual(
    state.errors.filter((message) => message !== null),
    []
  );
});

test('a Steam sign-in saved and then signed out reads as not gone through', async () => {
  const { fetch } = steamFetch(
    async () => undefined,
    [async () => steamStatus({ ...completedEnding, isAuthenticated: false })]
  );
  const { state, readLoginEnding, attemptRef } = liftSteamSignIn(fetch);
  attemptRef.current = 'attempt-a';

  assert.equal(await readLoginEnding('attempt-a', 'user', false), false);

  assert.deepEqual(state.successes, []);
  assert.equal(state.errors.at(-1), 'modals.steamAuth.errors.authenticationFailed');
});

test('a saved Steam sign-in beside another pending sign-in still closes with success', async () => {
  const { fetch } = steamFetch(
    async () => undefined,
    [
      async () =>
        steamStatus({
          ...completedEnding,
          canManage: false,
          ownershipReason: 'login-in-progress'
        })
    ]
  );
  const { state, readLoginEnding, attemptRef } = liftSteamSignIn(fetch);
  attemptRef.current = 'attempt-a';

  assert.equal(await readLoginEnding('attempt-a', 'user', false), true);

  assert.deepEqual(state.successes, ['modals.steamAuth.success.authenticatedAs']);
});

test('a Steam sign-in nothing decided by its deadline ends as ended or expired', async () => {
  const { fetch } = steamFetch(
    async () => undefined,
    [
      async () => steamStatus({ attemptId: 'attempt-a' }),
      () => Promise.reject(new TypeError('Failed to fetch'))
    ]
  );
  const { state, readLoginEnding, attemptRef } = liftSteamSignIn(fetch);
  attemptRef.current = 'attempt-a';

  assert.equal(
    await readLoginEnding('attempt-a', 'user', false),
    null,
    'still running: keep waiting'
  );
  assert.equal(state.errors.at(-1), undefined);
  assert.equal(
    await readLoginEnding('attempt-a', 'user', true),
    false,
    'a failed final read ends it too'
  );

  assert.equal(state.errors.at(-1), 'errors.integration.attemptExpired');
  assert.deepEqual(state.successes, []);
});

test('a Steam sign-in the caller no longer owns is left to whoever reset it', async () => {
  const { fetch } = steamFetch(async () => undefined, [async () => steamStatus(completedEnding)]);
  const { state, readLoginEnding, attemptRef } = liftSteamSignIn(fetch);
  attemptRef.current = 'attempt-b';

  assert.equal(await readLoginEnding('attempt-a', 'user', false), false);

  assert.deepEqual(state.successes, []);
  assert.deepEqual(state.errors, []);
});

test('a Steam sign-in the server refuses ends its countdown', async () => {
  const refused = liftSteamSignIn(async () => ({
    ok: true,
    json: async () => ({ success: false })
  }));
  await refused.handleAuthenticate();
  assert.equal(refused.state.errors.at(-1), 'modals.steamAuth.errors.authenticationFailed');
  assert.equal(refused.state.deadlines.at(-1), null);
});

test('an unreadable Steam sign-in answer reads the ending of its own sign-in', async () => {
  const { fetch } = steamFetch(
    async () => ({
      ok: true,
      json: async () => {
        throw new SyntaxError('Unexpected token');
      }
    }),
    [async () => steamStatus({})]
  );
  const unreadable = liftSteamSignIn(fetch);
  await unreadable.handleAuthenticate();
  assert.equal(unreadable.state.errors.at(-1), 'errors.integration.attemptExpired');
  assert.equal(unreadable.state.deadlines.at(-1), null);
});

test('resetting the Steam sign-in form ends its countdown', () => {
  const deadlines = [];
  const noop = () => undefined;
  const resetAuthForm = bindLifted(liftConstArrow(STEAM_LOGIN_FLOW, 'resetAuthForm'), {
    attemptRef: { current: null },
    cancelledAttemptRef: { current: null },
    setAttemptId: noop,
    cancelPendingRequest: noop,
    setError: noop,
    setPassword: noop,
    setTwoFactorCode: noop,
    setEmailCode: noop,
    setNeedsTwoFactor: noop,
    setNeedsEmailCode: noop,
    setWaitingForMobileConfirmation: noop,
    setUseManualCode: noop,
    setLoading: noop,
    setEndingWait: noop,
    setLoginDeadline: (value) => deadlines.push(value)
  });

  resetAuthForm();

  assert.deepEqual(
    deadlines,
    [null],
    'a closed or refused attempt keeps no countdown for the next'
  );
});

test('the Epic and Xbox cards leave a sign-in failure to their open dialog', () => {
  for (const [path, hook] of [
    ['src/components/features/management/epic/EpicDaemonStatus.tsx', 'useEpicMappingAuth'],
    ['src/components/features/management/xbox/XboxDaemonStatus.tsx', 'useXboxMappingAuth']
  ]) {
    const sourceFile = parseSource(path, ts.ScriptKind.TSX);
    const call = findSoleNode(
      sourceFile,
      `${hook} call`,
      (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === hook
    );
    const names = call.arguments[0].properties.map((property) => property.name.getText(sourceFile));
    assert.ok(
      !names.includes('onError'),
      `${hook}: the dialog shows "Failed to sign in" with the reason, so a popup repeats it`
    );
  }
});

test('the key regeneration error box carries no icon of its own under the dialog title icon', () => {
  const sourceFile = parseSource(
    'src/components/features/management/steam/AuthenticationManager.tsx',
    ts.ScriptKind.TSX
  );
  const alert = findSoleNode(
    sourceFile,
    'regenerate error Alert',
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(sourceFile) === 'Alert' &&
      node.getText(sourceFile).includes('regenerateError')
  );
  const icon = alert.openingElement.attributes.properties.find(
    (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(sourceFile) === 'icon'
  );
  assert.equal(icon?.initializer.expression.getText(sourceFile), 'null');
});

test('the API Authentication box puts its buttons in the shared action slot', () => {
  const sourceFile = parseSource(
    'src/components/features/management/steam/AuthenticationManager.tsx',
    ts.ScriptKind.TSX
  );
  const statusBox = findSoleNode(
    sourceFile,
    'authentication status Alert',
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(sourceFile) === 'Alert' &&
      node.openingElement.getText(sourceFile).includes('getAlertColor()')
  );
  const action = statusBox.openingElement.attributes.properties.find(
    (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(sourceFile) === 'action'
  );
  assert.ok(action, 'the slot keeps the buttons at the right edge, centered, like every other box');
  for (const label of ['management.auth.regenerate', 'management.auth.logout']) {
    assert.ok(action.getText(sourceFile).includes(label), `${label} sits in the action slot`);
  }
  const body = statusBox.children.map((child) => child.getText(sourceFile)).join('');
  assert.ok(!body.includes('<Button'), 'no button is left in the text column');
});

test('the sign-in dialog boxes carry no icon of their own under the Key title icon', () => {
  const sourceFile = parseSource(
    'src/components/features/management/steam/AuthenticationManager.tsx',
    ts.ScriptKind.TSX
  );
  const dialog = findSoleNode(
    sourceFile,
    'sign-in Modal with the Key title icon',
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(sourceFile) === 'Modal' &&
      node.openingElement.getText(sourceFile).includes('<Key')
  );
  const alerts = [];
  const visit = (node) => {
    if (
      (ts.isJsxElement(node) && node.openingElement.tagName.getText(sourceFile) === 'Alert') ||
      (ts.isJsxSelfClosingElement(node) && node.tagName.getText(sourceFile) === 'Alert')
    ) {
      alerts.push(ts.isJsxElement(node) ? node.openingElement : node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(dialog, visit);

  assert.equal(alerts.length, 2, 'the error box and the API key help box');
  for (const alert of alerts) {
    const icon = alert.attributes.properties.find(
      (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(sourceFile) === 'icon'
    );
    assert.equal(
      icon?.initializer.expression.getText(sourceFile),
      'null',
      'one icon per item: the dialog title already has the Key'
    );
  }
});
