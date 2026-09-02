import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  findSoleNode,
  liftConstArrow,
  parseSource,
  transpile
} from './transpile-module.mjs';

/**
 * Event badges on a COLLAPSED group in the normal view.
 *
 * The server sends one download per group and the rest are fetched only when the reader opens it,
 * so a collapsed group holds its newest session and nothing else. Counting the badges over the
 * downloads it carries therefore shows that one session's events and silently drops every event
 * tagged on the rest of the group. The row names its whole membership by id, and that is what both
 * the badge count and the fetch behind it read.
 */

const NORMAL_VIEW = 'src/components/features/downloads/NormalView.tsx';

const collectGroupEvents = bindLifted(liftConstArrow(NORMAL_VIEW, 'collectGroupEvents'), {});

/** Event tags per download id, in the shape the associations context answers with. */
const associationsFrom = (eventsById) => (downloadId) => ({ events: eventsById[downloadId] ?? [] });

/** Source text of the first argument of every call to `calleeName` in the normal view. */
const firstArgumentsOf = (calleeName) => {
  const sourceFile = parseSource(NORMAL_VIEW, typescript.ScriptKind.TSX);
  return collectNodes(
    sourceFile,
    (node) =>
      typescript.isCallExpression(node) && node.expression.getText(sourceFile) === calleeName
  ).map((call) => call.arguments[0].getText(sourceFile));
};

const lanParty = { id: 1, name: 'LAN party', colorIndex: 2, autoTagged: false };
const patchNight = { id: 2, name: 'Patch night', colorIndex: 5, autoTagged: true };

test('a collapsed group is badged with the events of every session it names', () => {
  const events = collectGroupEvents(
    [7, 8, 9],
    associationsFrom({ 7: [lanParty], 8: [patchNight], 9: [] })
  );

  assert.deepEqual(
    events.map((event) => event.id),
    [1, 2]
  );
});

test('an event tagged on several sessions of one group is badged once', () => {
  const events = collectGroupEvents([7, 8], associationsFrom({ 7: [lanParty], 8: [lanParty] }));

  assert.equal(events.length, 1);
  assert.equal(events[0].name, 'LAN party');
});

// The card, grid and drawer layouts each badge their own group, and all three have to read the
// membership rather than the single download the collapsed row carries.
test('every group badge is counted over the ids the row names', () => {
  const argumentTexts = firstArgumentsOf('collectGroupEvents');

  assert.equal(argumentTexts.length, 3);
  assert.deepEqual(argumentTexts, ['group.downloadIds', 'group.downloadIds', 'group.downloadIds']);
});

// Without this the badge count above reads an empty cache: the associations context answers with no
// events for any id it was never asked to fetch, so the badges would still come from one session.
test('the event tags are fetched for every session the group names', () => {
  const argumentTexts = firstArgumentsOf('useGroupDownloadAssociations');

  assert.equal(argumentTexts.length, 3);
  assert.deepEqual(argumentTexts, ['group.downloadIds', 'group.downloadIds', 'group.downloadIds']);
});

/**
 * Opening the group fetches the members it names, and the route behind that answers at most 500
 * rows per request. A group larger than that therefore has to be asked for in slices, and the
 * slices arrive sorted only within themselves, so the joined rows are sorted once on the client.
 *
 * The method that ships is lifted out of the service and run here rather than restated, so sending
 * the whole list in one request again, or dropping the sort, fails this file instead of quietly
 * losing the oldest members of a large group.
 */

const apiService = parseSource('src/services/api.service.ts');

const byIdsText = findSoleNode(
  apiService,
  'the getDownloadsByIds method',
  (node) =>
    typescript.isMethodDeclaration(node) && node.name.getText(apiService) === 'getDownloadsByIds'
).getText(apiService);

/**
 * The lifted method, ready to call, with the id list of each request it sent recorded.
 *
 * @param {(ids: number[]) => unknown[]} respond The rows the route answers a slice with.
 * @returns {{ getDownloadsByIds: Function, requests: number[][] }}
 */
const liftGetDownloadsByIds = (respond) => {
  const requests = [];
  const compiled = transpile(
    `const holder = { ${byIdsText.slice('static '.length)} };`,
    typescript.ModuleKind.CommonJS
  );
  const holder = new Function('API_BASE', 'fetch', 'isAbortError', `${compiled}\nreturn holder;`)(
    'http://cache/api',
    async (_url, options) => JSON.parse(options.body),
    () => false
  );
  holder.getJsonFetchOptions = (body) => ({ body: JSON.stringify(body) });
  holder.handleResponse = async (request) => {
    requests.push(request.downloadIds);
    return respond(request.downloadIds);
  };
  return { getDownloadsByIds: (...args) => holder.getDownloadsByIds(...args), requests };
};

/** A member row whose start time is later the newer its id is. */
const member = (id) => ({
  id,
  startTimeUtc: new Date(1_700_000_000_000 + id * 60_000).toISOString()
});

test('a group of fewer than 500 members loads in a single request', async () => {
  const { getDownloadsByIds, requests } = liftGetDownloadsByIds((ids) => ids.map(member));

  const rows = await getDownloadsByIds([9, 8, 7]);

  assert.equal(requests.length, 1, 'the common case is not given a second round trip');
  assert.deepEqual(
    rows.map((row) => row.id),
    [9, 8, 7]
  );
});

test('a group larger than 500 members is asked for in slices and keeps every member', async () => {
  const ids = Array.from({ length: 1201 }, (_unused, index) => 1201 - index);
  // Each request answers its own slice newest first, the way the route sorts within a call.
  const { getDownloadsByIds, requests } = liftGetDownloadsByIds((slice) =>
    slice.map(member).sort((left, right) => right.id - left.id)
  );

  const rows = await getDownloadsByIds(ids);

  assert.deepEqual(
    requests.map((slice) => slice.length),
    [500, 500, 201]
  );
  assert.equal(rows.length, 1201, 'no member is lost past the first slice');
  assert.deepEqual(
    rows.map((row) => row.id),
    ids,
    'the joined rows are newest first across the whole set, not only within a slice'
  );
});

test('members sharing a start time are ordered by id, the way the route breaks the tie', async () => {
  const sameStart = (id) => ({ id, startTimeUtc: '2026-01-01T00:00:00Z' });
  const { getDownloadsByIds } = liftGetDownloadsByIds((ids) => ids.map(sameStart));

  const rows = await getDownloadsByIds([4, 6, 5]);

  assert.deepEqual(
    rows.map((row) => row.id),
    [6, 5, 4]
  );
});
