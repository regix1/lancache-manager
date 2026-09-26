import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  compileTree,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const serviceNames = await import(await compileTree('../src/utils/serviceDisplayName.ts'));
const liveNames = await import(
  await compileTree('../src/components/features/downloads/liveDownloadPreviews.ts')
);
const downloadGrouping = await import(
  await compileTree('../src/components/features/downloads/downloadGrouping.ts')
);

class MemoryStorage {
  constructor(entries = []) {
    this.values = new Map(entries);
  }

  getItem(key) {
    return this.values.has(key) ? this.values.get(key) : null;
  }
}

const currentSource = parseSource(DOWNLOADS_TAB, typescript.ScriptKind.TSX);
const storageKeysNode = findSoleNode(
  currentSource,
  'STORAGE_KEYS declaration',
  (node) =>
    typescript.isVariableDeclaration(node) &&
    node.name.getText(currentSource) === 'STORAGE_KEYS' &&
    node.initializer !== undefined
);
const storageKeys = bindLifted(
  `() => (${storageKeysNode.initializer.getText(currentSource)})`,
  {}
)();

const loadHideSmallFilesSource = liftConstArrow(DOWNLOADS_TAB, 'loadHideSmallFiles');
const serviceOptionsSource = liftHookCallback(DOWNLOADS_TAB, 'useMemo', 'const groups = new Map');
const availableServicesSource = liftHookCallback(
  DOWNLOADS_TAB,
  'useMemo',
  'serviceFilterOptions.map'
);
const exportFilterNode = findSoleNode(currentSource, 'download export filter', (node) => {
  if (!typescript.isCallExpression(node) || node.arguments.length !== 1) return false;
  if (!typescript.isPropertyAccessExpression(node.expression)) return false;
  const [argument] = node.arguments;
  return (
    node.expression.name.getText(currentSource) === 'filter' &&
    typescript.isArrowFunction(argument) &&
    argument.getText(currentSource).includes('settings.hideSmallFiles')
  );
});
const exportFilterSource = exportFilterNode.arguments[0].getText(currentSource);

const getInitializer = (sourceFile, name, contains = '') => {
  const declaration = findSoleNode(sourceFile, `${name} initializer`, (node) => {
    if (!typescript.isVariableDeclaration(node) || node.name.getText(sourceFile) !== name) {
      return false;
    }
    if (node.initializer === undefined) return false;
    return contains === '' || node.initializer.getText(sourceFile).includes(contains);
  });
  return declaration.initializer.getText(sourceFile);
};

const runInitializer = (sourceFile, name, bindings, contains = '') =>
  bindLifted(`() => (${getInitializer(sourceFile, name, contains)})`, bindings)();

const translate = (key, values = {}) => {
  if (key === 'downloads.active.depotLabel') return `Depot ${values.depotId}`;
  if (key === 'downloads.tab.groups.unknownOther') return 'Unknown/Other';
  if (key === 'downloads.tab.groups.steamApp') return `Steam App ${values.appId}`;
  if (key === 'downloads.tab.groups.serviceDownloads') return `${values.service} Downloads`;
  if (key === 'dashboard.downloadsPanel.serviceGroup') return `${values.service} Downloads`;
  if (key === 'downloads.tab.retro.gameFallback') return 'Unknown Game';
  return key;
};

const makeServiceOptions = (serviceFilterOptions) => {
  const availableServices = bindLifted(availableServicesSource, { serviceFilterOptions })();
  const t = (key) => (key === 'downloads.tab.filters.allServices' ? 'All Services' : key);
  const options = bindLifted(serviceOptionsSource, {
    availableServices,
    getServiceFilterKey: serviceNames.getServiceFilterKey,
    formatServiceLabel: serviceNames.formatServiceLabel,
    t
  })();
  return { availableServices, options };
};

test('service options keep every service, fold aliases once, and never add a divider', () => {
  const cases = [
    {
      name: 'all large',
      input: [
        { service: 'steam', hasLargeFiles: true },
        { service: 'wsus', hasLargeFiles: true }
      ],
      expected: [
        { value: 'all', label: 'All Services' },
        { value: 'steam', label: 'Steam' },
        { value: 'wsus', label: 'Wsus' }
      ]
    },
    {
      name: 'all small',
      input: [
        { service: 'steam', hasLargeFiles: false },
        { service: 'wsus', hasLargeFiles: false }
      ],
      expected: [
        { value: 'all', label: 'All Services' },
        { value: 'steam', label: 'Steam' },
        { value: 'wsus', label: 'Wsus' }
      ]
    },
    {
      name: 'mixed aliases',
      input: [
        { service: 'xboxlive', hasLargeFiles: false },
        { service: 'steam', hasLargeFiles: true },
        { service: 'microsoft', hasLargeFiles: false },
        { service: 'xbox', hasLargeFiles: true },
        { service: 'steam', hasLargeFiles: false }
      ],
      expected: [
        { value: 'all', label: 'All Services' },
        { value: 'xbox', label: 'Xbox' },
        { value: 'steam', label: 'Steam' }
      ]
    }
  ];

  for (const scenario of cases) {
    const before = structuredClone(scenario.input);
    const { options } = makeServiceOptions(scenario.input);
    assert.deepEqual(options, scenario.expected, scenario.name);
    assert.ok(
      options.every((option) => option.value !== 'divider'),
      scenario.name
    );
    assert.deepEqual(scenario.input, before, `${scenario.name} input stays unchanged`);
  }
});

test('hide-small-files storage keeps current and legacy semantics', () => {
  const load = (entries) =>
    bindLifted(loadHideSmallFilesSource, {
      storage: new MemoryStorage(entries),
      STORAGE_KEYS: storageKeys
    })();

  assert.equal(load([]), false);
  assert.equal(load([[storageKeys.HIDE_SMALL_FILES, 'true']]), true);
  assert.equal(load([[storageKeys.HIDE_SMALL_FILES, 'false']]), false);
  assert.equal(load([[storageKeys.LEGACY_SHOW_SMALL_FILES, 'false']]), true);
  assert.equal(load([[storageKeys.LEGACY_SHOW_SMALL_FILES, 'true']]), false);
  assert.equal(
    load([
      [storageKeys.HIDE_SMALL_FILES, 'false'],
      [storageKeys.LEGACY_SHOW_SMALL_FILES, 'false']
    ]),
    false,
    'the current key takes precedence over the legacy key'
  );
});

test('the shipped export filter still removes only positive files below one MiB', () => {
  const rows = [
    { id: 1, totalBytes: 0, clientIp: '10.0.0.1', service: 'steam', cacheHitPercent: 0 },
    { id: 2, totalBytes: 512_000, clientIp: '10.0.0.1', service: 'steam', cacheHitPercent: 0 },
    { id: 3, totalBytes: 1_048_575, clientIp: '10.0.0.1', service: 'steam', cacheHitPercent: 0 },
    { id: 4, totalBytes: 1_048_576, clientIp: '10.0.0.1', service: 'steam', cacheHitPercent: 0 },
    { id: 5, totalBytes: 2_000_000, clientIp: '10.0.0.1', service: 'steam', cacheHitPercent: 0 }
  ];
  const settings = {
    hideMetadata: false,
    hideSmallFiles: true,
    hideLocalhost: false,
    hideEvicted: false,
    hideUnknownGames: false,
    hitMissFilter: 'all',
    selectedService: 'all'
  };
  const predicate = bindLifted(exportFilterSource, {
    settings,
    evictedDataMode: 'show',
    isUnmappedSteam: () => false,
    getServiceFilterKey: serviceNames.getServiceFilterKey,
    selectedClientIps: null,
    query: '',
    queryMatchesUnknownLabel: false
  });

  assert.deepEqual(
    rows.filter(predicate).map((row) => row.id),
    [1, 4, 5]
  );
  settings.hideSmallFiles = false;
  assert.deepEqual(
    rows.filter(predicate).map((row) => row.id),
    [1, 2, 3, 4, 5]
  );
});

test('the shared helper preserves real titles and lowercases recognized service placeholders', () => {
  const cases = [
    ['Xbox Live', 'microsoft', 'Existing', 'xbox'],
    [undefined, 'xboxlive', 'Existing', 'xbox'],
    ['Windows Update', 'wsus', 'Existing', 'wsus'],
    ['Epic Games', 'epicgames', 'Existing', 'epicgames'],
    ['Riot Games', 'riot', 'Existing', 'riot'],
    ['Steam App 730', 'steam', 'Existing', 'Steam App 730'],
    [undefined, 'steam', 'Depot 731', 'Depot 731'],
    ['Forza Horizon 5', 'xboxlive', 'Existing', 'Forza Horizon 5'],
    ['Xbox Live Arcade Collection', 'microsoft', 'Existing', 'Xbox Live Arcade Collection'],
    ['Windows Update', 'steam', 'Existing', 'Windows Update']
  ];
  for (const [gameName, service, emptyName, expected] of cases) {
    assert.equal(liveNames.getGameDisplayName(gameName, service, emptyName), expected);
  }
});

test('single-download grouping changes only the visible title', () => {
  const source = {
    id: 71,
    gameName: 'Windows Update',
    service: 'wsus',
    totalBytes: 2_000_000,
    cacheHitBytes: 1_500_000,
    cacheMissBytes: 500_000,
    clientIp: '10.0.0.7',
    startTimeUtc: '2026-09-25T12:00:00Z',
    isEvicted: false
  };
  const before = structuredClone(source);
  const group = downloadGrouping.toGroup(source);

  assert.equal(group.name, 'wsus');
  assert.equal(group.service, 'wsus');
  assert.equal(group.hasRealGameName, false);
  assert.deepEqual(group.downloadIds, [71]);
  assert.deepEqual(source, before);
});

test('event grouping executes the shipped name expression without changing group identity', () => {
  const groupSource = liftConstArrow(
    'src/components/features/events/EventList.tsx',
    'groupDownloadsByGame'
  );
  const groupDownloadsByGame = bindLifted(groupSource, {
    getServiceFilterKey: serviceNames.getServiceFilterKey,
    getGameDisplayName: liveNames.getGameDisplayName
  });
  const rows = [
    { service: 'wsus', gameName: 'Windows Update', totalBytes: 10 },
    { service: 'wsus', gameName: 'Windows Update', totalBytes: 20 },
    { service: 'xboxlive', gameName: 'Forza Horizon 5', totalBytes: 30 }
  ];
  const groups = groupDownloadsByGame(rows, 'Unknown');

  assert.deepEqual(groups, [
    { name: 'wsus', service: 'wsus', totalBytes: 30, count: 2 },
    { name: 'Forza Horizon 5', service: 'xboxlive', totalBytes: 30, count: 1 }
  ]);
});

test('DownloadsTab conversion corrects ordinary titles and preserves special branches', () => {
  const toDownloadGroupSource = liftHookCallback(DOWNLOADS_TAB, 'useCallback', 'let name: string');
  const toDownloadGroup = bindLifted(toDownloadGroupSource, {
    t: translate,
    getServiceDisplayName: serviceNames.getServiceDisplayName,
    getServiceFilterKey: serviceNames.getServiceFilterKey,
    getGameDisplayName: liveNames.getGameDisplayName
  });
  const baseRow = {
    id: 'game-wsus',
    appName: 'Windows Update',
    service: 'wsus',
    steamAppId: null,
    groupType: 'game',
    downloadIds: [1],
    totalBytes: 10,
    cacheHitBytes: 5,
    cacheMissBytes: 5,
    clientIps: ['10.0.0.1'],
    startTimeUtc: '2026-09-25T12:00:00Z',
    lastStartTimeUtc: '2026-09-25T12:00:00Z',
    requestCount: 1,
    isEvicted: false,
    isPartiallyEvicted: false,
    hasRealGameName: false
  };
  assert.equal(toDownloadGroup(baseRow, []).name, 'wsus');
  assert.equal(toDownloadGroup({ ...baseRow, id: 'unknown-other' }, []).name, 'Unknown/Other');
  assert.equal(
    toDownloadGroup({ ...baseRow, id: 'service-wsus', appName: 'wsus' }, []).name,
    'Wsus Downloads'
  );
  assert.equal(
    toDownloadGroup(
      { ...baseRow, id: 'steam-730', service: 'steam', steamAppId: 730, appName: 'Steam' },
      []
    ).name,
    'Steam App 730'
  );
});

test('dashboard title expressions execute the shared decision for active, recent, and grouped rows', () => {
  const activeSource = parseSource(
    'src/components/features/downloads/ActiveDownloadsView.tsx',
    typescript.ScriptKind.TSX
  );
  const activeTitle = runInitializer(activeSource, 'displayName', {
    game: { gameName: 'Xbox Live', service: 'microsoft', depotId: 0 },
    getGameDisplayName: liveNames.getGameDisplayName,
    t: translate
  });
  assert.equal(activeTitle, 'xbox');

  const recentSource = parseSource(
    'src/components/features/dashboard/RecentDownloadsPanel.tsx',
    typescript.ScriptKind.TSX
  );
  const currentTitle = runInitializer(
    recentSource,
    'displayName',
    {
      game: { gameName: 'Windows Update', service: 'wsus', depotId: 0 },
      getGameDisplayName: liveNames.getGameDisplayName,
      getServiceDisplayName: serviceNames.getServiceDisplayName,
      t: translate
    },
    'getGameDisplayName'
  );
  assert.equal(currentTitle, 'wsus');

  const singleItem = {
    service: 'wsus',
    gameName: 'Windows Update',
    depotId: 0,
    totalBytes: 10,
    cacheHitPercent: 0,
    cacheHitBytes: 0,
    startTimeUtc: '2026-09-25T12:00:00Z',
    clientIp: '10.0.0.1',
    isEvicted: false
  };
  const singleDisplay = runInitializer(recentSource, 'display', {
    isGroup: false,
    isServiceBucket: false,
    item: singleItem,
    t: translate,
    getGameDisplayName: liveNames.getGameDisplayName,
    getServiceDisplayName: serviceNames.getServiceDisplayName,
    formatServiceLabel: serviceNames.formatServiceLabel
  });
  assert.equal(singleDisplay.name, 'wsus');

  const groupedDisplay = runInitializer(recentSource, 'display', {
    isGroup: true,
    isServiceBucket: false,
    item: {
      ...singleItem,
      id: 'game-wsus',
      name: 'Windows Update',
      downloadIds: [1],
      lastSeen: '2026-09-25T12:00:00Z',
      clientIps: ['10.0.0.1'],
      count: 1,
      isPartiallyEvicted: false
    },
    t: translate,
    getGameDisplayName: liveNames.getGameDisplayName,
    getServiceDisplayName: serviceNames.getServiceDisplayName,
    formatServiceLabel: serviceNames.formatServiceLabel
  });
  assert.equal(groupedDisplay.name, 'wsus');
});

test('Retro rendered, alt, and measurement expressions produce the same title', () => {
  const rowSource = parseSource(
    'src/components/features/downloads/RetroRow.tsx',
    typescript.ScriptKind.TSX
  );
  const bindings = {
    data: { gameName: 'Windows Update', service: 'wsus' },
    getGameDisplayName: liveNames.getGameDisplayName,
    getServiceDisplayName: serviceNames.getServiceDisplayName,
    t: translate
  };
  const title = runInitializer(rowSource, 'title', bindings, 'getGameDisplayName');
  const imageTitle = runInitializer(rowSource, 'imageTitle', bindings, 'getGameDisplayName');
  assert.equal(title, 'wsus');
  assert.equal(imageTitle, 'wsus');

  const altUses = [];
  const visit = (node) => {
    if (
      typescript.isJsxAttribute(node) &&
      node.name.getText(rowSource) === 'alt' &&
      node.initializer?.getText(rowSource) === '{imageTitle}'
    ) {
      altUses.push(node);
    }
    typescript.forEachChild(node, visit);
  };
  visit(rowSource);
  assert.equal(altUses.length, 2);

  const viewSource = parseSource(
    'src/components/features/downloads/RetroView.tsx',
    typescript.ScriptKind.TSX
  );
  const appNameNode = findSoleNode(
    viewSource,
    'measured appName',
    (node) =>
      typescript.isPropertyAssignment(node) &&
      node.name.getText(viewSource) === 'appName' &&
      node.initializer.getText(viewSource).includes('getGameDisplayName')
  );
  const appName = bindLifted(`() => (${appNameNode.initializer.getText(viewSource)})`, bindings)();
  assert.equal(appName, title);
});
