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
  status = async () => ({ upToDateAppIds: ['A'], unknownAppIds: ['B'] })
) => {
  let badges = previous;
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
    setUnknownAppIds: () => undefined,
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
    load: bindLifted(liftHookCallback(panelPath, 'useCallback', 'const gamesCache ='), bindings)
  };
};

test('ordinary picker consumes explicit unknowns and filters unsolicited positives by eligibility', async () => {
  const picker = panel(['B', 'C'], async () => ({
    upToDateAppIds: ['A', 'C'],
    unknownAppIds: ['B', 'C']
  }));
  await picker.load();
  assert.deepEqual(picker.badges(), ['A', 'B']);
  assert.deepEqual(picker.calls, ['epic']);
  assert.equal(picker.bindings.gamesCacheRef.current.hasData, false);
});

test('ordinary picker timeout preserves eligible previous badges and remains retryable', async () => {
  const picker = panel(['B', 'C'], async () => {
    throw new DOMException('timeout', 'TimeoutError');
  });
  await picker.load();
  assert.deepEqual(picker.badges(), ['B']);
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
  release({ upToDateAppIds: ['A'], unknownAppIds: [] });
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

test('ordinary picker controls pass service and abort stale badge requests', async () => {
  const picker = panel(['A', 'B']);
  const calls = [];
  picker.bindings.gamesRequestRef.current = new AbortController();
  const controller = picker.bindings.gamesRequestRef.current;
  Object.assign(picker.bindings, {
    ownedGames: [],
    setRemovingAppId: () => undefined,
    setIsClearingAllCache: () => undefined,
    notifyError: assert.fail,
    reloadGamesOnce: async () => undefined,
    ApiService: {
      deletePrefillCachedApp: async (...args) => calls.push(args),
      clearAllPrefillCache: async (...args) => calls.push(args)
    }
  });
  await bindLifted(
    liftHookCallback(panelPath, 'useCallback', 'await ApiService.deletePrefillCachedApp'),
    picker.bindings
  )('A');
  assert.deepEqual(picker.badges(), ['B']);
  assert.equal(controller.signal.aborted, true);
  await bindLifted(
    liftHookCallback(panelPath, 'useCallback', 'await ApiService.clearAllPrefillCache'),
    picker.bindings
  )();
  assert.deepEqual(picker.badges(), []);
  assert.deepEqual(calls, [['A', 'epic'], ['epic']]);
});

test('scheduled picker merges unknowns, coalesces bursts and rejects old-session replies', async () => {
  let selection = { serviceKey: 'epic', sessionId: 's1', cachedAppIds: ['B', 'C'] };
  let release;
  let calls = 0;
  const bindings = {
    gameRequestRef: { current: null },
    gameSelectionRef: { current: selection },
    setGameLoadError: () => undefined,
    t: (key) => key,
    setLoadingGameSelectionService: () => undefined,
    setGameSelectionError: () => undefined,
    setGameSelection: (update) => {
      selection = update(selection);
    },
    getPersistentServiceId: (service) => service,
    getErrorMessage: String,
    resolveCachedAppIds,
    ApiService: {
      getPersistentPrefillGames: async () => {
        if (++calls === 1)
          await new Promise((resolve) => {
            release = resolve;
          });
        return { games: [], cachedAppIds: ['A'], unknownAppIds: ['B'] };
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
  selection = { serviceKey: 'xbox', sessionId: 's2', cachedAppIds: [] };
  bindings.gameSelectionRef.current = selection;
  await load('epic', 's1');
  assert.deepEqual(selection.cachedAppIds, []);
});

test('scheduled picker controls scope deletion and clear, and re-read without a socket', async () => {
  let selection = { serviceKey: 'xbox', sessionId: 's2', cachedAppIds: ['opaque/id', 'B'] };
  const calls = [];
  const bindings = {
    gameSelection: selection,
    gameSelectionRef: { current: selection },
    gameEpochRef: { current: 0 },
    gameRequestRef: { current: { controller: new AbortController() } },
    setRemovingCachedAppId: () => undefined,
    setIsClearingCachedGames: () => undefined,
    setGameSelectionError: (error) => assert.equal(error, null),
    getErrorMessage: String,
    getPersistentServiceId: (service) => service,
    setGameSelection: (update) => {
      selection = update(selection);
    },
    loadGameSelection: async (...args) => calls.push(args),
    ApiService: {
      deletePrefillCachedApp: async (...args) => calls.push(args),
      clearAllPrefillCache: async (...args) => calls.push(args)
    }
  };
  await bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'await ApiService.deletePrefillCachedApp'),
    bindings
  )('opaque/id');
  assert.deepEqual(selection.cachedAppIds, ['B']);
  await bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'await ApiService.clearAllPrefillCache'),
    bindings
  )();
  assert.deepEqual(selection.cachedAppIds, []);
  assert.deepEqual(calls, [['opaque/id', 'xbox'], ['xbox', 's2'], ['xbox'], ['xbox', 's2']]);
});

const scheduled = (fetchGames) => {
  let selection = {
    serviceKey: 'steam',
    sessionId: 's1',
    games: [{ appId: 'A', name: 'Alpha' }],
    cachedAppIds: ['A'],
    unknownAppIds: []
  };
  let loadError = 'previous load failure';
  let actionError = 'cache removal failed';
  let loading = null;
  const bindings = {
    gameSelectionRef: { current: selection },
    gameRequestRef: { current: null },
    setLoadingGameSelectionService: (value) => {
      loading = value;
    },
    setGameLoadError: (value) => {
      loadError = value;
    },
    setGameSelectionError: (value) => {
      actionError = value;
    },
    setGameSelection: (update) => {
      selection = update(selection);
    },
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
    state: () => ({ selection, loadError, actionError, loading })
  };
};

test('failed scheduled load drains queued authentication and clears only its load error', async () => {
  let reject;
  const calls = [];
  const picker = scheduled(async (...args) => {
    calls.push(args);
    if (calls.length === 1)
      await new Promise((_resolve, fail) => {
        reject = fail;
      });
    return { games: [{ appId: 'A', name: 'Alpha' }], cachedAppIds: ['A'], unknownAppIds: [] };
  });
  const pending = picker.load('steam', 's1');
  assert.deepEqual(picker.state().selection.unknownAppIds, ['A']);
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
    return { games: [], cachedAppIds: [], unknownAppIds: ['A'] };
  });
  await picker.load('steam', 's1');
  const safeError = picker.state().loadError;
  assert.equal(safeError, 'errors.prefill.requestFailed');
  assert.equal(picker.state().selection.games.length, 1);
  await picker.load('steam', 's1');
  assert.equal(picker.state().loadError, safeError);
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

test('scheduled authoritative same-session authentication refreshes the current picker', () => {
  const selection = { serviceKey: 'steam', sessionId: 's1', cachedAppIds: ['A'] };
  const calls = [];
  const bindings = {
    opened: true,
    gameSelection: selection,
    gameAuthRef: { current: { key: 'steam:s1', authenticated: false } },
    persistentContainerByService: new Map([['steam', { sessionId: 's1', isAuthenticated: true }]]),
    getPersistentServiceId: (service) => service,
    loadGameSelection: (...args) => calls.push(args),
    gameRequestRef: { current: null },
    gameSelectionRef: { current: selection },
    setGameSelection: () => undefined,
    setLoadingGameSelectionService: () => undefined
  };
  const source = liftHookCallback(
    modalPath,
    'useEffect',
    'authenticated: container.isAuthenticated'
  );
  bindLifted(source, bindings)();
  assert.deepEqual(calls, [['steam', 's1']]);
  bindings.gameAuthRef.current = { key: 'steam:other', authenticated: false };
  bindLifted(source, bindings)();
  assert.equal(calls.length, 1);
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
    handleResponse: async () => ({ games: [], cachedAppIds: [], unknownAppIds: [] })
  };
  await request.call(receiver, 'steam', undefined, 'session / one');
  await request.call(receiver, 'steam');
  assert.equal(
    urls[0],
    '/api/system/prefill/persistent/games?service=steam&expectedSessionId=session%20%2F%20one'
  );
  assert.equal(urls[1], '/api/system/prefill/persistent/games?service=steam');
});

test('scheduled cache action failure survives a concurrent successful library refresh', async () => {
  const picker = scheduled(async () => ({ games: [], cachedAppIds: [], unknownAppIds: [] }));
  let reject;
  let actionError = null;
  const bindings = {
    ...picker.bindings,
    gameSelection: picker.bindings.gameSelectionRef.current,
    gameEpochRef: { current: 1 },
    setRemovingCachedAppId: () => undefined,
    setGameSelectionError: (value) => {
      actionError = value;
    },
    t: (key) => key,
    ApiService: {
      deletePrefillCachedApp: () =>
        new Promise((_resolve, fail) => {
          reject = fail;
        })
    },
    loadGameSelection: picker.load
  };
  const remove = bindLifted(
    liftHookCallback(modalPath, 'useCallback', 'await ApiService.deletePrefillCachedApp'),
    bindings
  );
  const pending = remove('A');
  await picker.load('steam', 's1');
  reject(new Error('private failure details'));
  await pending;
  assert.equal(actionError, 'prefill.errors.removeFromCacheFailed');
  assert.equal(picker.state().loadError, null);
});
