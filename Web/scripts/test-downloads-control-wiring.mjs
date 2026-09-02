import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  MemoryStorage,
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  parseSource,
  transpile
} from './transpile-module.mjs';

/**
 * The Downloads page filters, sorts and slices in the database now, so a control only does
 * something if its value reaches the request. A checkbox that renders and never reaches the query
 * string looks like it works and changes nothing on screen.
 *
 * Nothing below restates the wiring. The object the page hands the fetch hook, the props it hands
 * the retro table, the object that table hands the same hook, the params the hook builds and the
 * method that turns those into a URL are all lifted out of the files that ship and run with their
 * free variables supplied by name. Drop a line and the query loses a parameter here; rename a free
 * variable and the lift throws instead of quietly passing against a copy.
 *
 * The two fetches are separate: the compact, card and normal views share one, the retro table has
 * its own, and the five filter checkboxes render in all four. So each of those five is checked in
 * both queries.
 */

const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const downloadsTab = parseSource(DOWNLOADS_TAB, ts.ScriptKind.TSX);
const retroView = parseSource('src/components/features/downloads/RetroView.tsx', ts.ScriptKind.TSX);
const fetchHook = parseSource('src/components/features/downloads/useRetroDownloads.ts');
const apiService = parseSource('src/services/api.service.ts');

const clientLabelUrl = await compileToUrl('../src/utils/clientLabel.ts');
const { findClientFilterGroup } = await import(
  await compileToUrl('../src/utils/clientFilterOptions.ts', { './clientLabel': clientLabelUrl })
);
const { formatServiceLabel, getServiceFilterKey } = await import(
  await compileToUrl('../src/utils/serviceDisplayName.ts')
);

/** Enough of i18next for an option label; nothing here asserts on wording. */
const t = (key) => key;

/** Source text of the value a named `const` is declared with. */
const initializerOf = (sourceFile, name) =>
  findSoleNode(
    sourceFile,
    `the ${name} declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === name &&
      node.initializer !== undefined
  ).initializer.getText(sourceFile);

/** Runs a lifted expression with its free variables supplied and hands back what it evaluates to. */
const evaluate = (source, bindings) => bindLifted(`() => (${source})`, bindings)();

// -- the request builder ----------------------------------------------------------------------

const API_BASE = 'http://cache/api';

const getRetroDownloadsText = findSoleNode(
  apiService,
  'the getRetroDownloads method',
  (node) => ts.isMethodDeclaration(node) && node.name.getText(apiService) === 'getRetroDownloads'
).getText(apiService);

assert.ok(
  getRetroDownloadsText.startsWith('static '),
  'getRetroDownloads is expected to be a static method'
);

/**
 * The query string the shipped service builds for a params object. `static` comes off because the
 * text is dropped into an object literal, and that object is what `this.getFetchOptions` and
 * `this.handleResponse` resolve against.
 */
const askServer = async (params) => {
  const asked = [];
  const compiled = transpile(
    `const holder = { ${getRetroDownloadsText.slice('static '.length)} };`,
    ts.ModuleKind.CommonJS
  );
  const holder = new Function('API_BASE', 'fetch', 'isAbortError', `${compiled}\nreturn holder;`)(
    API_BASE,
    async (url) => {
      asked.push(url);
      return {};
    },
    () => false
  );
  holder.getFetchOptions = () => ({});
  holder.handleResponse = async () => ({ items: [] });

  await holder.getRetroDownloads(params);
  assert.equal(asked.length, 1, 'one page is one request');
  return new URL(asked[0]).searchParams;
};

// -- the fetch hook ---------------------------------------------------------------------------

const paramsText = initializerOf(fetchHook, 'params');

/**
 * Every option name the hook pulls off its argument. An option the caller leaves out reads as
 * undefined here, which is what React hands the hook too.
 */
const hookOptionNames = findSoleNode(
  fetchHook,
  'the options the fetch hook destructures',
  (node) =>
    ts.isVariableDeclaration(node) &&
    ts.isObjectBindingPattern(node.name) &&
    node.initializer !== undefined &&
    node.initializer.getText(fetchHook) === 'options'
).name.elements.map((element) => element.name.getText(fetchHook));

const toParams = (options) =>
  evaluate(paramsText, Object.fromEntries(hookOptionNames.map((name) => [name, options[name]])));

// -- the page ---------------------------------------------------------------------------------

const STORAGE_KEYS = evaluate(initializerOf(downloadsTab, 'STORAGE_KEYS'), {});
const DEFAULT_ITEMS_PER_PAGE = evaluate(initializerOf(downloadsTab, 'DEFAULT_ITEMS_PER_PAGE'), {});

/** The settings the page starts a visit with, given what browser storage holds. */
const savedSettings = (stored = {}) => {
  const storage = new MemoryStorage();
  for (const [key, value] of Object.entries(stored)) {
    storage.setItem(key, value);
  }
  const readStorage = { storage, STORAGE_KEYS };
  return bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useState', 'savedViewMode'), {
    ...readStorage,
    DEFAULT_ITEMS_PER_PAGE,
    loadHideMetadata: bindLifted(initializerOf(downloadsTab, 'loadHideMetadata'), readStorage),
    loadHideSmallFiles: bindLifted(initializerOf(downloadsTab, 'loadHideSmallFiles'), readStorage)
  })();
};

/** The eviction mode the page paints with before the server answers what it is. */
const seededEvictedDataMode = (stored = {}) => {
  const storage = new MemoryStorage();
  for (const [key, value] of Object.entries(stored)) {
    storage.setItem(key, value);
  }
  return bindLifted(initializerOf(downloadsTab, 'readCachedEvictedDataMode'), {
    storage,
    STORAGE_KEYS,
    isEvictedDataMode: bindLifted(initializerOf(downloadsTab, 'isEvictedDataMode'), {})
  })();
};

const clientFilterSource = liftHookCallback(DOWNLOADS_TAB, 'useMemo', 'findClientFilterGroup');
const serviceOptionsSource = liftHookCallback(
  DOWNLOADS_TAB,
  'useMemo',
  'getServiceFilterKey(service)'
);
const tabFetchOptionsText = findSoleNode(
  downloadsTab,
  'the page fetch',
  (node) =>
    ts.isCallExpression(node) && node.expression.getText(downloadsTab) === 'useRetroDownloads'
).arguments[0].getText(downloadsTab);

const retroFetch = findSoleNode(
  retroView,
  'the retro table fetch',
  (node) => ts.isCallExpression(node) && node.expression.getText(retroView) === 'useRetroDownloads'
);
const retroFetchOptionsText = retroFetch.arguments[0].getText(retroView);

/** Every name the retro fetch reads, which is how the props it needs are told from the rest. */
const retroFetchReads = new Set(
  collectNodes(retroFetch.arguments[0], ts.isIdentifier).map((node) => node.getText(retroView))
);

const retroPropsText = findSoleNode(
  downloadsTab,
  'the retro table element',
  (node) => ts.isJsxSelfClosingElement(node) && node.tagName.getText(downloadsTab) === 'RetroView'
)
  .attributes.properties.filter(
    (attribute) =>
      ts.isJsxAttribute(attribute) && retroFetchReads.has(attribute.name.getText(downloadsTab))
  )
  .map(
    (attribute) =>
      `${attribute.name.getText(downloadsTab)}: ${attribute.initializer.expression.getText(downloadsTab)}`
  )
  .join(', ');

/** What the page reads from outside the toolbar: the header, and the mode Management owns. */
const PAGE_CONTEXT = {
  activeTab: 'recent',
  currentPage: 1,
  evictedDataMode: 'show',
  mockMode: false,
  retroTimeParams: {},
  retroEventId: undefined
};

const pageBindings = (settings, { clientGroups = [], ...context }) => ({
  ...PAGE_CONTEXT,
  ...context,
  settings,
  serverClientFilter: bindLifted(clientFilterSource, {
    settings,
    clientGroups,
    findClientFilterGroup
  })(),
  debouncedSearchQuery: settings.searchQuery
});

/** The query the compact, card and normal views send. */
const groupedQuery = (settings, context = {}) =>
  askServer(toParams(evaluate(tabFetchOptionsText, pageBindings(settings, context))));

/** The query the retro table sends, through the props the page hands it. */
const retroQuery = (settings, context = {}) =>
  askServer(
    toParams(
      evaluate(
        retroFetchOptionsText,
        evaluate(`{${retroPropsText}}`, pageBindings(settings, context))
      )
    )
  );

/** Option values of a dropdown or segmented control, picked out by a label key only it carries. */
const controlValues = (labelKey) =>
  collectNodes(
    downloadsTab,
    (node) =>
      ts.isJsxAttribute(node) &&
      node.name.getText(downloadsTab) === 'options' &&
      node.initializer !== undefined &&
      node.initializer.getText(downloadsTab).includes(labelKey)
  ).map((attribute) =>
    evaluate(attribute.initializer.expression.getText(downloadsTab), { t }).map(
      (option) => option.value
    )
  );

// -- the untouched default --------------------------------------------------------------------

test('a first visit asks for every row', async () => {
  const query = await groupedQuery(savedSettings());

  assert.equal(query.get('service'), 'all', 'every service');
  assert.equal(query.get('client'), 'all', 'every client');
  assert.equal(query.get('hitMiss'), 'all', 'hits and misses');
  assert.equal(query.get('search'), null, 'nothing typed');
  assert.equal(query.get('hideLocalhost'), 'false');
  assert.equal(query.get('hideSmallFiles'), 'false');
  assert.equal(query.get('hideUnknown'), 'false');
  assert.equal(query.get('hideEvicted'), 'false');
  assert.equal(query.get('showZeroBytes'), 'true', 'zero-byte sessions are shown, not hidden');
  assert.equal(query.get('startTime'), null, 'no range narrows the first visit');
  assert.equal(query.get('eventId'), null, 'no event narrows the first visit');
});

// -- the five filter checkboxes ---------------------------------------------------------------

test('hide metadata asks the server to drop zero-byte sessions', async () => {
  const settings = { ...savedSettings(), hideMetadata: true };
  assert.equal((await groupedQuery(settings)).get('showZeroBytes'), 'false');
  assert.equal((await retroQuery(settings)).get('showZeroBytes'), 'false');
});

test('hide small files reaches the query from every view', async () => {
  const settings = { ...savedSettings(), hideSmallFiles: true };
  assert.equal((await groupedQuery(settings)).get('hideSmallFiles'), 'true');
  assert.equal((await retroQuery(settings)).get('hideSmallFiles'), 'true');
});

test('hide localhost reaches the query from every view', async () => {
  const settings = { ...savedSettings(), hideLocalhost: true };
  assert.equal((await groupedQuery(settings)).get('hideLocalhost'), 'true');
  assert.equal((await retroQuery(settings)).get('hideLocalhost'), 'true');
});

test('hide unknown games reaches the query from every view', async () => {
  const settings = { ...savedSettings(), hideUnknownGames: true };
  assert.equal((await groupedQuery(settings)).get('hideUnknown'), 'true');
  assert.equal((await retroQuery(settings)).get('hideUnknown'), 'true');
});

test('hide evicted reaches the query from every view', async () => {
  const settings = { ...savedSettings(), hideEvicted: true };
  assert.equal((await groupedQuery(settings)).get('hideEvicted'), 'true');
  assert.equal((await retroQuery(settings)).get('hideEvicted'), 'true');
});

// The stored mode is not a toolbar control - it is set in Management and hides evicted rows for
// every reader. The page ORs it with the checkbox, and seeds it to 'hide' until the server answers,
// so the very first request of a visit hides them whatever the checkbox says.
test('the stored eviction mode hides evicted rows on its own', async () => {
  const settings = savedSettings();
  assert.equal(seededEvictedDataMode(), 'hide', 'hidden until the server answers');

  const beforeTheServerAnswers = await groupedQuery(settings, {
    evictedDataMode: seededEvictedDataMode()
  });
  assert.equal(beforeTheServerAnswers.get('hideEvicted'), 'true');

  const showing = await groupedQuery(settings, { evictedDataMode: 'show' });
  assert.equal(showing.get('hideEvicted'), 'false');
  assert.equal(
    (await retroQuery(settings, { evictedDataMode: 'hide' })).get('hideEvicted'),
    'true'
  );
});

// -- the dropdowns and the search box ---------------------------------------------------------

test('the service dropdown offers one Xbox entry and sends its folded key', async () => {
  const options = bindLifted(serviceOptionsSource, {
    availableServices: ['steam', 'xbox', 'xboxlive', 'microsoft', 'wsus'],
    filteredAvailableServices: ['steam', 'xbox', 'xboxlive', 'microsoft', 'wsus'],
    getServiceFilterKey,
    formatServiceLabel,
    t
  })();

  assert.deepEqual(
    options.map((option) => option.value),
    ['all', 'steam', 'xbox', 'wsus'],
    'the three Xbox aliases are one entry and wsus keeps its own'
  );

  const settings = { ...savedSettings(), selectedService: 'xbox' };
  assert.equal((await groupedQuery(settings)).get('service'), 'xbox');
  assert.equal((await retroQuery(settings)).get('service'), 'xbox');

  const wsus = { ...savedSettings(), selectedService: 'wsus' };
  assert.equal((await groupedQuery(wsus)).get('service'), 'wsus');
});

test('a selected client group travels as its member addresses', async () => {
  const clientGroups = [{ id: 3, nickname: 'Lab', memberIps: ['10.0.0.7', '10.0.0.8'] }];

  const group = { ...savedSettings(), selectedClient: 'group-3' };
  assert.equal((await groupedQuery(group, { clientGroups })).get('client'), '10.0.0.7,10.0.0.8');
  assert.equal((await retroQuery(group, { clientGroups })).get('client'), '10.0.0.7,10.0.0.8');

  const single = { ...savedSettings(), selectedClient: '10.0.0.9' };
  assert.equal((await groupedQuery(single)).get('client'), '10.0.0.9');
});

test('what is typed in the search box reaches the query', async () => {
  const settings = { ...savedSettings(), searchQuery: 'team fortress' };
  assert.equal((await groupedQuery(settings)).get('search'), 'team fortress');
  assert.equal((await retroQuery(settings)).get('search'), 'team fortress');
});

test('the hit/miss control sends the bucket it is set to', async () => {
  const [mobile, desktop] = controlValues('downloads.tab.filters.hitMissMiss');
  assert.deepEqual(mobile, ['all', 'hit', 'miss']);
  assert.deepEqual(desktop, mobile, 'the phone and the wide layout offer the same buckets');

  for (const bucket of mobile) {
    const settings = { ...savedSettings(), hitMissFilter: bucket };
    assert.equal((await groupedQuery(settings)).get('hitMiss'), bucket);
    assert.equal((await retroQuery(settings)).get('hitMiss'), bucket);
  }
});

test('the header time range reaches the query', async () => {
  const range = { retroTimeParams: { startTime: 1735689600, endTime: 1738368000 } };
  const settings = savedSettings();

  const grouped = await groupedQuery(settings, range);
  assert.equal(grouped.get('startTime'), '1735689600');
  assert.equal(grouped.get('endTime'), '1738368000');

  const retro = await retroQuery(settings, range);
  assert.equal(retro.get('startTime'), '1735689600');
  assert.equal(retro.get('endTime'), '1738368000');
});

test('the tagged event reaches the query', async () => {
  const settings = savedSettings();
  assert.equal((await groupedQuery(settings, { retroEventId: 12 })).get('eventId'), '12');
  assert.equal((await retroQuery(settings, { retroEventId: 12 })).get('eventId'), '12');
});

// -- the pager and the page size --------------------------------------------------------------

test('the page number and the page size reach the query', async () => {
  const settings = { ...savedSettings(), itemsPerPage: 20 };
  const query = await groupedQuery(settings, { currentPage: 7 });

  assert.equal(query.get('page'), '7');
  assert.equal(query.get('pageSize'), '20');
});

// -- the sort dropdown ------------------------------------------------------------------------

const [mobileSorts, desktopSorts] = controlValues('downloads.tab.sort.alphabetical');

test('the phone and the wide layout offer the same sorts', () => {
  assert.equal(mobileSorts.length, 9);
  assert.deepEqual(desktopSorts, mobileSorts);
});

for (const token of mobileSorts) {
  test(`the ${token} sort reaches the query`, async () => {
    const settings = { ...savedSettings(), sortOrder: token };
    assert.equal((await groupedQuery(settings)).get('sort'), token);
    assert.equal((await retroQuery(settings)).get('sort'), token);
  });
}

// The dropdown dropped "latest" but the choice is stored, so a reader who picked it still has it.
// It reaches the server's catch-all branch, the same one "recent" reaches, and the page rewrites it
// so the dropdown has a value to show as selected.
test('a stored latest sort comes back as recent', async () => {
  const settings = savedSettings({ [STORAGE_KEYS.SORT_ORDER]: 'latest' });
  assert.equal(settings.sortOrder, 'recent');
  assert.equal((await groupedQuery(settings)).get('sort'), 'recent');
});

// -- the grouping toggles ---------------------------------------------------------------------

test('group unknown games reaches the query', async () => {
  assert.equal(
    (await groupedQuery({ ...savedSettings(), groupUnknownGames: true })).get('groupUnknownGames'),
    'true'
  );
  assert.equal(
    (await groupedQuery({ ...savedSettings(), groupUnknownGames: false })).get('groupUnknownGames'),
    'false'
  );
});

test('group by frequency reaches the query', async () => {
  assert.equal(
    (await groupedQuery({ ...savedSettings(), groupByFrequency: true })).get('groupByFrequency'),
    'true'
  );
  assert.equal(
    (await groupedQuery({ ...savedSettings(), groupByFrequency: false })).get('groupByFrequency'),
    'false'
  );
});

test('the retro table sends its own two grouping checkboxes', async () => {
  const settings = { ...savedSettings(), groupByGameRetro: true, groupByServiceRetro: true };
  const query = await retroQuery(settings);

  assert.equal(query.get('groupByGame'), 'true');
  assert.equal(query.get('groupByService'), 'true');

  const off = await retroQuery({
    ...savedSettings(),
    groupByGameRetro: false,
    groupByServiceRetro: false
  });
  assert.equal(off.get('groupByGame'), 'false');
  assert.equal(off.get('groupByService'), 'false');
});

// The grouped views have no control for these two: one title seen under two services has always
// been one row there, so they are sent on every request.
test('the grouped views always ask for one row per title', async () => {
  const query = await groupedQuery(savedSettings());
  assert.equal(query.get('groupByGame'), 'true');
  assert.equal(query.get('mergeAcrossServices'), 'true');
});

// A download still running is the row a reader on a cache box is most likely watching, so the
// grouped views list it as it downloads. The retro table is a history view and leaves it out.
test('only the grouped views ask for the download that is still running', async () => {
  assert.equal((await groupedQuery(savedSettings())).get('includeActive'), 'true');
  assert.equal((await retroQuery(savedSettings())).get('includeActive'), null);
});

// -- what runs while the retro table is showing -------------------------------------------------

// The retro table fetches its own page. Leaving the page's own fetch running there made every page
// turn, filter change and refresh two full aggregations of the whole table instead of one.
test('the page stops its own fetch while the retro table is showing', () => {
  const fetchFor = (viewMode) =>
    evaluate(tabFetchOptionsText, pageBindings({ ...savedSettings(), viewMode }, {}));

  assert.equal(fetchFor('retro').enabled, false, 'the retro table is the only one asking');
  assert.equal(fetchFor('compact').enabled, true, 'every other view is served by this fetch');
  assert.equal(fetchFor('normal').enabled, true);
  assert.equal(fetchFor('card').enabled, true);
});

// Mock rows are generated on the client, so there is no second server round trip to save by
// stopping this fetch, and the export reads its rows: off here wrote an empty file.
test('mock mode keeps the page fetching while the retro table is showing', () => {
  const fetchFor = (viewMode, mockMode) =>
    evaluate(tabFetchOptionsText, pageBindings({ ...savedSettings(), viewMode }, { mockMode }));

  assert.equal(fetchFor('retro', true).enabled, true, 'the retro export has rows to write');
  assert.equal(fetchFor('retro', false).enabled, false, 'and the real fetch still stops');
});

// -- the export ---------------------------------------------------------------------------------

const exportCall = findSoleNode(
  downloadsTab,
  'the row fetch the export makes',
  (node) =>
    ts.isCallExpression(node) &&
    node.expression.getText(downloadsTab) === 'ApiService.getDownloadRows'
);

// A hundred thousand rows is two hundred sequential requests. Leaving the Downloads page has to
// stop them, and the only way to stop them is the signal the fourth argument carries.
test('the export walk is handed something that can stop it', () => {
  assert.equal(exportCall.arguments.length, 4, 'the range, the tagged event and the abort signal');
  assert.match(exportCall.arguments[3].getText(downloadsTab), /signal/);
});

// The menu the export is started from closes on the click, so the disabled state on its own item
// tells the reader nothing. Without this the page looks idle for the whole walk.
test('an export in progress is visible on the page it was started from', () => {
  const source = downloadsTab.getText();
  assert.match(source, /exportLoading &&[\s\S]{0,80}<LoadingSpinner/);
});

// Every filter, sort and page-size change is a server round trip that rebuilds the grouped list.
// The rows already on screen stay there while it runs, so without this there is nothing to say a
// new answer is coming. The retro table runs its own fetch, so the indicator reads that one too.
test('a page still being fetched says so beside the search box', () => {
  const source = downloadsTab.getText();
  assert.match(
    source,
    /\(serverPage\.isFetching \|\| retroFetching\) &&[\s\S]{0,80}<LoadingSpinner/
  );
});
