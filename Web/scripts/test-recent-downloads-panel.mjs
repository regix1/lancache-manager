import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback } from './transpile-module.mjs';

/**
 * Three claims the Recent Downloads panel makes, driven through the code that ships.
 *
 * The panel's filter predicate, its grouper and its grouped-items memo all live inside the
 * component and are never exported, so each one is lifted out of the file by shape and called
 * with its free variables supplied by name. Copying the logic into this file would only test
 * the copy.
 *
 * 1. Both dropdowns narrow the rows already on screen. The server narrows them too, but only
 *    after a round trip, and until the answer lands the previous selection's rows stay visible.
 * 2. Service groups key on the folded name, so xbox, xboxlive and microsoft share one row while
 *    wsus keeps its own.
 * 3. microsoft resolves to the xbox brand color, because a merged Xbox group takes its service
 *    from whichever member came first and that member can be microsoft.
 */

const PANEL = 'src/components/features/dashboard/RecentDownloadsPanel.tsx';

const { getServiceFilterKey, formatServiceLabel } = await import(
  await compileToUrl('../src/utils/serviceDisplayName.ts')
);
const { isResolvedGameName } = await import(
  await compileToUrl('../src/components/features/downloads/liveDownloadPreviews.ts')
);
const { getServiceBadgeStyles, getServiceColorClass, getServiceColorVar, UNKNOWN_COLOR_VAR } =
  await import(await compileToUrl('../src/utils/serviceColors.ts'));

// The panel reads its group label out of i18n. This stub hands back the interpolated service
// value alone, so an assertion on a group name is an assertion on what formatServiceLabel folded
// the raw name to.
const translateServiceGroup = (key, values) => values.service;

const createGroups = bindLifted(
  liftHookCallback(PANEL, 'useCallback', 'const groups: Record<string, DownloadGroup> = {}'),
  { isResolvedGameName, getServiceFilterKey, formatServiceLabel, t: translateServiceGroup }
);

// Anchored on the shape of the memo rather than on either half of the predicate, so a wrong
// predicate fails an assertion here instead of failing to be found at all.
const visibleDownloadsSource = liftHookCallback(
  PANEL,
  'useMemo',
  'downloads.filter((download) => {'
);

const groupedItemsSource = liftHookCallback(PANEL, 'useMemo', 'totalGroups: allItems.length');

/**
 * The panel's filter predicate, run over `rows` with the selection a user has made.
 *
 * @param {object[]} rows The downloads the panel currently holds.
 * @param {{ service?: string, client?: string, clientGroup?: { memberIps: string[] } | null }} selection
 * @returns {object[]} The rows that selection leaves on screen.
 */
const filterRows = (rows, selection) =>
  bindLifted(visibleDownloadsSource, {
    downloads: rows,
    selectedService: selection.service ?? 'all',
    selectedClient: selection.client ?? 'all',
    selectedClientGroup: selection.clientGroup ?? null,
    getServiceFilterKey
  })();

/**
 * The panel's grouped-items memo. Both the raw prop and the filtered rows are bound, so which one
 * the memo actually reads is what the assertions measure.
 *
 * @param {object[]} rows The raw downloads prop.
 * @param {object[]} visible The rows the filter predicate left.
 * @returns {{ displayedItems: object[], totalGroups: number }}
 */
const groupOver = (rows, visible) =>
  bindLifted(groupedItemsSource, {
    downloads: rows,
    visibleDownloads: visible,
    createGroups,
    displayCount: 10
  })();

const groupIds = (grouped) => grouped.displayedItems.map((group) => group.id).sort();

let nextId = 1;

/**
 * One recorded download row, with a game name the panel cannot resolve so the row groups by
 * service rather than by title.
 *
 * @param {{ service: string, clientIp?: string, startTimeUtc?: string }} row
 * @returns {object} The download shape the panel's grouper reads.
 */
const download = (row) => ({
  id: nextId++,
  service: row.service,
  clientIp: row.clientIp ?? '10.0.0.7',
  startTimeUtc: row.startTimeUtc ?? '2026-08-30T10:00:00Z',
  gameName: row.service,
  gameAppId: undefined,
  totalBytes: 1000,
  cacheHitBytes: 250,
  cacheMissBytes: 750,
  isEvicted: false
});

test('a new service selection drops the rows it excludes before the server answers', () => {
  const rows = [
    download({ service: 'steam', startTimeUtc: '2026-08-30T10:05:00Z' }),
    download({ service: 'xboxlive' })
  ];

  const visible = filterRows(rows, { service: 'steam' });
  assert.deepEqual(
    visible.map((row) => row.service),
    ['steam']
  );

  assert.deepEqual(groupIds(groupOver(rows, visible)), ['service-steam']);
  assert.deepEqual(groupIds(groupOver(rows, rows)), ['service-steam', 'service-xbox']);
});

test('a new client selection drops the rows it excludes before the server answers', () => {
  const rows = [
    download({ service: 'steam', clientIp: '10.0.0.7', startTimeUtc: '2026-08-30T10:05:00Z' }),
    download({ service: 'epicgames', clientIp: '10.0.0.9' })
  ];

  assert.deepEqual(
    filterRows(rows, { client: '10.0.0.9' }).map((row) => row.clientIp),
    ['10.0.0.9']
  );
  assert.deepEqual(
    filterRows(rows, {
      client: 'group-lab',
      clientGroup: { memberIps: ['10.0.0.7'] }
    }).map((row) => row.clientIp),
    ['10.0.0.7']
  );
});

test('the predicate removes nothing from rows the server already narrowed', () => {
  const served = [
    download({ service: 'xbox', clientIp: '10.0.0.7' }),
    download({ service: 'xboxlive', clientIp: '10.0.0.7' }),
    download({ service: 'microsoft', clientIp: '10.0.0.7' })
  ];

  const visible = filterRows(served, { service: 'xbox', client: '10.0.0.7' });
  assert.equal(visible.length, served.length);
  served.forEach((row, index) => assert.equal(visible[index], row));
});

test('xbox, xboxlive and microsoft share one group and wsus keeps its own', () => {
  const rows = [
    download({ service: 'xbox' }),
    download({ service: 'xboxlive' }),
    download({ service: 'microsoft' }),
    download({ service: 'wsus' })
  ];

  const groups = createGroups(rows);
  assert.equal(groups.length, 2);

  const xbox = groups.find((group) => group.id === 'service-xbox');
  assert.ok(xbox, 'the three Xbox aliases must fold into one group');
  assert.equal(xbox.count, 3);
  assert.deepEqual(
    xbox.downloads.map((row) => row.service),
    ['xbox', 'xboxlive', 'microsoft']
  );
  assert.equal(xbox.name, 'Xbox');

  const wsus = groups.find((group) => group.id === 'service-wsus');
  assert.ok(wsus, 'wsus stays outside the Xbox fold and keeps its own row');
  assert.equal(wsus.count, 1);
  assert.equal(wsus.name, 'Wsus');
});

test('a merged Xbox row led by microsoft draws the Xbox badge, not the generic one', () => {
  // A merged group carries the service of whichever member came first, so the badge in that row
  // is looked up by the raw name microsoft.
  const merged = createGroups([
    download({ service: 'microsoft' }),
    download({ service: 'xbox' })
  ])[0];
  assert.equal(merged.service, 'microsoft');

  const badge = getServiceBadgeStyles(merged.service);
  assert.notEqual(badge.color, `var(${UNKNOWN_COLOR_VAR})`);
  assert.equal(badge.color, 'var(--theme-xbox-text)');
  assert.deepEqual(badge, getServiceBadgeStyles('xbox'));
});

test('microsoft resolves to the same service as xbox and xboxlive', () => {
  assert.equal(getServiceColorClass('microsoft'), 'service-xbox');
  assert.equal(getServiceColorClass('microsoft'), getServiceColorClass('xboxlive'));
  assert.equal(getServiceColorVar('microsoft'), '--theme-xbox');
  assert.equal(getServiceColorVar('microsoft'), getServiceColorVar('xbox'));
});
