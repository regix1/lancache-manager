import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

const { resolveCachedAppIds } = await import(
  await compileToUrl('../src/components/features/prefill/cachedApps.ts')
);

test('verified apps appear and explicit negative apps disappear', () => {
  assert.deepEqual(resolveCachedAppIds(['A', 'B'], ['A'], ['C']), ['A']);
});

test('unknown apps preserve only existing eligible badges', () => {
  assert.deepEqual(resolveCachedAppIds(['A', 'B'], [], ['B', 'C']), ['B']);
});

test('empty manager eligibility clears every badge', () => {
  assert.deepEqual(resolveCachedAppIds(['A'], [], []), []);
});

test('a new session cannot gain badges from unknown responses', () => {
  assert.deepEqual(resolveCachedAppIds([], [], ['A']), []);
});

test('verified and previous unknown memberships are deduplicated', () => {
  assert.deepEqual(resolveCachedAppIds(['A', 'B', 'B'], ['A', 'A'], ['A', 'B']), ['A', 'B']);
});

const panelPath = 'src/components/features/prefill/PrefillPanel.tsx';
const modalPath =
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx';

const panel = (
  previous = [],
  status = async () => ({
    upToDateAppIds: ['A'],
    outdatedAppIds: [],
    unknownAppIds: ['B']
  })
) => {
  let badges = previous;
  let outdated = [];
  let unknown = [];
  const calls = [];
  const bindings = {
    signalR: { session: { id: 'session-a' } },
    gamesKeyRef: { current: 'epic:session-a' },
    gamesEpochRef: { current: 0 },
    reloadGamesRef: { current: null },
    reloadGamesAgainRef: { current: false },
    ownedGames: previous.map((appId) => ({ appId })),
    assertOk: async () => undefined,
    ApiError: Error,
    setOutdatedAppIds: (value) => {
      outdated = value;
    },
    setUnknownAppIds: (value) => {
      unknown = value;
    },
    setGameLoadError: () => undefined,
    gamesRequestRef: { current: null },
    gamesCacheRef: { current: null },
    gamesCacheWindowMs: 300000,
    serviceId: 'epic',
    serviceBasePath: 'epic-prefill',
    API_BASE: '/api',
    fetch: async () => ({
      ok: true,
      json: async () => ['A', 'B', 'C'].map((appId) => ({ appId }))
    }),
    ApiService: {
      getPrefillCachedApps: async (service) => {
        calls.push(service);
        return ['A', 'B'].map((appId) => ({ appId }));
      },
      getPrefillCacheStatus: status
    },
    setCachedAppIds: (update) => {
      badges = typeof update === 'function' ? update(badges) : update;
    },
    setIsLoadingGames: () => undefined,
    setOwnedGames: () => undefined,
    setShowGameSelection: () => undefined,
    setIsUsingGamesCache: () => undefined,
    addLog: () => undefined,
    t: (key) => key,
    resolveCachedAppIds
  };
  return {
    bindings,
    calls,
    badges: () => badges,
    outdated: () => outdated,
    unknown: () => unknown,
    load: bindLifted(liftHookCallback(panelPath, 'useCallback', 'const gamesCache ='), bindings)
  };
};

test('ordinary picker consumes explicit unknowns and filters unsolicited positives by eligibility', async () => {
  const picker = panel(['B', 'C'], async () => ({
    upToDateAppIds: ['A', 'C'],
    outdatedAppIds: [],
    unknownAppIds: ['B', 'C']
  }));
  await picker.load();
  assert.deepEqual(picker.badges(), ['A', 'B']);
  assert.deepEqual(picker.unknown(), ['B']);
  assert.deepEqual(picker.calls, ['epic']);
  assert.equal(picker.bindings.gamesCacheRef.current.hasData, true);
});

test('ordinary picker timeout keeps manager membership and marks it unknown', async () => {
  const picker = panel(['B', 'C'], async () => {
    throw new DOMException('timeout', 'TimeoutError');
  });
  await picker.load();
  assert.deepEqual(picker.badges(), ['A', 'B']);
  assert.deepEqual(picker.unknown(), ['A', 'B']);
  assert.equal(picker.bindings.gamesCacheRef.current.hasData, false);
});

test('ordinary picker service reset aborts stale verification without restoring deleted badges', async () => {
  let release;
  let entered;
  const ready = new Promise((resolve) => {
    entered = resolve;
  });
  const picker = panel(['A'], () => {
    entered();
    return new Promise((resolve) => {
      release = resolve;
    });
  });
  const pending = picker.load();
  await ready;
  const reset = bindLifted(
    liftHookCallback(panelPath, 'useEffect', 'setCachedAppIds([])'),
    picker.bindings
  );
  reset();
  release({ upToDateAppIds: ['A'], outdatedAppIds: [], unknownAppIds: [] });
  await pending;
  assert.deepEqual(picker.badges(), []);
  assert.equal(picker.bindings.gamesCacheRef.current, null);
});

test('ordinary picker reload bursts run one active pass and one catch-up pass', async () => {
  let release;
  let passes = 0;
  const reload = bindLifted(
    liftHookCallback(panelPath, 'useCallback', 'reloadGamesAgainRef.current = true'),
    {
      reloadGamesRef: { current: null },
      reloadGamesAgainRef: { current: false },
      gamesKeyRef: { current: 'epic:session-a' },
      gamesEpochRef: { current: 0 },
      loadGamesRef: {
        current: async () => {
          if (++passes === 1)
            await new Promise((resolve) => {
              release = resolve;
            });
        }
      }
    }
  );
  const pending = reload();
  reload();
  reload();
  release();
  await pending;
  assert.equal(passes, 2);
});

test('ordinary picker clear-all aborts stale requests, clears status, and reloads', async () => {
  const picker = panel(['A', 'B']);
  const calls = [];
  let reloads = 0;
  picker.bindings.gamesRequestRef.current = new AbortController();
  const controller = picker.bindings.gamesRequestRef.current;
  picker.bindings.setOutdatedAppIds(['A']);
  picker.bindings.setUnknownAppIds(['B']);
  Object.assign(picker.bindings, {
    ownedGames: [],
    setIsClearingAllCache: () => undefined,
    notifyError: assert.fail,
    reloadGamesOnce: async () => {
      reloads += 1;
    },
    ApiService: {
      clearAllPrefillCache: async (...args) => calls.push(args)
    }
  });
  await bindLifted(
    liftHookCallback(panelPath, 'useCallback', 'await ApiService.clearAllPrefillCache'),
    picker.bindings
  )();
  assert.deepEqual(picker.badges(), []);
  assert.deepEqual(picker.outdated(), []);
  assert.deepEqual(picker.unknown(), []);
  assert.equal(controller.signal.aborted, true);
  assert.equal(picker.bindings.gamesCacheRef.current, null);
  assert.equal(reloads, 1);
  assert.deepEqual(calls, [['epic']]);
});

test('scheduled picker merges unknowns, coalesces bursts and rejects old-session replies', async () => {
  let selection = {
    serviceKey: 'epic',
    scheduleId: 'schedule-1',
    sessionId: 's1',
    cachedAppIds: ['B', 'C'],
    outdatedAppIds: [],
    unknownAppIds: []
  };
  let release;
  let calls = 0;
  const bindings = {
    current: { current: { opening: 'opening-1' } },
    identityRef: { current: 'account-a' },
    containerRef: {
      current: {
        sessionId: 's1',
        isRunning: true,
        isAuthenticated: true,
        needsRelogin: false
      }
    },
    gameAuthRef: { current: { key: 'epic:s1', authenticated: true } },
    gameRequestRef: { current: null },
    gameSelectionRef: { current: selection },
    setGameLoadError: () => undefined,
    setGameLoaded: () => undefined,
    t: (key) => key,
    setLoadingGameSelectionService: () => undefined,
    setGameSelectionError: () => undefined,
    setGameSelection: (update) => {
      selection = update(selection);
    },
    isScheduledPrefillAnonymousService: (service) => ['battleNet', 'riot'].includes(service),
    getPersistentServiceId: (service) => service,
    getErrorMessage: String,
    ApiError: Error,
    resolveCachedAppIds,
    ApiService: {
      getPersistentPrefillGames: async () => {
        if (++calls === 1)
          await new Promise((resolve) => {
            release = resolve;
          });
        return {
          games: [],
          cachedAppIds: ['A'],
          outdatedAppIds: [],
          unknownAppIds: ['B']
        };
      }
    }
  };
  const load = bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'const key = `${serviceKey}:${sessionId}`'),
    bindings
  );
  const pending = load('epic', 's1');
  load('epic', 's1');
  load('epic', 's1');
  release();
  await pending;
  assert.equal(calls, 2);
  assert.deepEqual(selection.cachedAppIds, ['A', 'B']);
  selection = {
    serviceKey: 'xbox',
    scheduleId: 'schedule-2',
    sessionId: 's2',
    cachedAppIds: [],
    outdatedAppIds: [],
    unknownAppIds: []
  };
  bindings.gameSelectionRef.current = selection;
  bindings.gameAuthRef.current = { key: 'xbox:s2', authenticated: true };
  await load('epic', 's1');
  assert.deepEqual(selection.cachedAppIds, []);
});

test('scheduled picker refreshes from an external cache-change event without cache deletion', async () => {
  const picker = scheduled(async () => ({
    games: [{ appId: 'A', name: 'Alpha' }],
    cachedAppIds: ['A'],
    outdatedAppIds: ['A'],
    unknownAppIds: []
  }));
  let refresh;
  const effect = bindLifted(
    liftHookCallback(modalPath, 'useEffect', "onSignalR('PrefillCacheChanged', refresh)"),
    {
      gameSelectionRef: picker.bindings.gameSelectionRef,
      loadGameSelection: picker.load,
      onSignalR: (name, callback) => {
        assert.equal(name, 'PrefillCacheChanged');
        refresh = callback;
      },
      offSignalR: () => undefined
    }
  );
  effect();
  refresh();
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(picker.state().selection.cachedAppIds, ['A']);
  assert.deepEqual(picker.state().selection.outdatedAppIds, ['A']);
  assert.deepEqual(picker.state().selection.unknownAppIds, []);
});

const scheduled = (fetchGames) => {
  let selection = {
    serviceKey: 'steam',
    scheduleId: 'schedule-1',
    sessionId: 's1',
    games: [{ appId: 'A', name: 'Alpha' }],
    cachedAppIds: ['A'],
    outdatedAppIds: [],
    unknownAppIds: []
  };
  let loadError = 'previous load failure';
  let actionError = 'cache removal failed';
  let loading = null;
  let loaded = true;
  const bindings = {
    current: { current: { opening: 'opening-1' } },
    identityRef: { current: 'account-a' },
    containerRef: {
      current: {
        sessionId: 's1',
        isRunning: true,
        isAuthenticated: true,
        needsRelogin: false
      }
    },
    gameAuthRef: { current: { key: 'steam:s1', authenticated: true } },
    gameSelectionRef: { current: selection },
    gameRequestRef: { current: null },
    setLoadingGameSelectionService: (value) => {
      loading = value;
    },
    setGameLoadError: (value) => {
      loadError = value;
    },
    setGameLoaded: (value) => {
      loaded = value;
    },
    setGameSelectionError: (value) => {
      actionError = value;
    },
    setGameSelection: (update) => {
      selection = update(selection);
    },
    isScheduledPrefillAnonymousService: (service) => ['battleNet', 'riot'].includes(service),
    getPersistentServiceId: (service) => service,
    t: (key) => key,
    ApiError: Error,
    resolveCachedAppIds,
    ApiService: { getPersistentPrefillGames: fetchGames }
  };
  return {
    bindings,
    load: bindLifted(
      liftHookCallback(modalPath, 'useCallback', 'const key = `${serviceKey}:${sessionId}`'),
      bindings
    ),
    state: () => ({ selection, loadError, actionError, loading, loaded })
  };
};

test('scheduled picker request checks the current container before auth effects can run', async () => {
  for (const container of [
    {
      sessionId: 'replacement',
      isRunning: true,
      isAuthenticated: true,
      needsRelogin: false
    },
    { sessionId: 's1', isRunning: false, isAuthenticated: true, needsRelogin: false },
    { sessionId: 's1', isRunning: true, isAuthenticated: false, needsRelogin: false },
    { sessionId: 's1', isRunning: true, isAuthenticated: true, needsRelogin: true }
  ]) {
    let calls = 0;
    const picker = scheduled(async () => {
      calls += 1;
      return { games: [], cachedAppIds: [], outdatedAppIds: [], unknownAppIds: [] };
    });
    picker.bindings.containerRef.current = container;
    await picker.load('steam', 's1');
    assert.equal(calls, 0);
  }

  let calls = 0;
  const picker = scheduled(async () => {
    calls += 1;
    return { games: [], cachedAppIds: [], outdatedAppIds: [], unknownAppIds: [] };
  });
  await picker.load('steam', 's1');
  assert.equal(calls, 1);
});

test('failed scheduled load drains queued authentication and clears only its load error', async () => {
  let reject;
  const calls = [];
  const picker = scheduled(async (...args) => {
    calls.push(args);
    if (calls.length === 1)
      await new Promise((_resolve, fail) => {
        reject = fail;
      });
    return {
      games: [{ appId: 'A', name: 'Alpha' }],
      cachedAppIds: ['A'],
      outdatedAppIds: [],
      unknownAppIds: []
    };
  });
  const pending = picker.load('steam', 's1');
  assert.deepEqual(picker.state().selection.unknownAppIds, []);
  assert.equal(picker.state().selection.games.length, 1);
  picker.load('steam', 's1');
  picker.load('steam', 's1');
  reject(new Error('SteamKit2.AsyncJobFailedException'));
  await pending;
  assert.equal(calls.length, 2);
  assert.equal(calls[0][2], 's1');
  assert.equal(picker.state().loadError, null);
  assert.equal(picker.state().actionError, 'cache removal failed');
  assert.deepEqual(picker.state().selection.unknownAppIds, []);
});

test('scheduled failure remains safe and visible until an authoritative result', async () => {
  let attempt = 0;
  const picker = scheduled(async () => {
    if (++attempt === 1) throw new Error('SteamKit2.AsyncJobFailedException');
    return { games: [], cachedAppIds: [], outdatedAppIds: [], unknownAppIds: ['A'] };
  });
  await picker.load('steam', 's1');
  const safeError = picker.state().loadError;
  assert.equal(safeError, 'errors.prefill.requestFailed');
  assert.equal(picker.state().selection.games.length, 1);
  await picker.load('steam', 's1');
  assert.equal(picker.state().loadError, null);
  assert.deepEqual(picker.state().selection.cachedAppIds, ['A']);
});

test('closed scheduled picker discards late failure and finalizer', async () => {
  let reject;
  const picker = scheduled(
    () =>
      new Promise((_resolve, fail) => {
        reject = fail;
      })
  );
  const pending = picker.load('steam', 's1');
  picker.bindings.gameSelectionRef.current = null;
  picker.bindings.gameRequestRef.current.controller.abort();
  picker.bindings.gameRequestRef.current = null;
  reject(new Error('late failure'));
  await pending;
  assert.equal(picker.state().loadError, 'previous load failure');
  assert.equal(
    picker.state().loading,
    'steam',
    'the old finalizer cannot write into a replacement owner'
  );
});

test('scheduled picker rejects late results after opening and account ownership change', async () => {
  let resolve;
  const picker = scheduled(
    () =>
      new Promise((done) => {
        resolve = done;
      })
  );
  const pending = picker.load('steam', 's1');
  picker.bindings.current.current = { opening: 'opening-2' };
  picker.bindings.identityRef.current = 'account-b';
  resolve({
    games: [{ appId: 'B', name: 'Beta' }],
    cachedAppIds: ['B'],
    outdatedAppIds: [],
    unknownAppIds: []
  });
  await pending;
  assert.deepEqual(picker.state().selection.games, [{ appId: 'A', name: 'Alpha' }]);
  assert.deepEqual(picker.state().selection.cachedAppIds, ['A']);
  assert.equal(picker.state().loading, 'steam');
});

test('ordinary trailing reload reads latest loader and an old owner cannot drain after replacement', async () => {
  let release;
  const calls = [];
  const bindings = {
    reloadGamesRef: { current: null },
    reloadGamesAgainRef: { current: false },
    gamesKeyRef: { current: 'steam:s1' },
    gamesEpochRef: { current: 0 },
    loadGamesRef: {
      current: async () => {
        calls.push('first');
        await new Promise((resolve) => {
          release = resolve;
        });
      }
    }
  };
  const reload = bindLifted(
    liftHookCallback(panelPath, 'useCallback', 'reloadGamesAgainRef.current = true'),
    bindings
  );
  const pending = reload();
  reload();
  bindings.loadGamesRef.current = async () => {
    calls.push('latest');
  };
  release();
  await pending;
  assert.deepEqual(calls, ['first', 'latest']);
  bindings.loadGamesRef.current = async () => {
    calls.push('old');
    await new Promise((resolve) => {
      release = resolve;
    });
  };
  const old = reload();
  reload();
  bindings.gamesKeyRef.current = 'steam:s2';
  bindings.gamesEpochRef.current += 1;
  const replacement = Promise.resolve();
  bindings.reloadGamesRef.current = replacement;
  release();
  await old;
  assert.equal(bindings.reloadGamesRef.current, replacement);
  assert.deepEqual(calls, ['first', 'latest', 'old']);
});

test('ordinary same-session authentication invalidates freshness and reloads only an open picker', () => {
  let calls = 0;
  const bindings = {
    serviceId: 'steam',
    signalR: { session: { id: 's1' }, isLoggedIn: true },
    gameAuthRef: { current: { key: 'steam:s1', authenticated: false } },
    gamesCacheRef: { current: { hasData: true } },
    gamesRequestRef: { current: null },
    setIsLoadingGames: () => undefined,
    setUnknownAppIds: () => undefined,
    ownedGames: [],
    showGameSelection: true,
    reloadGamesOnce: () => {
      calls += 1;
    }
  };
  const source = liftHookCallback(
    panelPath,
    'useEffect',
    'previous.authenticated === signalR.isLoggedIn'
  );
  bindLifted(source, bindings)();
  assert.equal(calls, 1);
  assert.equal(bindings.gamesCacheRef.current, null);
  bindings.gameAuthRef.current = { key: 'steam:s1', authenticated: false };
  bindLifted(source, { ...bindings, showGameSelection: false })();
  assert.equal(calls, 1);
  bindings.gameAuthRef.current = { key: 'steam:other', authenticated: false };
  bindLifted(source, bindings)();
  assert.equal(calls, 1);
});

test('scheduled picker retains choices through auth loss, reloads on recovery, and closes on replacement', () => {
  let selection = {
    serviceKey: 'steam',
    scheduleId: 'schedule-1',
    sessionId: 's1',
    games: [{ appId: 'A', name: 'Alpha' }],
    cachedAppIds: ['A'],
    outdatedAppIds: [],
    unknownAppIds: []
  };
  const calls = [];
  const gameSelectionRef = { current: selection };
  const gameAuthRef = { current: { key: 'steam:s1', authenticated: true } };
  const controller = new AbortController();
  const gameRequestRef = { current: { key: 'steam:s1', controller } };
  const loading = [];
  const bindings = {
    target: { serviceKey: 'steam', scheduleId: 'schedule-1' },
    gameSelection: selection,
    container: {
      sessionId: 's1',
      isRunning: true,
      isAuthenticated: false,
      needsRelogin: false
    },
    gameAuthRef,
    loadGameSelection: (...args) => calls.push(args),
    gameRequestRef,
    gameSelectionRef,
    setGameSelection: (update) => {
      selection = typeof update === 'function' ? update(selection) : update;
      gameSelectionRef.current = selection;
    },
    setLoadingGameSelectionService: (value) => loading.push(value),
    setGameLoaded: () => undefined,
    isScheduledPrefillAnonymousService: () => false
  };
  const source = liftHookCallback(modalPath, 'useEffect', 'const authenticated =');
  bindLifted(source, bindings)();
  assert.equal(controller.signal.aborted, true);
  assert.equal(selection.games[0].appId, 'A');
  assert.deepEqual(selection.cachedAppIds, ['A']);
  assert.deepEqual(selection.unknownAppIds, ['A']);
  assert.deepEqual(calls, []);

  bindLifted(source, {
    ...bindings,
    gameSelection: selection,
    container: {
      sessionId: 's1',
      isRunning: true,
      isAuthenticated: true,
      needsRelogin: false
    }
  })();
  assert.deepEqual(calls, [['steam', 's1']]);

  bindLifted(source, {
    ...bindings,
    gameSelection: selection,
    container: {
      sessionId: 'replacement',
      isRunning: true,
      isAuthenticated: true,
      needsRelogin: false
    }
  })();
  assert.equal(selection, null);
  assert.equal(gameSelectionRef.current, null);
  assert.equal(gameAuthRef.current, null);
  assert.deepEqual(loading, [null, null]);
});

test('persistent games request pins the session and retains the unpinned client contract', async () => {
  const source = parseSource('src/services/api.service.ts');
  const method = findSoleNode(
    source,
    'persistent games request',
    (node) =>
      ts.isMethodDeclaration(node) && node.name.getText(source) === 'getPersistentPrefillGames'
  );
  const urls = [];
  const request = bindLifted(method.getText(source).replace(/^static async /, 'async function '), {
    API_BASE: '/api',
    fetch: async (url) => {
      urls.push(url);
      return {};
    },
    isAbortError: () => false
  });
  const receiver = {
    getFetchOptions: (options) => options,
    handleResponse: async () => ({
      games: [],
      cachedAppIds: [],
      outdatedAppIds: [],
      unknownAppIds: []
    })
  };
  await request.call(receiver, 'steam', undefined, 'session / one');
  await request.call(receiver, 'steam');
  assert.equal(
    urls[0],
    '/api/system/prefill/persistent/games?service=steam&expectedSessionId=session%20%2F%20one'
  );
  assert.equal(urls[1], '/api/system/prefill/persistent/games?service=steam');
});

test('ordinary clear-all failure survives a concurrent successful library refresh', async () => {
  const picker = panel(['A']);
  let reject;
  const notices = [];
  const bindings = {
    ...picker.bindings,
    gamesEpochRef: { current: 1 },
    gamesKeyRef: { current: 'epic:session-a' },
    setIsClearingAllCache: () => undefined,
    notifyError: (...args) => notices.push(args),
    addLog: () => undefined,
    serviceId: 'epic',
    t: (key) => key,
    ApiService: {
      clearAllPrefillCache: () =>
        new Promise((_resolve, fail) => {
          reject = fail;
        })
    },
    reloadGamesOnce: picker.load
  };
  const clear = bindLifted(
    liftHookCallback(panelPath, 'useCallback', 'await ApiService.clearAllPrefillCache'),
    bindings
  );
  const pending = clear();
  await picker.load();
  reject(new Error('private failure details'));
  await pending;
  assert.equal(notices.length, 1);
  assert.equal(notices[0][0], 'prefill.errors.clearAllFromCacheFailed');
  assert.equal(notices[0][1].message, 'private failure details');
  assert.deepEqual(picker.badges(), ['A', 'B']);
});
