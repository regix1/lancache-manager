import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
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
 * These drive the memos that ship: the search filter, the three exclusive group partitions, the intersection
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

const renderGameRowDeclaration = findSoleNode(
  modalFile,
  'renderGameRow declaration',
  (node) =>
    ts.isVariableDeclaration(node) &&
    node.name.getText(modalFile) === 'renderGameRow' &&
    node.initializer !== undefined &&
    ts.isArrowFunction(node.initializer)
);

const renderGameRowSource = renderGameRowDeclaration.initializer.getText(modalFile);

test('selected, cached, and available games form three exclusive groups', () => {
  const localSelected = new Set(['1']);
  const cachedAppIdsSet = new Set(['1', '2']);
  const hideCached = false;

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
  const cachedGames = runMemo('cachedGames', {
    sortedGames: filteredGames,
    localSelected,
    hideCached,
    cachedAppIdsSet
  });
  const availableGames = runMemo('availableGames', {
    sortedGames: filteredGames,
    localSelected,
    cachedAppIdsSet
  });

  assert.deepEqual(appIdsOf(selectedGames), ['1'], 'the selected cached game must still be listed');
  assert.deepEqual(appIdsOf(cachedGames), ['2']);
  assert.deepEqual(appIdsOf(availableGames), ['3']);
  assert.deepEqual(
    [...selectedGames, ...cachedGames, ...availableGames].map((game) => game.appId).sort(),
    ['1', '2', '3'],
    'each visible game belongs to exactly one group'
  );
});

test('hiding cached games empties only the cached group', () => {
  const cachedGames = runMemo('cachedGames', {
    sortedGames: library,
    localSelected: new Set(),
    hideCached: true,
    cachedAppIdsSet: new Set(['2'])
  });
  const availableGames = runMemo('availableGames', {
    sortedGames: library,
    localSelected: new Set(),
    cachedAppIdsSet: new Set(['2'])
  });

  assert.deepEqual(appIdsOf(cachedGames), []);
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
    liftHookCallback(
      modalPath,
      'useCallback',
      '[...selectedGames, ...cachedGames, ...availableGames]'
    ),
    {
      setLocalSelected: (next) => {
        replacedWith = next;
      },
      selectedGames: runMemo('selectedGames', bindings),
      cachedGames: runMemo('cachedGames', bindings),
      availableGames: runMemo('availableGames', bindings)
    }
  );
  selectAll();

  assert.deepEqual([...replacedWith], ['1', '3'], 'the hidden cached game must not be selected');
});

test('search filters selected, cached, and available games from the same source', () => {
  const localSelected = new Set(['1']);
  const cachedAppIdsSet = new Set(['2']);
  const filteredGames = runMemo('filteredGames', { games: library, search: 'beta' });
  const sortedGames = runMemo('sortedGames', { filteredGames, localSelected });

  assert.deepEqual(appIdsOf(runMemo('selectedGames', { sortedGames, localSelected })), []);
  assert.deepEqual(
    appIdsOf(
      runMemo('cachedGames', {
        sortedGames,
        localSelected,
        hideCached: false,
        cachedAppIdsSet
      })
    ),
    ['2']
  );
  assert.deepEqual(
    appIdsOf(runMemo('availableGames', { sortedGames, localSelected, cachedAppIdsSet })),
    []
  );
});

test('Steam import keeps existing selections and reports duplicates and missing games', () => {
  const parseImportText = bindLifted(
    liftHookCallback(modalPath, 'useCallback', "trimmed.startsWith('[')"),
    {}
  );
  let selection = new Set(['1']);
  let result = null;
  let importText = '1, 2, 9';
  const handleImport = bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'const appIds = parseImportText(importText)'),
    {
      importText,
      parseImportText,
      gameIdSet: new Set(['1', '2', '3']),
      setLocalSelected: (update) => {
        selection = update(selection);
      },
      setImportResult: (next) => {
        result = next;
      },
      setImportText: (next) => {
        importText = next;
      }
    }
  );

  handleImport();

  assert.deepEqual([...selection], ['1', '2']);
  assert.deepEqual(result, { added: 1, alreadySelected: 1, notInLibrary: ['9'] });
  assert.equal(importText, '');
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
      openedRef: { current: true },
      openEpochRef: { current: 1 },
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
      openedRef: { current: true },
      openEpochRef: { current: 1 },
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
    cachedAppIdsSet: new Set(['2', '3', '4']),
    outdatedAppIdsSet: new Set(['3']),
    unknownAppIdsSet: new Set(['4'])
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

test('all three pane headers carry their own count', () => {
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

  assert.deepEqual(badgeContents, [
    '{cachedGames.length}',
    '{availableGames.length}',
    '{selectedInLibrary.length}'
  ]);
  assert.equal(
    modalFile.text.includes('localSelected.size'),
    false,
    'the raw selection size is no longer a displayed number anywhere in the modal'
  );
});

test('the wider three-pane layout keeps focus tied to a moved game row', () => {
  let selection = new Set(['1']);
  const pendingFocusAppId = { current: null };
  const toggleGame = bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'pendingFocusAppId.current = appId'),
    {
      pendingFocusAppId,
      setLocalSelected: (update) => {
        selection = update(selection);
      }
    }
  );
  toggleGame('2');

  let focusedAppId = null;
  const focusMovedGame = bindLifted(
    liftHookCallback(modalPath, 'useEffect', "'button[data-game-app-id]'"),
    {
      pendingFocusAppId,
      gameListRef: {
        current: {
          querySelectorAll: () => [
            { dataset: { gameAppId: '1' }, focus: () => (focusedAppId = '1') },
            { dataset: { gameAppId: '2' }, focus: () => (focusedAppId = '2') }
          ]
        }
      }
    }
  );
  focusMovedGame();

  assert.deepEqual([...selection], ['1', '2']);
  assert.equal(focusedAppId, '2');
  assert.equal(pendingFocusAppId.current, null);
  assert.equal(modalFile.text.includes('size="2xl"'), true);
  assert.equal(modalFile.text.includes('data-game-app-id={game.appId}'), true);
});

test('clear and reopen keep their existing ownership paths', () => {
  let selection = new Set(['1', '2']);
  const selectNone = bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'setLocalSelected(new Set())'),
    {
      setLocalSelected: (next) => {
        selection = next;
      }
    }
  );
  selectNone();
  assert.deepEqual([...selection], []);

  const resetSelection = bindLifted(
    liftHookCallback(modalPath, 'useEffect', 'setLocalSelected(new Set(selectedAppIds))'),
    {
      opened: true,
      openedRef: { current: false },
      openEpochRef: { current: 0 },
      setIsSaving: () => undefined,
      selectedAppIds: ['2', '3'],
      setLocalSelected: (next) => {
        selection = next;
      },
      setSearch: () => undefined,
      setImportText: () => undefined,
      setImportResult: () => undefined,
      setClearCacheConfirmOpen: () => undefined
    }
  );
  resetSelection();

  assert.deepEqual([...selection], ['2', '3']);
});

test('the shared game row keeps selection and status without a delete action', () => {
  const Button = () => null;
  const Tooltip = () => null;
  const Badge = () => null;
  const Check = () => null;
  const Trash2 = () => null;
  const cases = [
    {
      game: { appId: 'Opaque/Game-ID', name: 'Cached verified game' },
      selected: false,
      cached: ['opaque/game-id'],
      outdated: [],
      unknown: [],
      badges: ['prefill.gameSelection.cachedBadge']
    },
    {
      game: { appId: 'Opaque/Game-ID', name: 'Selected cached game' },
      selected: true,
      cached: ['OPAQUE/GAME-id'],
      outdated: [],
      unknown: [],
      badges: ['prefill.gameSelection.cachedBadge']
    },
    {
      game: { appId: 'MixedCase-ID', name: 'Cached outdated game' },
      selected: false,
      cached: ['mixedcase-id'],
      outdated: ['MIXEDCASE-ID'],
      unknown: [],
      badges: ['prefill.gameSelection.cachedBadge', 'prefill.gameSelection.updateAvailable']
    },
    {
      game: {
        appId: '9NBLGGH4R315',
        name: 'A long Xbox game name that keeps its unresolved cache status visible'
      },
      selected: false,
      cached: ['9nblggh4r315'],
      outdated: [],
      unknown: ['9NBLGGH4R315'],
      badges: ['prefill.gameSelection.cachedBadge', 'prefill.gameSelection.statusUnknown']
    },
    {
      game: { appId: 'ordinary/id', name: 'Ordinary available game' },
      selected: false,
      cached: [],
      outdated: [],
      unknown: [],
      badges: []
    }
  ];

  for (const fixture of cases) {
    const toggled = [];
    const renderGameRow = bindLifted(
      renderGameRowSource,
      {
        React,
        Button,
        Tooltip,
        Badge,
        Check,
        Trash2,
        cachedAppIdsSet: new Set(fixture.cached.map((id) => id.toLowerCase())),
        outdatedAppIdsSet: new Set(fixture.outdated.map((id) => id.toLowerCase())),
        unknownAppIdsSet: new Set(fixture.unknown.map((id) => id.toLowerCase())),
        toggleGame: (appId) => toggled.push(appId),
        t: (key) => key,
        onRemoveFromCache: () => assert.fail('the picker must not expose row deletion'),
        removingAppId: null
      },
      { jsx: ts.JsxEmit.React }
    );
    const row = renderGameRow(fixture.game, fixture.selected);
    const elements = [];
    const visit = (node) => {
      if (Array.isArray(node)) {
        node.forEach(visit);
        return;
      }
      if (!React.isValidElement(node)) return;
      elements.push(node);
      visit(node.props.children);
    };
    visit(row);

    const rowActions = elements.filter((element) => element.type === Button);
    assert.equal(rowActions.length, 1, `${fixture.game.appId} must have one row selector`);
    assert.equal(rowActions[0].props['data-game-app-id'], fixture.game.appId);
    assert.equal(rowActions[0].props['aria-pressed'], fixture.selected);
    assert.equal(
      elements.some((element) => element.type === Trash2),
      false
    );
    assert.deepEqual(
      elements.filter((element) => element.type === Badge).map((element) => element.props.children),
      fixture.badges
    );

    rowActions[0].props.onClick();
    assert.deepEqual(toggled, [fixture.game.appId]);
  }
});

test('both picker parents omit row deletion and keep clear-all wired', () => {
  const panel = parseSource('src/components/features/prefill/PrefillPanel.tsx', ts.ScriptKind.TSX);
  const scheduled = parseSource(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
    ts.ScriptKind.TSX
  );

  for (const [name, source] of [
    ['PrefillPanel', panel],
    ['ScheduledPrefillConfigModal', scheduled]
  ]) {
    const picker = findSoleNode(
      source,
      `${name} GameSelectionModal`,
      (node) =>
        ts.isJsxSelfClosingElement(node) && node.tagName.getText(source) === 'GameSelectionModal'
    );
    const attributeNames = picker.attributes.properties
      .filter(ts.isJsxAttribute)
      .map((attribute) => attribute.name.getText(source));
    assert.equal(attributeNames.includes('onRemoveFromCache'), false);
    assert.equal(attributeNames.includes('removingAppId'), false);
    assert.equal(attributeNames.includes('onClearAllCache'), true);

    const removedDeclarations = collectNodes(
      source,
      (node) =>
        ts.isVariableDeclaration(node) &&
        [
          'handleRemoveFromCache',
          'handleRemoveGameFromCache',
          'removingAppId',
          'removingCachedAppId'
        ].includes(node.name.getText(source))
    );
    assert.deepEqual(removedDeclarations, []);
  }

  // Clear-all remains admin-only in the ordinary picker because its route is AccountHolder-only.
  assert.equal(panel.text.includes('isAdmin ? handleClearAllFromCache : undefined'), true);
  assert.equal(modalFile.text.includes('onRemoveFromCache'), false);
  assert.equal(modalFile.text.includes('removingAppId'), false);
  assert.equal(modalFile.text.includes('Trash2'), false);
  assert.equal(modalFile.text.includes('prefill.gameSelection.removeFromCache'), false);
});

/**
 * The toolbar button wipes every cached tag, which is the same action the Utilities menu already
 * puts behind a confirmation. Pinned because the button reads as ordinary next to Show Cached and
 * Rescan, so a later edit could easily wire it straight to the callback again.
 */
test('the clear-all button asks before wiping the cached tags', () => {
  assert.equal(modalFile.text.includes('onClick={() => setClearCacheConfirmOpen(true)}'), true);
  assert.equal(modalFile.text.includes('<ConfirmationModal'), true);
  assert.equal(modalFile.text.includes('void onClearAllCache();'), true);
});

/**
 * A clear re-reads the library itself so the badges are right with the socket down, and the
 * PrefillCacheChanged broadcast re-reads it too. Both go through reloadGamesOnce so one click
 * costs one pass; a direct loadGames(true) anywhere else brings the double fetch back.
 */
test('every library reload in the prefill panel goes through the shared pass', () => {
  const panel = parseSource('src/components/features/prefill/PrefillPanel.tsx', ts.ScriptKind.TSX);
  const forcedReloads = panel.text.match(/loadGamesRef\.current\(true\)/g) ?? [];
  assert.equal(
    forcedReloads.length,
    1,
    'loadGames(true) belongs only inside reloadGamesOnce; every other caller awaits that'
  );
  assert.equal(panel.text.includes('handleClearAllFromCache'), true);
  assert.equal(panel.text.includes('handleRemoveFromCache'), false);
  assert.equal(panel.text.includes('void reloadGamesOnce();'), true);
});

test('populated refresh keeps local selection and active filters when parent arrays change', () => {
  let selection = new Set(['1']);
  let search = 'Alpha';
  const openedRef = { current: true };
  const reset = bindLifted(
    liftHookCallback(modalPath, 'useEffect', 'setLocalSelected(new Set(selectedAppIds))'),
    {
      opened: true,
      openedRef,
      openEpochRef: { current: 1 },
      selectedAppIds: ['2'],
      setLocalSelected: (value) => {
        selection = value;
      },
      setSearch: (value) => {
        search = value;
      },
      setImportText: assert.fail,
      setImportResult: assert.fail,
      setClearCacheConfirmOpen: assert.fail
    }
  );
  reset();
  assert.deepEqual([...selection], ['1']);
  assert.equal(search, 'Alpha');
});

test('a save completing after close and reopen cannot close the replacement picker', async () => {
  let release;
  const openEpochRef = { current: 1 };
  let closed = false;
  const save = bindLifted(liftHookCallback(modalPath, 'useCallback', 'await onSave'), {
    openEpochRef,
    openedRef: { current: true },
    setIsSaving: () => undefined,
    games: library,
    selectedInLibrary: ['1'],
    localSelected: new Set(['1']),
    onSave: () =>
      new Promise((resolve) => {
        release = resolve;
      }),
    onClose: () => {
      closed = true;
    },
    notifyError: assert.fail,
    t: (key) => key
  });
  const pending = save();
  openEpochRef.current += 2;
  release();
  await pending;
  assert.equal(closed, false);
});
