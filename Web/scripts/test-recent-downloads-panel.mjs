import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback } from './transpile-module.mjs';

/**
 * Four claims the Recent Downloads panel makes, driven through the code that ships.
 *
 * The panel builds no groups any more: the server folds them and sends them in order, and the
 * panel draws that order. What is left in the panel is the narrowing a dropdown does before the
 * server answers, and the fold that collapses the service dropdown's own entries. Both live
 * inside the component and are never exported, so each is lifted out of the file by shape and
 * called with its free variables supplied by name. Copying the logic into this file would only
 * test the copy.
 *
 * 1. Both dropdowns narrow the groups already on screen.
 * 2. The service dropdown folds xbox, xboxlive and microsoft to one entry while wsus keeps its own.
 * 3. microsoft resolves to the xbox brand color, because a merged Xbox group takes its service
 *    from whichever member the server folded first and that member can be microsoft.
 * 4. No group-building code is left in the panel.
 */

const PANEL = 'src/components/features/dashboard/RecentDownloadsPanel.tsx';

const { getServiceFilterKey } = await import(
  await compileToUrl('../src/utils/serviceDisplayName.ts')
);
const { getServiceBadgeStyles, getServiceColorClass, getServiceColorVar, UNKNOWN_COLOR_VAR } =
  await import(await compileToUrl('../src/utils/serviceColors.ts'));

// Anchored on the shape of the memo rather than on either half of the predicate, so a wrong
// predicate fails an assertion here instead of failing to be found at all.
const visibleGroupsSource = liftHookCallback(PANEL, 'useMemo', 'groups.filter((group) => {');

const serviceFilterOptionsSource = liftHookCallback(
  PANEL,
  'useMemo',
  'const representatives = new Map<string, string>()'
);

/**
 * The panel's narrowing predicate, run over the server's groups with the selection a user made.
 *
 * @param {object[]} groups The groups the panel currently holds.
 * @param {{ service?: string, client?: string, clientGroup?: { memberIps: string[] } | null }} selection
 * @returns {object[]} The groups that selection leaves on screen.
 */
const narrowGroups = (groups, selection) =>
  bindLifted(visibleGroupsSource, {
    groups,
    selectedService: selection.service ?? 'all',
    selectedClient: selection.client ?? 'all',
    selectedClientGroup: selection.clientGroup ?? null,
    getServiceFilterKey
  })();

/**
 * The panel's service dropdown entries, built from the raw service names the server reports.
 *
 * @param {string[]} services Raw service names, as the batch sends them.
 * @returns {{ key: string, service: string }[]} One entry per folded name.
 */
const dropdownServices = (services) =>
  bindLifted(serviceFilterOptionsSource, {
    serviceOptions: services.map((service) => ({ service })),
    getServiceFilterKey
  })();

let nextId = 1;

/**
 * One group as the server sends it: the folded service, the distinct clients that downloaded it
 * and every member id.
 *
 * @param {{ service: string, clientIps?: string[] }} spec
 * @returns {object} The group shape the panel reads.
 */
const group = (spec) => ({
  id: `service-${spec.service}`,
  name: spec.service,
  type: 'content',
  service: spec.service,
  totalBytes: 1000,
  cacheHitBytes: 250,
  cacheMissBytes: 750,
  count: 1,
  lastSeen: '2026-08-30T10:00:00Z',
  isEvicted: false,
  isPartiallyEvicted: false,
  hasRealGameName: false,
  clientIps: spec.clientIps ?? ['10.0.0.7'],
  downloadIds: [nextId++]
});

test('a new service selection drops the groups it excludes before the server answers', () => {
  const groups = [group({ service: 'steam' }), group({ service: 'xbox' })];

  assert.deepEqual(
    narrowGroups(groups, { service: 'steam' }).map((row) => row.service),
    ['steam']
  );
  assert.deepEqual(
    narrowGroups(groups, {}).map((row) => row.service),
    ['steam', 'xbox']
  );
});

test('a new client selection drops the groups it excludes before the server answers', () => {
  const groups = [
    group({ service: 'steam', clientIps: ['10.0.0.7'] }),
    group({ service: 'epicgames', clientIps: ['10.0.0.9', '10.0.0.11'] })
  ];

  assert.deepEqual(
    narrowGroups(groups, { client: '10.0.0.9' }).map((row) => row.service),
    ['epicgames']
  );
  assert.deepEqual(
    narrowGroups(groups, {
      client: 'group-lab',
      clientGroup: { memberIps: ['10.0.0.7'] }
    }).map((row) => row.service),
    ['steam']
  );
});

test('a group is kept when any one of its clients is the selected one', () => {
  const shared = [group({ service: 'steam', clientIps: ['10.0.0.7', '10.0.0.9'] })];

  assert.equal(narrowGroups(shared, { client: '10.0.0.9' }).length, 1);
  assert.equal(narrowGroups(shared, { client: '10.0.0.12' }).length, 0);
});

test('the predicate removes nothing from groups the server already narrowed', () => {
  const served = [group({ service: 'xbox' }), group({ service: 'steam' })];

  const visible = narrowGroups(served, { client: '10.0.0.7' });
  assert.equal(visible.length, served.length);
  served.forEach((row, index) => assert.equal(visible[index], row));
});

test('xbox, xboxlive and microsoft share one dropdown entry and wsus keeps its own', () => {
  const options = dropdownServices(['xbox', 'xboxlive', 'microsoft', 'wsus']);

  assert.deepEqual(
    options.map((option) => option.key),
    ['wsus', 'xbox']
  );
  // The entry keeps the first raw name it folded, which is what the label is drawn from.
  assert.equal(options.find((option) => option.key === 'xbox').service, 'xbox');
});

test('a merged Xbox row led by microsoft draws the Xbox badge, not the generic one', () => {
  // The server folds the three aliases into one group and that group carries whichever raw name
  // it folded first, so the badge in that row can be looked up by the name microsoft.
  const badge = getServiceBadgeStyles('microsoft');
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

test('the panel builds no groups of its own', async () => {
  const source = await readFile(new URL(`../${PANEL}`, import.meta.url), 'utf8');

  assert.equal(source.includes('createGroups'), false, 'the panel must not fold rows into groups');
  assert.equal(
    source.includes('groupedItems'),
    false,
    'the grouped-items memo is the server’s job now'
  );
  assert.equal(
    source.includes('isResolvedGameName(d.gameName'),
    false,
    'the group-key rules belong to the server'
  );
});
