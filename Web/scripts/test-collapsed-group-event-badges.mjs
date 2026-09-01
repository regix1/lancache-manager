import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import { bindLifted, collectNodes, liftConstArrow, parseSource } from './transpile-module.mjs';

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
