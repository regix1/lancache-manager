import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  collectNodes,
  findSoleNode,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * The game picker filtered cached games out of the whole list BEFORE splitting it into the Selected
 * and Available groups, so a game that was both selected and cached rendered in neither group while
 * still being counted and re-submitted on save. It also seeded its selection straight from the
 * parent, so an app id the library no longer lists inflated the count forever.
 *
 * These drive the memos that ship: the search filter, the two group partitions, the intersection
 * against the library, and the two header numbers. Save is driven too, because that intersection
 * is only meaningful while there is a library: a games route answering with nothing would otherwise
 * make every pick look like an orphan and post an empty selection over the top of it.
 */

const modalPath = 'src/components/features/prefill/GameSelectionModal.tsx';
const modalFile = parseSource(modalPath, ts.ScriptKind.TSX);

/** Source text of the arrow in `const <name> = useMemo(() => ..., [deps])`. */
const liftMemo = (name) => {
  const declaration = findSoleNode(
    modalFile,
    `${name} useMemo declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(modalFile) === name &&
      node.initializer !== undefined &&
      ts.isCallExpression(node.initializer) &&
      node.initializer.expression.getText(modalFile) === 'useMemo'
  );
  return declaration.initializer.arguments[0].getText(modalFile);
};

/** Runs the named memo's factory with its free variables supplied by name. */
const runMemo = (name, bindings) => bindLifted(liftMemo(name), bindings)();

const library = [
  { appId: '1', name: 'Alpha' },
  { appId: '2', name: 'Beta' },
  { appId: '3', name: 'Gamma' }
];

const appIdsOf = (games) => games.map((game) => game.appId);

test('a selected game that is cached keeps its row while cached games are hidden', () => {
  const localSelected = new Set(['1']);
  const cachedAppIdsSet = new Set(['1']);
  const hideCached = true;

  const filteredGames = runMemo('filteredGames', {
    games: library,
    search: '',
    hideCached,
    cachedAppIdsSet
  });
  assert.deepEqual(
    appIdsOf(filteredGames),
    ['1', '2', '3'],
    'the search decides this list on its own; dropping cached games here strands a selected one'
  );

  const selectedGames = runMemo('selectedGames', { sortedGames: filteredGames, localSelected });
  const availableGames = runMemo('availableGames', {
    sortedGames: filteredGames,
    localSelected,
    hideCached,
    cachedAppIdsSet
  });

  assert.deepEqual(appIdsOf(selectedGames), ['1'], 'the selected cached game must still be listed');
  assert.deepEqual(appIdsOf(availableGames), ['2', '3']);
});

test('hiding cached games still hides one the user has not picked', () => {
  const availableGames = runMemo('availableGames', {
    sortedGames: library,
    localSelected: new Set(),
    hideCached: true,
    cachedAppIdsSet: new Set(['2'])
  });

  assert.deepEqual(appIdsOf(availableGames), ['1', '3']);
});

test('Select All takes the rows on screen, not the cached ones being hidden', () => {
  const localSelected = new Set();
  const bindings = {
    sortedGames: library,
    localSelected,
    hideCached: true,
    cachedAppIdsSet: new Set(['2'])
  };

  let replacedWith = null;
  const selectAll = bindLifted(
    liftHookCallback(modalPath, 'useCallback', '[...selectedGames, ...availableGames]'),
    {
      setLocalSelected: (next) => {
        replacedWith = next;
      },
      selectedGames: runMemo('selectedGames', bindings),
      availableGames: runMemo('availableGames', bindings)
    }
  );
  selectAll();

  assert.deepEqual([...replacedWith], ['1', '3'], 'the hidden cached game must not be selected');
});

test('an app id the library no longer lists is not counted and is not saved', async () => {
  const gameIdSet = new Set(['1', '2']);

  assert.deepEqual(
    runMemo('selectedInLibrary', { localSelected: new Set(['9']), gameIdSet }),
    [],
    'an id absent from the library counts for nothing'
  );

  let saved = null;
  const handleSave = bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'onSave(games.length > 0'),
    {
      setIsSaving: () => undefined,
      onSave: (appIds) => {
        saved = appIds;
        return Promise.resolve();
      },
      games: library,
      localSelected: new Set(['9', '1']),
      selectedInLibrary: runMemo('selectedInLibrary', {
        localSelected: new Set(['9', '1']),
        gameIdSet
      }),
      onClose: () => undefined,
      notifyError: () => undefined,
      t: (key) => key
    }
  );
  await handleSave();

  assert.deepEqual(saved, ['1'], 'save must not hand back an id the library does not list');
});

test('a library that came back empty does not turn the whole selection into orphans', async () => {
  const localSelected = new Set(['1', '2']);

  let saved = null;
  const handleSave = bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'onSave(games.length > 0'),
    {
      setIsSaving: () => undefined,
      onSave: (appIds) => {
        saved = appIds;
        return Promise.resolve();
      },
      games: [],
      localSelected,
      selectedInLibrary: runMemo('selectedInLibrary', { localSelected, gameIdSet: new Set() }),
      onClose: () => undefined,
      notifyError: () => undefined,
      t: (key) => key
    }
  );
  await handleSave();

  assert.deepEqual(saved, ['1', '2'], 'an empty library is unknown, not proof every pick is gone');
});

test('the header splits the selection into what will download and what is already cached', () => {
  const selectedInLibrary = runMemo('selectedInLibrary', {
    localSelected: new Set(['1', '2', '3', '4']),
    gameIdSet: new Set(['1', '2', '3', '4'])
  });
  const cachedSelectedCount = runMemo('cachedSelectedCount', {
    selectedInLibrary,
    cachedAppIdsSet: new Set(['3'])
  });
  const willDownload = selectedInLibrary.length - cachedSelectedCount;

  assert.equal(cachedSelectedCount, 1);
  assert.equal(willDownload, 3);
  assert.equal(
    willDownload + cachedSelectedCount,
    selectedInLibrary.length,
    'the two header numbers have to sum to the number on the Selected badge'
  );
});

test('the Selected badge carries the selection total and nothing else prints it', () => {
  const countBadges = collectNodes(
    modalFile,
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(modalFile) === 'Badge' &&
      node.openingElement.attributes.properties.some(
        (attribute) =>
          ts.isJsxAttribute(attribute) &&
          attribute.name.getText(modalFile) === 'className' &&
          attribute.initializer !== undefined &&
          ts.isStringLiteral(attribute.initializer) &&
          attribute.initializer.text.split(/\s+/).includes('badge-count')
      )
  );
  const badgeContents = countBadges.map((badge) =>
    badge.children
      .map((child) => child.getText(modalFile).trim())
      .filter((text) => text.length > 0)
      .join('')
  );

  assert.deepEqual(badgeContents, ['{selectedInLibrary.length}', '{availableGames.length}']);
  assert.equal(
    modalFile.text.includes('localSelected.size'),
    false,
    'the raw selection size is no longer a displayed number anywhere in the modal'
  );
});
