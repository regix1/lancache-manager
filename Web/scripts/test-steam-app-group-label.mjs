import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  compileTree,
  findSoleNode,
  liftHookCallback,
  moduleUrl,
  parseSource,
  translator
} from './transpile-module.mjs';

/**
 * The title of a Steam group whose depots never resolved to a game.
 *
 * The endpoint once named such a group "Steam App 620" in English, and the browser printed that
 * verbatim, so a Chinese reader got an English row. The row itself says which case it is: it
 * carries the app id and reports that no member resolved a real name, and the browser draws the
 * label from both. Reading the title text instead would break on any server rewording and would
 * also rename a game that is genuinely called "Steam App 620", which is why both cases are
 * asserted here.
 */

const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';
const RETRO_VIEW = 'src/components/features/downloads/RetroView.tsx';
const RETRO_ROW = 'src/components/features/downloads/RetroRow.tsx';

const { getGameDisplayName } = await import(
  await compileTree('../src/components/features/downloads/liveDownloadPreviews.ts')
);
// The two date helpers load the i18n setup, which reads JSON a data URL cannot import; only the
// time-range formatter beside the mapper uses them.
const retroGrouping = await import(
  await compileTree('../src/components/features/downloads/retroGrouping.ts', {
    '@utils/dateTimeFormat': moduleUrl('export const formatTimestamp = () => "";'),
    '@utils/formatters': moduleUrl('export const isFromDifferentYear = () => false;')
  })
);

const groupFor = (locale, row) =>
  bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useCallback', 'downloads.tab.groups.steamApp'), {
    t: translator(locale),
    getGameDisplayName,
    getServiceDisplayName: (service) => service,
    getServiceFilterKey: (service) => service
  })(row, []);

/** A merged Steam game row in the shape the endpoint sends. */
const steamRow = (overrides) => ({
  id: 'game-appid-620',
  service: 'steam',
  appName: 'Unknown/Other',
  steamAppId: 620,
  hasRealGameName: false,
  groupType: 'game',
  downloadIds: [11],
  clientIps: ['192.168.1.10'],
  totalBytes: 4096,
  cacheHitBytes: 3072,
  cacheMissBytes: 1024,
  requestCount: 2,
  startTimeUtc: '2026-08-30T10:00:00Z',
  lastStartTimeUtc: '2026-08-30T11:00:00Z',
  isEvicted: false,
  isPartiallyEvicted: false,
  ...overrides
});

test('a Steam group with an app id and no resolved title is named in the local language', () => {
  assert.equal(groupFor('en', steamRow({})).name, 'Steam App 620');
  assert.equal(groupFor('zh', steamRow({})).name, 'Steam 应用 620');
});

// The one that matters: a game actually titled like the placeholder keeps its own title. The row
// says a member resolved that name, so nothing about the text itself is inspected.
test('a resolved game whose title reads like the placeholder keeps that title', () => {
  assert.equal(
    groupFor('zh', steamRow({ appName: 'Steam App 620', hasRealGameName: true })).name,
    'Steam App 620'
  );
});

/** A retro row in the shape the endpoint sends, ungrouped unless the overrides say otherwise. */
const retroWireRow = (overrides) => ({
  id: 'depot-621-192.168.1.10',
  service: 'steam',
  appName: 'Unknown/Other',
  steamAppId: 620,
  hasRealGameName: false,
  epicAppId: null,
  depotId: 621,
  clientIp: '192.168.1.10',
  clientIps: ['192.168.1.10'],
  depotIds: [621],
  datasource: 'Default',
  startTimeUtc: '2026-08-30T10:00:00Z',
  endTimeUtc: '2026-08-30T11:00:00Z',
  cacheHitBytes: 3072,
  cacheMissBytes: 1024,
  totalBytes: 4096,
  requestCount: 2,
  averageBytesPerSecond: 100,
  downloadIds: [11],
  isEvicted: false,
  isPartiallyEvicted: false,
  ...overrides
});

const retroView = parseSource(RETRO_VIEW, typescript.ScriptKind.TSX);
const retroRow = parseSource(RETRO_ROW, typescript.ScriptKind.TSX);

/** The shipped expression one retro title is drawn from, run against a mapped row. */
const retroTitle = (sourceFile, label, matches) => (locale, row) =>
  bindLifted(
    `() => (${findSoleNode(sourceFile, label, matches).initializer.getText(sourceFile)})`,
    {
      data: row,
      t: translator(locale),
      getGameDisplayName,
      getServiceDisplayName: (service) => service
    }
  )();

const titleNamed = (sourceFile, name) => (node) =>
  typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === name;

/** Every place a retro row's title is drawn: the measured column text, the title and the image alt. */
const retroTitles = (locale, row) => [
  retroTitle(
    retroView,
    'measured appName',
    (node) => typescript.isPropertyAssignment(node) && node.name.getText(retroView) === 'appName'
  )(locale, row),
  retroTitle(retroRow, 'title', titleNamed(retroRow, 'title'))(locale, row),
  retroTitle(retroRow, 'imageTitle', titleNamed(retroRow, 'imageTitle'))(locale, row)
];

test('a retro row for an unnamed Steam game is labeled by its app id in the local language', () => {
  const row = retroGrouping.mapDtoToDepotGroupedData(retroWireRow({}));
  assert.equal(row.hasRealGameName, false);
  assert.deepEqual(retroTitles('zh', row), ['Steam 应用 620', 'Steam 应用 620', 'Steam 应用 620']);
});

test('a retro row for a game titled like the placeholder keeps that title', () => {
  const row = retroGrouping.mapDtoToDepotGroupedData(
    retroWireRow({ appName: 'Steam App 620', hasRealGameName: true })
  );
  assert.deepEqual(retroTitles('zh', row), ['Steam App 620', 'Steam App 620', 'Steam App 620']);
});

test('an ungrouped retro row for a named game keeps its title in Chinese', () => {
  const row = retroGrouping.mapDtoToDepotGroupedData(
    retroWireRow({
      id: 'depot-731-192.168.1.10',
      appName: 'Counter-Strike 2',
      steamAppId: 730,
      depotId: 731,
      depotIds: [731],
      hasRealGameName: true
    })
  );
  assert.equal(row.hasRealGameName, true);
  assert.deepEqual(retroTitles('zh', row), [
    'Counter-Strike 2',
    'Counter-Strike 2',
    'Counter-Strike 2'
  ]);
});
