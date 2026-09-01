import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback } from './transpile-module.mjs';

/**
 * The Downloads page asks the server for one page of grouped rows instead of reading the whole
 * table. Three pieces of that path decide whether the page still behaves the way it did, and all
 * three are lifted out of the shipping component here rather than restated:
 *
 *   - the client filter, which has to expand a selected client GROUP into its member addresses,
 *     because the server has no group concept and a raw "group-3" matches no row at all;
 *   - the row-to-group mapping, which has to take the membership answers from the row instead of
 *     from the single download the row carries;
 *   - the page assembly, which swaps in the fetched sessions for the one group that is open.
 *
 * Nothing below re-implements any of that. If an arrow moves, or a free variable is renamed, the
 * lift throws instead of quietly testing a copy that no longer ships.
 */

const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const clientLabelUrl = await compileToUrl('../src/utils/clientLabel.ts');
const { findClientFilterGroup } = await import(
  await compileToUrl('../src/utils/clientFilterOptions.ts', { './clientLabel': clientLabelUrl })
);
const { getServiceDisplayName, getServiceFilterKey } = await import(
  await compileToUrl('../src/utils/serviceDisplayName.ts')
);

/** Enough of i18next to tell the three title branches apart. */
const t = (key, values) => (values ? `${key}:${Object.values(values).join(',')}` : key);

const runClientFilter = (selectedClient, clientGroups) =>
  bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useMemo', 'findClientFilterGroup'), {
    settings: { selectedClient },
    clientGroups,
    findClientFilterGroup
  })();

const toDownloadGroup = bindLifted(
  liftHookCallback(DOWNLOADS_TAB, 'useCallback', 'downloads.tab.groups.unknownOther'),
  { t, getServiceDisplayName, getServiceFilterKey }
);

const buildPage = (items, expandedMembers = null) =>
  bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useMemo', 'expandedMembers.downloads'), {
    serverPage: { items },
    expandedMembers,
    toDownloadGroup
  })();

const download = (overrides = {}) => ({
  id: 7,
  service: 'steam',
  clientIp: '10.0.0.7',
  startTimeUtc: '2026-08-08T10:00:00Z',
  endTimeUtc: '2026-08-08T10:05:00Z',
  cacheHitBytes: 250,
  cacheMissBytes: 750,
  totalBytes: 1000,
  cacheHitPercent: 25,
  isActive: false,
  gameName: 'Team Fortress 2',
  gameAppId: 440,
  averageBytesPerSecond: 100,
  isEvicted: false,
  ...overrides
});

const row = (overrides = {}) => ({
  id: 'game-appid-440',
  startTimeUtc: '2026-08-08T09:00:00Z',
  endTimeUtc: '2026-08-08T10:05:00Z',
  lastStartTimeUtc: '2026-08-08T10:00:00Z',
  depotId: 441,
  appName: 'Team Fortress 2',
  steamAppId: 440,
  epicAppId: null,
  service: 'steam',
  datasource: 'default',
  clientIp: '10.0.0.7',
  averageBytesPerSecond: 100,
  cacheHitBytes: 250,
  cacheMissBytes: 750,
  cacheHitPercent: 25,
  totalBytes: 1000,
  requestCount: 4,
  downloadIds: [7, 8, 9, 10],
  clientIps: ['10.0.0.7', '10.0.0.8'],
  depotIds: [441],
  isEvicted: false,
  isPartiallyEvicted: false,
  primaryDownload: download(),
  hasRealGameName: true,
  groupType: 'game',
  ...overrides
});

// The bug this fixes: picking a client group in the retro view returned an empty table, because the
// dropdown value went to the server unchanged and no row has a client address of "group-3".
test('a selected client group is sent as its member addresses', () => {
  const groups = [{ id: 3, nickname: 'Lab', memberIps: ['10.0.0.7', '10.0.0.8'] }];
  assert.equal(runClientFilter('group-3', groups), '10.0.0.7,10.0.0.8');
});

test('a single selected address is sent as itself', () => {
  assert.equal(runClientFilter('10.0.0.9', []), '10.0.0.9');
});

test('no client selection asks for every client', () => {
  assert.equal(runClientFilter('all', []), 'all');
});

// A saved selection outlives the group it names. Sending the id through matches nothing, which is
// what the browser-side filter did with it, and is safer than silently widening to every client.
test('a selection naming a group that is gone is not widened to every client', () => {
  assert.equal(runClientFilter('group-3', []), 'group-3');
});

test('a game row keeps the resolved title the server sent', () => {
  const group = toDownloadGroup(row(), [download()]);
  assert.equal(group.id, 'game-appid-440');
  assert.equal(group.name, 'Team Fortress 2');
  assert.equal(group.type, 'game');
});

test('a service row is titled in the reader language, not with the raw service name', () => {
  const group = toDownloadGroup(
    row({ id: 'service-wsus', service: 'wsus', appName: 'wsus', groupType: 'content' }),
    []
  );
  assert.equal(group.name, 'downloads.tab.groups.serviceDownloads:Wsus');
  assert.equal(group.type, 'content');
});

test('a merged Xbox row is one row titled Xbox, whichever alias built it', () => {
  const group = toDownloadGroup(
    row({ id: 'service-xbox', service: 'microsoft', appName: 'Xbox', groupType: 'content' }),
    []
  );
  assert.equal(group.name, 'downloads.tab.groups.serviceDownloads:Xbox');
});

test('the Epic row keeps its own name rather than the folded service label', () => {
  const group = toDownloadGroup(
    row({ id: 'service-epicgames', service: 'epicgames', groupType: 'content' }),
    []
  );
  assert.equal(group.name, 'Epic Games');
});

test('the unknown bucket is titled from its own key', () => {
  const group = toDownloadGroup(
    row({
      id: 'unknown-other',
      service: 'unknown',
      appName: 'Unknown/Other',
      groupType: 'content'
    }),
    []
  );
  assert.equal(group.name, 'downloads.tab.groups.unknownOther');
});

// The reason the server had to answer these at all: a collapsed group carries its newest member
// only, so reading eviction or the title off that one row answers a question about the group with a
// fact about one download. Here the newest member is clean and unnamed while the group is neither.
test('the membership answers come from the row, not from the one download it carries', () => {
  const group = toDownloadGroup(row({ isPartiallyEvicted: true, hasRealGameName: true }), [
    download({ isEvicted: false, gameName: '' })
  ]);

  assert.equal(group.isEvicted, false);
  assert.equal(group.isPartiallyEvicted, true);
  assert.equal(group.hasRealGameName, true);
  assert.equal(group.count, 4, 'the session count is the whole membership, not the rows on hand');
  assert.deepEqual([...group.clientsSet], ['10.0.0.7', '10.0.0.8']);
  assert.deepEqual(
    group.downloadIds,
    [7, 8, 9, 10],
    'the event badges are counted over these, so they name the whole membership'
  );
});

// firstSeen is the group's earliest start and lastSeen its newest member's START. The row also
// carries an end time, and using it here would label a group with an instant no session began at.
test('the group is stamped with the newest member start, not the latest end', () => {
  const group = toDownloadGroup(row(), [download()]);
  assert.equal(group.firstSeen, '2026-08-08T09:00:00Z');
  assert.equal(group.lastSeen, '2026-08-08T10:00:00Z');
});

test('a collapsed group renders from the one download the server sent with it', () => {
  const [group] = buildPage([row()]);
  assert.equal(group.downloads.length, 1);
  assert.equal(group.downloads[0].id, 7);
});

test('the open group renders the sessions that were fetched for it', () => {
  const members = [download({ id: 7 }), download({ id: 8 }), download({ id: 9 })];
  const [group] = buildPage([row()], { groupId: 'game-appid-440', downloads: members });
  assert.deepEqual(
    group.downloads.map((d) => d.id),
    [7, 8, 9]
  );
});

test('sessions fetched for one group do not leak into the others', () => {
  const page = buildPage(
    [row(), row({ id: 'service-wsus', service: 'wsus', groupType: 'content' })],
    {
      groupId: 'game-appid-440',
      downloads: [download({ id: 7 }), download({ id: 8 })]
    }
  );
  assert.equal(page[0].downloads.length, 2);
  assert.equal(page[1].downloads.length, 1);
});

// The row is fetched in two queries, so a wipe landing between them can leave it without its
// download. An empty list renders the row's own totals; a missing field would throw.
test('a row whose download went away still renders', () => {
  const [group] = buildPage([row({ primaryDownload: null })]);
  assert.deepEqual(group.downloads, []);
  assert.equal(group.totalBytes, 1000);
});
