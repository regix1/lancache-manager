import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  entityBusyFor,
  findSoleNode,
  liftHookCallback,
  loadNotificationModules,
  parseSource
} from './transpile-module.mjs';

/**
 * useCacheRemovalActive() gates every Remove button in the game-cache domain
 * (Game Cache Detector, Evicted Items). It must stay true while a cache-domain
 * removal (single, evicted, or a cache bulk run) is active, and false while an
 * unrelated batch (log removal) runs - a log batch's bulk_removal card must not
 * grey out buttons that have nothing to do with it. No component render is
 * needed: the hook is a pure `useMemo` over the server's `runs` (single removals) and the
 * drawn `notifications` (the browser's own bulk cards), so `useMemo` and `useNotifications` are
 * stubbed to plain functions and the hook is called
 * directly, the same way the other hook tests in this directory do it.
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

// Runs the factory on every call and ignores the dependency array, so these tests
// check what the hook computes, never whether the memo is keyed on the right values.
const reactStubUrl = moduleUrl(`export const useMemo = (fn) => fn();`);

const notificationsStubUrl = moduleUrl(`
  export const box = { notifications: [], runs: [] };
  export const useNotifications = () => ({ notifications: box.notifications, runs: box.runs });
`);

/** Loads the real hook, aliasing `react` and `useNotifications` to the stubs above. */
const loadUseCacheRemovalActive = async () => {
  const hookUrl = await compileToUrl('../src/hooks/useCacheRemovalActive.ts', {
    react: reactStubUrl,
    '@contexts/notifications/useNotifications': notificationsStubUrl
  });
  const { useCacheRemovalActive } = await import(hookUrl);
  return useCacheRemovalActive;
};

const activeFor = async (notifications, runs = []) => {
  const { box } = await import(notificationsStubUrl);
  box.notifications = notifications;
  box.runs = runs;
  const useCacheRemovalActive = await loadUseCacheRemovalActive();
  return useCacheRemovalActive();
};

test('no notifications: the gate is off', async () => {
  assert.equal(await activeFor([]), false);
});

test('a running log batch does not gate the game-cache controls', async () => {
  const active = await activeFor([
    { id: 'x', type: 'bulk_removal', status: 'running', details: { itemTypes: ['log_removal'] } }
  ]);
  assert.equal(active, false, 'a log batch has nothing to do with cache removals');
});

test('a running cache batch (game and service items) gates the controls', async () => {
  const active = await activeFor([
    {
      id: 'x',
      type: 'bulk_removal',
      status: 'running',
      details: { itemTypes: ['game_removal', 'service_removal'] }
    }
  ]);
  assert.equal(active, true, 'the cache batch owns game and service removal events');
});

test('a running evicted batch gates the controls', async () => {
  const active = await activeFor([
    {
      id: 'x',
      type: 'bulk_removal',
      status: 'running',
      details: { itemTypes: ['eviction_removal'] }
    }
  ]);
  assert.equal(active, true);
});

test('a single running, queued or cancelling removal still gates the controls', async () => {
  for (const type of ['game_removal', 'service_removal', 'eviction_removal']) {
    for (const status of ['running', 'waiting', 'cancelling']) {
      const active = await activeFor([], [{ id: 'x', type, status, details: {} }]);
      assert.equal(active, true, `${type} ${status} should gate`);
    }
  }
});

test('a Hidden removal gates the controls though it draws no card', async () => {
  const active = await activeFor(
    [],
    [{ id: 'x', type: 'game_removal', status: 'running', details: {} }]
  );
  assert.equal(active, true);
});

const steamRun = (status) => ({
  id: 'r',
  type: 'game_removal',
  status,
  details: { gameAppId: 570, gameName: 'Dota 2' }
});

test('an entity stays busy while its removal runs or unwinds, and not while it waits', async () => {
  const dota = { kind: 'steamGame', gameAppId: 570 };
  assert.equal(await entityBusyFor(dota, [steamRun('running')]), true);
  assert.equal(await entityBusyFor(dota, [steamRun('cancelling')]), true);
  // A waiting removal's record holds only the queue's park state: it names no game yet.
  assert.equal(await entityBusyFor(dota, [steamRun('waiting')]), false);
  assert.equal(
    await entityBusyFor({ kind: 'steamGame', gameAppId: 440 }, [steamRun('running')]),
    false
  );
});

const removal = (details, type = 'game_removal') => ({
  id: 'r',
  type,
  status: 'running',
  details
});

test('removing a game marks only that game busy, never a same-named game on another service', async () => {
  const diabloBlizzard = { kind: 'namedGame', service: 'blizzard', gameName: 'Diablo IV' };
  const diabloXbox = { kind: 'namedGame', service: 'xboxlive', gameName: 'Diablo IV' };
  const blizzardRemoval = removal({ gameName: 'Diablo IV', service: 'blizzard' });
  assert.equal(await entityBusyFor(diabloBlizzard, [blizzardRemoval]), true);
  assert.equal(await entityBusyFor(diabloXbox, [blizzardRemoval]), false);
  assert.equal(
    await entityBusyFor({ kind: 'service', service: 'blizzard' }, [blizzardRemoval]),
    false,
    'removing one game does not mark its service card busy'
  );
  assert.equal(
    await entityBusyFor(diabloBlizzard, [removal({ service: 'blizzard' }, 'service_removal')]),
    false,
    'a service removal of the same service is not this game'
  );

  // A Steam removal names no service; a named game of the same name stays idle.
  const steamRemoval = removal({ gameAppId: 2344520, gameName: 'Diablo IV' });
  assert.equal(await entityBusyFor(diabloBlizzard, [steamRemoval]), false);
  assert.equal(await entityBusyFor(diabloXbox, [steamRemoval]), false);

  // An Epic game without an Epic id is matched by name only against an Epic run.
  const fortnite = { kind: 'epicGame', gameName: 'Fortnite' };
  const xboxFortnite = removal({ gameName: 'Fortnite', service: 'xboxlive' });
  assert.equal(await entityBusyFor(fortnite, [xboxFortnite]), false);
  assert.equal(
    await entityBusyFor({ ...fortnite, epicAppId: 'abc' }, [xboxFortnite]),
    false,
    'a run with no Epic id falls back to the name, and it names another service'
  );
  const epicFortnite = removal({ gameName: 'Fortnite', service: 'epicgames' });
  assert.equal(await entityBusyFor(fortnite, [epicFortnite]), true);
  assert.equal(
    await entityBusyFor({ kind: 'namedGame', service: 'xboxlive', gameName: 'Fortnite' }, [
      epicFortnite
    ]),
    false
  );
});

test('a service stays busy while its removal runs, from the service name the run carries', async () => {
  const steam = { kind: 'service', service: 'steam' };
  const run = (status, service) => ({
    id: 'r',
    type: 'service_removal',
    status,
    details: { operationId: 'op', service }
  });
  assert.equal(await entityBusyFor(steam, [run('running', 'steam')]), true);
  assert.equal(await entityBusyFor(steam, [run('cancelling', 'steam')]), true);
  assert.equal(await entityBusyFor(steam, [run('running', 'epicgames')]), false);
});

const { NOTIFICATION_REGISTRY: notificationEntries } = await loadNotificationModules();
const entryFor = (type) => notificationEntries.find((entry) => entry.type === type);

test('a game removal event puts its service on the card only when it names one', () => {
  const entry = entryFor('game_removal');
  const event = {
    operationId: 'op',
    gameAppId: null,
    epicAppId: null,
    gameName: 'Diablo IV',
    stageKey: 'signalr.namedRemove.starting',
    timestamp: '2026-09-22T10:00:00Z',
    percentComplete: 10
  };
  assert.equal(entry.started.getDetails({ ...event, service: 'blizzard' }).service, 'blizzard');
  assert.equal(entry.progress.getDetails({ ...event, service: 'blizzard' }).service, 'blizzard');
  // The Steam route sends a null service.
  for (const getDetails of [entry.started.getDetails, entry.progress.getDetails])
    assert.equal('service' in getDetails({ ...event, gameAppId: 570, service: null }), false);
});

test('removing a game evicted data marks only that game busy; a whole service marks its card', async () => {
  const started = (event) => ({
    id: 'r',
    type: 'eviction_removal',
    status: 'running',
    details: entryFor('eviction_removal').started.getDetails({ operationId: 'op', ...event })
  });

  const named = started({ context: { scope: 'Named', key: 'blizzard' }, gameName: 'Diablo IV' });
  assert.equal(named.details.service, 'blizzard');
  for (const [identifier, busy] of [
    [{ kind: 'namedGame', service: 'blizzard', gameName: 'Diablo IV' }, true],
    [{ kind: 'namedGame', service: 'xboxlive', gameName: 'Diablo IV' }, false],
    [{ kind: 'service', service: 'blizzard' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, [named]), busy, JSON.stringify(identifier));

  const epic = started({
    context: { scope: 'Epic', key: 'abc' },
    epicAppId: 'abc',
    gameName: 'Fortnite'
  });
  assert.equal(epic.details.service, 'epicgames');
  for (const [identifier, busy] of [
    [{ kind: 'epicGame', epicAppId: 'abc' }, true],
    [{ kind: 'epicGame', gameName: 'Fortnite' }, true],
    [{ kind: 'namedGame', service: 'xboxlive', gameName: 'Fortnite' }, false],
    [{ kind: 'service', service: 'epicgames' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, [epic]), busy, JSON.stringify(identifier));

  // The server writes the missing game name of a whole-service removal as null.
  const service = started({ context: { scope: 'Service', key: 'blizzard' }, gameName: null });
  for (const [identifier, busy] of [
    [{ kind: 'service', service: 'blizzard' }, true],
    [{ kind: 'namedGame', service: 'blizzard', gameName: 'Diablo IV' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, [service]), busy, JSON.stringify(identifier));
});

test('the log row that shows a spinner is the service the running log removal names', () => {
  const source = parseSource(
    'src/components/features/management/log-processing/LogRemovalManager.tsx',
    ts.ScriptKind.TSX
  );
  const initializer = (name) =>
    findSoleNode(
      source,
      `${name} declaration`,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
    ).initializer.getText(source);
  const activeRow = (runs) => {
    const activeLogRemovalNotification = bindLifted(
      `() => (${initializer('activeLogRemovalNotification')})`,
      { runs }
    )();
    return bindLifted(`() => (${initializer('activeLogRemoval')})`, {
      activeLogRemovalNotification
    })();
  };
  const run = (status) => ({
    id: 'r',
    type: 'log_removal',
    status,
    details: { operationId: 'op', service: 'steam' }
  });

  assert.equal(activeRow([run('running')]), 'steam');
  assert.equal(activeRow([run('waiting')]), null, 'a queued removal has not started on a row');
  assert.equal(activeRow([]), null);
});

test('a scan history refresh that fails keeps the rows it already showed', async () => {
  const entryWrites = [];
  const listErrors = [];
  const logged = [];
  let answer = async () => [{ scanId: 'scan-1' }];
  const loadHistory = bindLifted(
    liftHookCallback(
      'src/components/features/management/cache/CorruptionScanHistory.tsx',
      'useCallback',
      'getCorruptionScanHistory'
    ),
    {
      mockMode: false,
      listRequestSeqRef: { current: 0 },
      ApiService: { getCorruptionScanHistory: () => answer() },
      validateCorruptionScanHistory: (response) => (Array.isArray(response) ? response : null),
      setEntries: (value) => entryWrites.push(value),
      setListLoading: (value) => logged.push(['loading', value]),
      setListError: (value) => listErrors.push(value),
      notifyError: (message) => logged.push(['logged', message]),
      t: (key) => key,
      getErrorMessage: (error) => error.message
    }
  );

  await loadHistory();
  answer = async () => {
    throw new Error('history read failed');
  };
  await loadHistory();
  answer = async () => ({ entries: 'not a list' });
  await loadHistory();

  assert.deepEqual(entryWrites, [[{ scanId: 'scan-1' }]], 'neither failure clears the rows');
  assert.deepEqual(listErrors, [
    null,
    null,
    'history read failed',
    null,
    'common.errors.invalidJsonResponse'
  ]);
});

test('a hidden load box leaves no wrapper behind in its section body', () => {
  // The shared section body hides itself only when it renders no node at all, so the load box
  // must sit in the body directly rather than inside a spacing wrapper that stays mounted.
  for (const [file, titleKey] of [
    [
      'src/components/features/management/cache/CorruptionScanHistory.tsx',
      'management.corruption.history.loadError'
    ],
    [
      'src/components/features/management/log-processing/LogRemovalManager.tsx',
      'management.logRemoval.errors.loadFailed'
    ]
  ]) {
    const source = parseSource(file, ts.ScriptKind.TSX);
    const box = findSoleNode(
      source,
      `${titleKey} ErrorBlock`,
      (node) =>
        ts.isJsxSelfClosingElement(node) &&
        node.tagName.getText(source) === 'ErrorBlock' &&
        node.getText(source).includes(titleKey)
    );
    let parent = box.parent;
    while (!ts.isJsxElement(parent)) parent = parent.parent;
    assert.equal(parent.openingElement.tagName.getText(source), 'AccordionSection', file);
  }
});

/** The children of the AccordionSection whose title uses `titleKey`, as one lifted fragment. */
const sectionBody = (file, titleKey) => {
  const source = parseSource(file, ts.ScriptKind.TSX);
  const section = findSoleNode(
    source,
    `${titleKey} section`,
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(source) === 'AccordionSection' &&
      node.openingElement.getText(source).includes(`title={t('${titleKey}')}`)
  );
  return `() => (<>${section.children.map((child) => child.getText(source)).join('')}</>)`;
};

// The load box renders nothing while the connection banner is up; the shared section body
// hides itself only when its children render no node at all.
const OUTAGE_ERROR_BLOCK = function ErrorBlock() {
  return null;
};

test('an evicted items load that failed during an outage leaves the section body empty', () => {
  const body = bindLifted(
    sectionBody(
      'src/components/features/management/sections/StorageSection.tsx',
      'management.sections.data.evictedItemsHeading'
    ),
    {
      React,
      ErrorBlock: OUTAGE_ERROR_BLOCK,
      EvictedItemsList: function EvictedItemsList() {
        return React.createElement('ul');
      },
      t: (key) => key,
      evictedItemsError: 'reason: server down',
      evictedGames: [],
      evictedServices: [],
      fetchEvictedItems: async () => undefined
    },
    { jsx: ts.JsxEmit.React }
  );
  assert.equal(renderToStaticMarkup(body()), '');
});

test('a game detection load that failed during an outage leaves the section body empty', () => {
  const file = 'src/components/features/management/game-detection/GameCacheDetector.tsx';
  const source = parseSource(file, ts.ScriptKind.TSX);
  const state = {
    showBlockingLoader: false,
    cacheExist: true,
    datasources: [{ name: 'default' }],
    hasResults: false,
    unmappedServices: null,
    loading: false,
    loadError: 'reason: server down',
    lastDetectionTime: null,
    selectedDatasource: null,
    filteredGames: [],
    filteredServices: []
  };
  const hasBodyContent = bindLifted(
    `() => (${findSoleNode(
      source,
      'hasBodyContent declaration',
      (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === 'hasBodyContent'
    ).initializer.getText(source)})`,
    state
  )();
  const body = bindLifted(
    sectionBody(file, 'management.gameDetection.title'),
    {
      ...state,
      hasBodyContent,
      React,
      ErrorBlock: OUTAGE_ERROR_BLOCK,
      t: (key) => key,
      setInitialLoadRetry: () => undefined
    },
    { jsx: ts.JsxEmit.React }
  );
  assert.equal(renderToStaticMarkup(body()), '');
});

test('a saved scan says it is view only once, in its notice, with no badge beside it', () => {
  const source = parseSource(
    'src/components/features/management/cache/CorruptionScanHistory.tsx',
    ts.ScriptKind.TSX
  ).getFullText();
  assert.ok(source.includes('management.corruption.history.viewOnlyNotice'));
  assert.equal(source.includes('management.corruption.history.viewOnlyBadge'), false);
});
