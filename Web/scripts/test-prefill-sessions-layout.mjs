import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, findSoleNode, parseSource, transpile } from './transpile-module.mjs';

const prefillTypes = await import(
  await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts')
);

const source = parseSource(
  'src/components/features/management/sections/PrefillSessionsSection.tsx',
  ts.ScriptKind.TSX
);
const component = findSoleNode(
  source,
  'persistent container',
  (node) =>
    ts.isVariableDeclaration(node) && node.name.getText(source) === 'PersistentContainerCard'
);
const sessionComponent = findSoleNode(
  source,
  'session card',
  (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === 'SessionCard'
);
const compiled = transpile(
  `const PersistentContainerCard = ${component.initializer.getText(source)};`,
  ts.ModuleKind.CommonJS,
  { jsx: ts.JsxEmit.React, jsxFactory: 'h' }
);
const sessionCompiled = transpile(
  `const SessionCard = ${sessionComponent.initializer.getText(source)};`,
  ts.ModuleKind.CommonJS,
  { jsx: ts.JsxEmit.React, jsxFactory: 'h' }
);
const runs = Symbol('runs');
const badge = Symbol('badge');
const timestamp = Symbol('timestamp');
const region = Symbol('region');
const walk = (node, type) => [
  ...(node?.type === type ? [node] : []),
  ...(node?.children ?? []).flatMap((child) => walk(child, type))
];
const render = (props) =>
  new Function(
    'currentPrefillRun',
    'useTranslation',
    'useActivityStatus',
    'useState',
    'resolveServiceId',
    'isAnonymousServiceId',
    'serviceDisplayName',
    'rowToggleHandlers',
    'formatBytes',
    'scheduleIntervalLabel',
    'StatusDot',
    'Badge',
    'Button',
    'ChevronDown',
    'CollapsibleRegion',
    'PrefillRuns',
    'FormattedTimestamp',
    'EmptyState',
    'h',
    `${compiled}\nreturn PersistentContainerCard;`
  )(
    prefillTypes.currentPrefillRun,
    () => ({ t: (key) => key }),
    () => ({ isActive: () => false }),
    () => [true, () => undefined],
    (service) => service,
    (service) => service === 'Riot',
    (service) => service,
    () => ({}),
    String,
    () => 'paused',
    Symbol('dot'),
    badge,
    Symbol('button'),
    Symbol('chevron'),
    region,
    runs,
    timestamp,
    Symbol('empty'),
    (type, attributes, ...children) => ({
      type,
      props: attributes ?? {},
      children: children.flat().filter(Boolean)
    })
  )(props);

const renderSession = (session) =>
  new Function(
    'currentPrefillRun',
    'useTranslation',
    'useState',
    'useActivityStatus',
    'resolveServiceId',
    'isAnonymousServiceId',
    'serviceDisplayName',
    'usePaginatedList',
    'getStatusBadgeLabelKey',
    'rowToggleHandlers',
    'StatusDot',
    'Badge',
    'Button',
    'ChevronDown',
    'CollapsibleRegion',
    'PrefillRuns',
    'FormattedTimestamp',
    'LoadingSpinner',
    'Pagination',
    'HistoryStatusBadge',
    'formatBytes',
    'cleanIpAddress',
    'translateStageKeyMessage',
    'RowActionsMenu',
    'ActionMenuItem',
    'ActionMenuDangerItem',
    'ActionMenuDivider',
    'StopCircle',
    'Ban',
    'XCircle',
    'h',
    `${sessionCompiled}\nreturn SessionCard;`
  )(
    prefillTypes.currentPrefillRun,
    () => ({
      t: (key, values = {}) => (values.service ? `${key}:${values.service}` : key)
    }),
    () => [false, () => undefined],
    () => ({ isActive: () => false }),
    (platform) => {
      const service = platform.toLowerCase();
      return service === 'blizzard' ? 'battlenet' : service;
    },
    (service) => service === 'battlenet' || service === 'riot',
    (service) =>
      ({ xbox: 'Xbox', riot: 'Riot Games', battlenet: 'Battle.net' })[service] ?? service,
    ({ items }) => ({ paginatedItems: items, totalPages: 1 }),
    () => 'active',
    () => ({}),
    Symbol('dot'),
    badge,
    Symbol('button'),
    Symbol('chevron'),
    region,
    runs,
    timestamp,
    Symbol('spinner'),
    Symbol('pagination'),
    Symbol('history-status'),
    String,
    String,
    String,
    Symbol('row-actions'),
    Symbol('action'),
    Symbol('danger'),
    Symbol('divider'),
    Symbol('stop'),
    Symbol('ban'),
    Symbol('error'),
    (type, attributes, ...children) => ({
      type,
      props: attributes ?? {},
      children: children.flat().filter(Boolean)
    })
  )({
    session,
    isLive: true,
    isAdmin: false,
    historyData: [],
    isHistoryExpanded: false,
    isLoadingHistory: false,
    onToggleHistory: () => undefined,
    historyPage: 1,
    onHistoryPageChange: () => undefined
  });

test('session titles preserve account identity and anonymous service labels', () => {
  const cases = [
    {
      platform: 'Xbox',
      username: 'NamedXboxPlayer',
      isPersistent: true,
      authState: 'Authenticated',
      title: 'NamedXboxPlayer',
      badges: ['Xbox', 'management.prefillSessions.labels.persistentBadge']
    },
    {
      platform: 'Xbox',
      isPersistent: true,
      authState: 'Authenticated',
      title: 'management.prefillSessions.labels.authenticatedAccount:Xbox',
      badges: ['Xbox', 'management.prefillSessions.labels.persistentBadge']
    },
    {
      platform: 'Xbox',
      isPersistent: true,
      authState: 'NotAuthenticated',
      title: 'management.prefillSessions.labels.notLoggedInSession',
      badges: ['Xbox', 'management.prefillSessions.labels.persistentBadge']
    },
    {
      platform: 'Riot',
      authState: 'Authenticated',
      title: 'management.prefillSessions.labels.anonymousAccount:Riot Games',
      badges: ['Riot Games']
    },
    {
      platform: 'BattleNet',
      authState: 'Authenticated',
      title: 'management.prefillSessions.labels.anonymousAccount:Battle.net',
      badges: ['Battle.net']
    },
    {
      platform: 'Riot',
      accountUsername: 'RiotPlayer',
      authState: 'Authenticated',
      title: 'RiotPlayer',
      badges: ['Riot Games']
    },
    {
      platform: 'BattleNet',
      username: 'BattlePlayer',
      authState: 'Authenticated',
      title: 'BattlePlayer',
      badges: ['Battle.net']
    }
  ];

  for (const entry of cases) {
    const tree = renderSession({
      id: `${entry.platform}-${entry.title}`,
      status: 'Active',
      createdAt: '2026-09-27T00:00:00Z',
      activeRunCount: 0,
      runs: [],
      ...entry
    });
    const title = walk(tree, 'span').find((node) =>
      node.props.className?.includes('mgmt-row__title')
    );
    assert.equal(title.children[0], entry.title);
    assert.deepEqual(
      walk(tree, badge).map((node) => node.children[0]),
      entry.badges
    );
    assert.notEqual(title.children[0], 'management.prefillSessions.labels.persistentContainer');
  }
});

const prefillRun = (runId, state, patch = {}) => ({
  runId,
  snapshot: {
    state,
    startedAt: '2026-10-28T16:00:00Z',
    bytesTransferred: 0,
    currentItem: null,
    ...patch.snapshot
  },
  completedAtUtc: patch.completedAtUtc
});

test('persistent session activity and schedule counts have separate owners', () => {
  const entries = [prefillRun('first', 'downloading'), prefillRun('second', 'completed')];
  const tree = render({
    container: {
      service: 'Steam',
      sessionId: 'session',
      isRunning: true,
      activeRunCount: 1,
      runs: entries
    },
    schedules: [
      { id: 'one', name: 'Daily', enabled: true },
      { id: 'two', name: 'Weekly', enabled: false }
    ]
  });
  assert.equal(walk(tree, runs)[0].props.runs, entries);
  assert.equal(walk(tree, runs)[0].props.activeCount, 1);
  assert.equal(walk(tree, runs)[0].props.onCancel, undefined);
  const schedules = walk(tree, 'section').find(
    (node) => node.props.className === 'prefill-session-detail__schedules'
  );
  const count = walk(schedules, badge).find((node) => node.props.className === 'badge-count');
  assert.deepEqual(count.children, [2]);
  assert.equal(walk(tree, region)[0].children[0].props.inert, false);
});

test('login expiry is labelled inside details and only appears for a signed-in container', () => {
  for (const isAuthenticated of [false, true]) {
    const tree = render({
      container: {
        service: 'Steam',
        sessionId: 'session',
        isRunning: true,
        isAuthenticated,
        authExpiresAtUtc: '2026-12-12T10:00:00Z'
      },
      schedules: []
    });
    const details = walk(tree, 'dl')[0];
    assert.deepEqual(
      walk(details, 'dt').map((node) => node.children[0]),
      [
        'activeSessions.labels.sessionId',
        ...(isAuthenticated ? ['prefill.persistent.reloginRequiredBy'] : [])
      ]
    );
    assert.equal(walk(details, timestamp).length, isAuthenticated ? 1 : 0);
    assert.equal(walk(tree, timestamp).length, walk(details, timestamp).length);
  }
});

test('a container whose sign-in state could not be read is not labelled as logged out', () => {
  const loginBadgeLabel = (patch) => {
    const tree = render({
      container: {
        service: 'Steam',
        sessionId: 'session',
        isRunning: true,
        isAuthenticated: false,
        needsRelogin: false,
        ...patch
      },
      schedules: []
    });
    const titleLine = walk(tree, 'div').find(
      (node) => node.props.className === 'session-row__titleline'
    );
    return walk(titleLine, badge)[0].children;
  };
  const base = 'management.prefillSessions.persistentSessions.status';
  assert.deepEqual(loginBadgeLabel({ authUnknown: true }), ['common.unknown']);
  assert.deepEqual(loginBadgeLabel({ authUnknown: false }), [`${base}.notLoggedIn`]);
  assert.deepEqual(loginBadgeLabel({ authUnknown: true, isAuthenticated: true }), [
    `${base}.authenticated`
  ]);
});

test('neutral buttons have no decorative ring and retain keyboard focus styling', () => {
  const css = readFileSync(
    new URL('../src/styles/components/buttons.css', import.meta.url),
    'utf8'
  );
  assert.match(css, /\.btn-field-surface\s*\{[^}]*box-shadow: none;/);
  assert.doesNotMatch(css, /box-shadow:\s*inset/);
  assert.match(css, /button:focus-visible,[\s\S]*?outline: 2px solid var\(--theme-border-focus\);/);
  assert.match(
    css,
    /\.btn-icon-square:focus-visible\s*\{[^}]*border-color: var\(--theme-border-focus\);/
  );
});

test('active prefill progress uses a separate track surface without an outline', () => {
  const css = readFileSync(new URL('../src/styles/features/prefill.css', import.meta.url), 'utf8');
  assert.match(css, /\.prefill-run-progress\s*\{[^}]*background: var\(--theme-bg-primary\);/);
  assert.match(css, /\.prefill-run-progress\s*\{[^}]*border: 0;/);
  assert.match(css, /\.prefill-run-progress\s*\{[^}]*appearance: none;/);
  assert.match(
    css,
    /\.prefill-run-progress::-webkit-progress-bar\s*\{[^}]*background: var\(--theme-bg-primary\);/
  );
});

test('a container row names the game and bytes of the run it describes', () => {
  const text = (node) =>
    typeof node === 'object' && node !== null ? node.children.flatMap(text) : [String(node)];
  const meta = (container) =>
    text(
      walk(
        render({
          container: { service: 'Steam', sessionId: 'session', ...container },
          schedules: []
        }),
        'div'
      ).find((node) => node.props.className === 'mgmt-row__meta session-row__meta')
    ).join('');
  const game = { snapshot: { currentItem: { name: 'Portal' }, bytesTransferred: 2048 } };
  assert.match(
    meta({ isRunning: true, activeRunCount: 1, runs: [prefillRun('a', 'downloading', game)] }),
    /prefilling.*2048/s
  );
  // The server still counts a run active for a moment after it ends; the row then names no game.
  const ended = meta({
    isRunning: true,
    activeRunCount: 1,
    runs: [prefillRun('a', 'completed', { ...game, completedAtUtc: '2026-10-28T16:01:00Z' })]
  });
  assert.match(ended, /prefillingGeneric/);
  assert.doesNotMatch(ended, /Portal/);
  assert.doesNotMatch(meta({ isRunning: true, activeRunCount: 0, runs: [] }), /prefilling/);
});

const liftSectionFunction = (name, pick) => {
  const declaration = findSoleNode(
    source,
    `${name} declaration`,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  );
  const js = transpile(
    `const lifted = ${pick(declaration.initializer).getText(source)};`,
    ts.ModuleKind.CommonJS
  );
  return (scope) =>
    new Function(...Object.keys(scope), `${js}\nreturn lifted;`)(...Object.values(scope));
};
const liftLoadHistory = liftSectionFunction('loadHistory', (node) => node.arguments[0]);
const liftLoadSessions = liftSectionFunction('loadSessions', (node) => node.arguments[0]);
const liftHistoryHandler = liftSectionFunction('handlePrefillHistoryUpdated', (node) => node);
const liftStoreHistory = liftSectionFunction('storeHistory', (node) => node.arguments[0]);
const liftFinishHistoryRead = liftSectionFunction('finishHistoryRead', (node) => node.arguments[0]);

// The two helpers every history reader calls; they read the scope's refs and setters.
const withHistoryHelpers = (scope) => {
  scope.storeHistory = liftStoreHistory(scope);
  scope.finishHistoryRead = liftFinishHistoryRead(scope);
  return scope;
};

const settle = () => new Promise((resolve) => setImmediate(resolve));

// One section's worth of state, with every history read held open until the test answers it.
const historySection = () => {
  const reads = [];
  const stored = { current: {} };
  const errors = [];
  const scheduled = [];
  const newestHistoryReadRef = { current: new Map() };
  const scope = withHistoryHelpers({
    ApiService: {
      getPrefillSessionHistory: (sessionId) =>
        new Promise((resolve, reject) => reads.push({ sessionId, resolve, reject })),
      getPrefillSessions: async () => ({
        sessions: [{ sessionId: 'session' }],
        totalCount: 1
      }),
      getActivePrefillSessions: async () => []
    },
    t: (key) => key,
    onError: (message, error) => errors.push({ message, error }),
    setHistoryData: (update) => {
      stored.current = typeof update === 'function' ? update(stored.current) : update;
    },
    setLoadingHistory: () => undefined,
    setExpandedHistory: () => undefined,
    setSessions: () => undefined,
    setActiveSessions: () => undefined,
    setTotalCount: () => undefined,
    setCacheRoute: () => undefined,
    setHasLoadedSessions: () => undefined,
    setLoadingSessions: () => undefined,
    setSessionsError: () => undefined,
    getErrorMessage: String,
    mockMode: false,
    page: 1,
    pageSize: 10,
    statusFilter: '',
    platformFilter: 'all',
    sessionsRequestRef: { current: 0 },
    newestHistoryReadRef,
    pendingHistoryRef: { current: new Set() },
    failingHistoryRef: { current: new Set() },
    scheduleHistoryRefresh: (callback) => scheduled.push(callback)
  });
  const onEvent = liftHistoryHandler(scope);
  return {
    reads,
    stored,
    errors,
    scheduled,
    newestHistoryReadRef,
    loadHistory: liftLoadHistory(scope),
    loadSessions: liftLoadSessions(scope),
    // An event, then the refresh the section schedules for it.
    async event(sessionId) {
      void onEvent({ sessionId });
      const callbacks = scheduled.splice(0);
      callbacks.at(-1)?.();
      await settle();
    },
    rows: (label) => [{ appName: label }]
  };
};

test('an older history read answered last never replaces a newer one', async () => {
  const section = historySection();
  void section.loadHistory('session');
  await section.event('session');
  assert.equal(section.reads.length, 2);
  section.reads[1].resolve(section.rows('event'));
  await settle();
  section.reads[0].resolve(section.rows('expand'));
  await settle();
  assert.deepEqual(section.stored.current.session, section.rows('event'));
});

test('the page-load read answered after an event read does not replace it', async () => {
  const section = historySection();
  await section.loadSessions();
  await settle();
  assert.equal(section.reads.length, 1);
  await section.event('session');
  assert.equal(section.reads.length, 2);
  section.reads[1].resolve(section.rows('event'));
  await settle();
  section.reads[0].resolve(section.rows('page load'));
  await settle();
  assert.deepEqual(section.stored.current.session, section.rows('event'));
});

test('an event read that never answers does not stop the next one', async () => {
  const section = historySection();
  await section.event('session');
  await section.event('session');
  assert.equal(section.reads.length, 2);
  section.reads[1].resolve(section.rows('second'));
  await settle();
  assert.deepEqual(section.stored.current.session, section.rows('second'));
});

test('events schedule one refresh each and a refresh reads each waiting session once', async () => {
  const section = historySection();
  const onEvent = liftHistoryHandler(
    withHistoryHelpers({
      pendingHistoryRef: { current: new Set() },
      scheduleHistoryRefresh: (callback) => section.scheduled.push(callback),
      newestHistoryReadRef: section.newestHistoryReadRef,
      failingHistoryRef: { current: new Set() },
      ApiService: {
        getPrefillSessionHistory: (sessionId) =>
          new Promise((resolve, reject) => section.reads.push({ sessionId, resolve, reject }))
      },
      setHistoryData: () => undefined,
      setLoadingHistory: () => undefined,
      onError: () => undefined,
      t: (key) => key
    })
  );
  void onEvent({ sessionId: 'first' });
  void onEvent({ sessionId: 'first' });
  void onEvent({ sessionId: 'second' });
  assert.equal(section.scheduled.length, 3);
  section.scheduled.at(-1)();
  assert.deepEqual(
    section.reads.map((read) => read.sessionId),
    ['first', 'second']
  );
});

test('a run of failed history reads reports once, and a read that answers re-arms the report', async () => {
  const section = historySection();
  for (let attempt = 0; attempt < 3; attempt++) {
    await section.event('session');
    section.reads[attempt].reject(new Error('down'));
    await settle();
  }
  assert.equal(section.errors.length, 1);
  assert.equal(section.errors[0].message, 'management.prefillSessions.errors.loadHistory');
  await section.event('session');
  section.reads[3].resolve(section.rows('back'));
  await settle();
  await section.event('session');
  section.reads[4].reject(new Error('down again'));
  await settle();
  assert.equal(section.errors.length, 2);
});

test('reads still open when the section closes store nothing and report nothing', async () => {
  const section = historySection();
  await section.event('session');
  await section.event('session');
  section.newestHistoryReadRef.current.clear();
  section.reads[0].reject(new Error('down'));
  section.reads[1].resolve(section.rows('late'));
  await settle();
  assert.equal(section.errors.length, 0);
  assert.deepEqual(section.stored.current, {});
});
