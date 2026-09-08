import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback } from './transpile-module.mjs';

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
      loadGames: async () => {
        if (++passes === 1)
          await new Promise((resolve) => {
            release = resolve;
          });
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
  await load('epic', 's1');
  assert.deepEqual(selection.cachedAppIds, []);
});

test('scheduled picker controls scope deletion and clear, and re-read without a socket', async () => {
  let selection = { serviceKey: 'xbox', sessionId: 's2', cachedAppIds: ['opaque/id', 'B'] };
  const calls = [];
  const bindings = {
    gameSelection: selection,
    gameRequestRef: { current: { controller: new AbortController() } },
    setRemovingCachedAppId: () => undefined,
    setIsClearingCachedGames: () => undefined,
    setGameSelectionError: assert.fail,
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
