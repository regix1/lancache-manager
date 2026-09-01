import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * A grouped row arrives from the server carrying only its newest session; the rest are fetched by
 * DownloadsTab once `expandedItem` names the group. The card grid's drawer never set that, so a
 * twelve-session group listed one session with nothing to say the other eleven existed, and a
 * drawer holding a copy of the group could not have shown them even after they arrived.
 *
 * These checks drive the click handler and the drawer's group lookup out of the file that ships:
 * the click has to ask for the sessions, and the drawer has to read the group back out of the page
 * rows rather than a snapshot taken at click time.
 */

const NORMAL_VIEW = 'src/components/features/downloads/NormalView.tsx';
const normalView = parseSource(NORMAL_VIEW, ts.ScriptKind.TSX);

/** Source text of the value a named `const` is declared with. */
const initializerOf = (name) =>
  findSoleNode(
    normalView,
    `the ${name} declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(normalView) === name &&
      node.initializer !== undefined
  ).initializer.getText(normalView);

/** Source text of the expression a JSX attribute is given. */
const attributeOf = (name) =>
  findSoleNode(
    normalView,
    `the ${name} attribute`,
    (node) => ts.isJsxAttribute(node) && node.name.getText(normalView) === name
  ).initializer.expression.getText(normalView);

const group = (id, sessionCount) => ({
  id,
  name: 'Game',
  count: sessionCount,
  downloads: Array.from({ length: sessionCount }, (unused, index) => ({ id: index }))
});

test('clicking a card asks the page for the sessions the drawer is about to list', () => {
  const asked = [];
  const opened = [];
  bindLifted(initializerOf('handleGridCardClick'), {
    expandedItem: null,
    onItemClick: (id) => asked.push(id),
    setDrawerGroupId: (id) => opened.push(id)
  })('game-620');

  assert.deepEqual(opened, ['game-620'], 'the drawer opens on the group that was clicked');
  assert.deepEqual(
    asked,
    ['game-620'],
    'and its members are fetched, which needs expandedItem set'
  );
});

test('clicking the card of the group already open leaves it open', () => {
  const asked = [];
  const opened = [];
  bindLifted(initializerOf('handleGridCardClick'), {
    expandedItem: 'game-620',
    onItemClick: (id) => asked.push(id),
    setDrawerGroupId: (id) => opened.push(id)
  })('game-620');

  assert.deepEqual(asked, [], 'the click toggles expandedItem, so asking again would collapse it');
  assert.deepEqual(opened, ['game-620'], 'the drawer still opens');
});

test('closing the drawer collapses the group it opened', () => {
  const asked = [];
  const opened = [];
  bindLifted(`() => (${attributeOf('onClose')})`, {
    expandedItem: 'game-620',
    drawerGroupId: 'game-620',
    onItemClick: (id) => asked.push(id),
    setDrawerGroupId: (id) => opened.push(id)
  })()();

  assert.deepEqual(opened, [null], 'the drawer closes');
  assert.deepEqual(asked, ['game-620'], 'and the fetched sessions are dropped with it');
});

test('the drawer reads its group out of the page rows, so fetched sessions reach it', () => {
  const drawerItemFor = (items) =>
    bindLifted(`() => (${initializerOf('drawerItem')})`, {
      items,
      drawerGroupId: 'game-620',
      toGroup: (item) => item,
      useMemo: (factory) => factory()
    })();

  assert.equal(drawerItemFor([group('game-620', 1)]).downloads.length, 1);
  assert.equal(
    drawerItemFor([group('game-620', 12)]).downloads.length,
    12,
    'a group whose members have arrived lists all of them'
  );
  assert.equal(
    drawerItemFor([group('game-730', 3)]),
    null,
    'and a group off the page opens nothing'
  );
});

test('a refetch that drops the open group off the page closes the drawer for good', () => {
  const closeWhenGroupLeaves = (drawerGroupId, drawerItem) => {
    const cleared = [];
    bindLifted(liftHookCallback(NORMAL_VIEW, 'useEffect', 'setDrawerGroupId(null)'), {
      drawerGroupId,
      drawerItem,
      setDrawerGroupId: (value) => cleared.push(value)
    })();
    return cleared;
  };

  assert.deepEqual(
    closeWhenGroupLeaves('game-620', null),
    [null],
    'the group left the page, so the id it would re-open on is dropped'
  );
  assert.deepEqual(
    closeWhenGroupLeaves('game-620', group('game-620', 12)),
    [],
    'a group still on the page keeps its drawer open'
  );
  assert.deepEqual(closeWhenGroupLeaves(null, null), [], 'no drawer open, nothing to close');
});
