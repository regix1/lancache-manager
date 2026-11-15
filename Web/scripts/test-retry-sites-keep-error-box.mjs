import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import {
  bindLifted,
  findSoleNode,
  liftHookCallback,
  make,
  parseSource
} from './transpile-module.mjs';

/**
 * Four more error boxes with Retry: Game cache detection, the failed games of a scheduled prefill
 * run, and the prefill connection box in both places the prefill page draws it. Clicking Retry
 * keeps the box on screen, with Retry busy, until the new read or connection answers: the box never
 * vanishes only to come back when the retry fails again. A connection already being made when the
 * user reaches Retry shows busy too, and starts no second connection.
 *
 * The shipped code runs here with stubs at its edges. Game cache detection is too large to draw
 * whole, so its load effect, its `syncCachedDetection` and its error box expression are lifted out
 * of the file, the way `test-count-dialog-owns-its-count.mjs` drives that component. The prefill
 * page is too large as well: the hook it reads runs whole, and the page's two connection box
 * expressions and its main hub reconnect handler are lifted out of `PrefillPanel.tsx`.
 */

const GAME_CACHE = 'src/components/features/management/game-detection/GameCacheDetector.tsx';
const RECENT_RUN =
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillActivityModal.tsx';
const PREFILL_HOOK = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
const PREFILL_PAGE = 'src/components/features/prefill/PrefillPanel.tsx';
const ERROR_BLOCK = 'src/components/ui/ErrorBlock.tsx';
const MESSAGE = 'The read failed';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name `path` imports, so a stub stands in for any the test does not bind itself. */
const importedNames = (path) =>
  parseSource(path, typescript.ScriptKind.TSX)
    .statements.filter(typescript.isImportDeclaration)
    .flatMap((declaration) => {
      const clause = declaration.importClause;
      if (!clause || clause.isTypeOnly) return [];
      const names = clause.name ? [clause.name.text] : [];
      const bindings = clause.namedBindings;
      if (bindings && typescript.isNamedImports(bindings))
        names.push(
          ...bindings.elements
            .filter((element) => !element.isTypeOnly)
            .map((element) => element.name.text)
        );
      return names;
    });

/** One component's hooks; `draw` starts a render and hands back the effects it asked for. */
const hookStore = () => {
  const slots = [];
  let cursor = 0;
  let effects = [];
  const hooks = {
    useState: (initial) => {
      const index = cursor++;
      const slot = (slots[index] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => {
      const index = cursor++;
      return (slots[index] ??= { current: initial });
    },
    useMemo: (build) => {
      cursor++;
      return build();
    },
    useCallback: (callback) => {
      cursor++;
      return callback;
    },
    useEffect: (effect) => {
      cursor++;
      effects.push(effect);
    },
    useLayoutEffect: (effect) => {
      cursor++;
      effects.push(effect);
    },
    useId: () => {
      cursor++;
      return 'details';
    }
  };
  const draw = () => {
    cursor = 0;
    effects = [];
    return () => effects;
  };
  return { hooks, draw };
};

const Button = stub('Button');

const turn = () => new Promise((resolve) => setImmediate(resolve));

/** The shipped error block, and the Retry buttons a drawn tree holds. */
const errorBlockHarness = () => {
  const blockStore = hookStore();
  const ErrorBlock = make(ERROR_BLOCK, 'ErrorBlock', {
    ...Object.fromEntries(importedNames(ERROR_BLOCK).map((name) => [name, stub(name)])),
    React: { ...React, ...blockStore.hooks },
    ...blockStore.hooks,
    useConnectionLost: () => false,
    Button
  });
  const retryButtons = (tree) => {
    const found = [];
    const walk = (node) => {
      if (Array.isArray(node)) {
        node.forEach(walk);
        return;
      }
      if (!React.isValidElement(node)) return;
      if (node.type === ErrorBlock) {
        blockStore.draw();
        walk(ErrorBlock(node.props));
        return;
      }
      if (node.type === Button && node.props.children === 'common.retry') found.push(node.props);
      Object.values(node.props).forEach(walk);
    };
    walk(tree);
    return found;
  };
  return { ErrorBlock, retryButtons };
};

/** Clicks a drawn Retry the way a browser does: a busy button is disabled, so it takes no click. */
const click = (retry) => {
  if (!retry.loading) retry.onClick();
};

/** Reads the test answers when it chooses, oldest first. */
const heldReads = () => {
  const out = [];
  const start = () =>
    new Promise((resolve, reject) => {
      out.push({ succeed: resolve, fail: (message) => reject(new Error(message)) });
    });
  const next = () => {
    const read = out.shift();
    assert.ok(read, 'no read is out');
    return read;
  };
  return { start, next, count: () => out.length };
};

/** The `{condition && (<ErrorBlock ... />)}` expressions in `path` whose text holds `contains`. */
const errorBoxExpressions = (path, contains) => {
  const source = parseSource(path, typescript.ScriptKind.TSX);
  const unwrap = (node) =>
    typescript.isParenthesizedExpression(node) ? unwrap(node.expression) : node;
  const found = [];
  const visit = (node) => {
    if (typescript.isJsxExpression(node) && node.expression) {
      const expression = unwrap(node.expression);
      if (
        typescript.isBinaryExpression(expression) &&
        expression.operatorToken.kind === typescript.SyntaxKind.AmpersandAmpersandToken
      ) {
        const element = unwrap(expression.right);
        if (
          typescript.isJsxSelfClosingElement(element) &&
          element.tagName.getText(source) === 'ErrorBlock' &&
          element.getText(source).includes(contains)
        )
          found.push(expression.getText(source));
      }
    }
    typescript.forEachChild(node, visit);
  };
  visit(source);
  return found;
};

/** Evaluates a lifted JSX expression with the names it reads supplied by `bindings`. */
const drawLifted = (expression, bindings) =>
  bindLifted(`() => (${expression})`, { React, ...bindings }, { jsx: typescript.JsxEmit.React })();

// --- Game cache detection ---------------------------------------------------------------------

const mountGameCache = () => {
  const reads = heldReads();
  const block = errorBlockHarness();
  const state = { loadError: null, isLoadingInitialCache: true, initialLoadRetry: 0 };
  const setter = (key) => (next) => {
    state[key] = typeof next === 'function' ? next(state[key]) : next;
  };
  const t = (key) => key;
  const syncCachedDetection = bindLifted(
    liftHookCallback(GAME_CACHE, 'useCallback', 'loadCachedDetectionSnapshot()'),
    {
      cachedDetectionRequestRef: { current: 0 },
      loadCachedDetectionSnapshot: () => reads.start(),
      applyCachedDetectionSnapshot: () => undefined,
      clearCachedDetectionSnapshot: () => undefined,
      setLoadError: setter('loadError'),
      invalidateImageCache: undefined,
      notifyError: () => undefined,
      t
    }
  );
  const loadEffect = bindLifted(liftHookCallback(GAME_CACHE, 'useEffect', 'loadCachedGames'), {
    mockMode: false,
    setLoadError: setter('loadError'),
    MockDataService: {},
    setGames: () => undefined,
    setServices: () => undefined,
    setLastDetectionTime: () => undefined,
    setIsLoadingInitialCache: setter('isLoadingInitialCache'),
    syncCachedDetection,
    getErrorMessage: (error) => error.message,
    sessionStore: { getItem: () => null, setItem: () => undefined },
    LOADED_RESULTS_SESSION_KEY: 'loaded-results',
    loading: false,
    scanType: null,
    buildLoadedResultsSummary: () => null,
    addNotification: () => undefined,
    t
  });
  const [box] = errorBoxExpressions(GAME_CACHE, 'setInitialLoadRetry');
  assert.ok(box, 'Game cache detection draws no error box with a Retry');
  // The effect runs on mount and again whenever its retry counter moves, as React runs it.
  let ranFor = -1;
  const settleEffect = () => {
    if (ranFor === state.initialLoadRetry) return;
    ranFor = state.initialLoadRetry;
    loadEffect();
  };
  const retryButtons = () => {
    settleEffect();
    return block.retryButtons(
      drawLifted(box, {
        ...state,
        setInitialLoadRetry: setter('initialLoadRetry'),
        hasBodyContent: false,
        t,
        ErrorBlock: block.ErrorBlock
      })
    );
  };
  return {
    retryButtons,
    failFirst: async () => {
      retryButtons();
      await turn();
      reads.next().fail(MESSAGE);
    },
    failAgain: () => reads.next().fail(MESSAGE)
  };
};

// --- The failed games of a scheduled prefill run ----------------------------------------------

const FAILED_RUN = {
  runId: 'run-1',
  scheduleName: 'Nightly',
  completedAtUtc: '2026-11-13T11:00:00Z',
  historyIncomplete: false,
  options: { selection: 'all', operatingSystems: [] },
  snapshot: {
    state: 'failed',
    reason: null,
    failedApps: 2,
    completedApps: 1,
    cachedApps: 0,
    totalApps: 3,
    bytesTransferred: 0,
    startedAt: '2026-11-13T10:00:00Z'
  }
};

const mountRecentRun = () => {
  const reads = heldReads();
  const block = errorBlockHarness();
  const store = hookStore();
  const RecentRun = make(RECENT_RUN, 'ScheduledPrefillRecentRun', {
    ...Object.fromEntries(importedNames(RECENT_RUN).map((name) => [name, stub(name)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useTranslation: () => ({ t: (key) => key }),
    SCHEDULED_PREFILL_PLATFORM_UI: { steam: { icon: stub('SteamIcon'), rowClassName: 'row' } },
    getPersistentServiceId: () => 'steam',
    getPrefillRunReasonKey: () => null,
    formatBytes: (bytes) => `${bytes} B`,
    formatCount: (value) => String(value),
    formatDurationFromSeconds: (seconds) => `${seconds} s`,
    getErrorMessage: (error) => error.message,
    ApiError: class ApiError extends Error {},
    ApiService: { getPersistentPrefillRunFailedGames: () => reads.start() },
    ErrorBlock: block.ErrorBlock,
    Button
  });
  let mounted = false;
  const draw = () => {
    const effects = store.draw();
    const tree = RecentRun({ run: FAILED_RUN, serviceKey: 'steam' });
    if (!mounted) {
      mounted = true;
      effects().forEach((effect) => effect());
    }
    return tree;
  };
  /** The details toggle of the row. */
  const toggle = () => {
    const found = [];
    const walk = (node) => {
      if (Array.isArray(node)) {
        node.forEach(walk);
        return;
      }
      if (!React.isValidElement(node)) return;
      if (node.type === Button && 'aria-expanded' in node.props) found.push(node.props);
      Object.values(node.props).forEach(walk);
    };
    walk(draw());
    return found[0];
  };
  return {
    retryButtons: () => block.retryButtons(draw()),
    failFirst: async () => {
      toggle().onClick();
      await turn();
      reads.next().fail(MESSAGE);
    },
    failAgain: () => reads.next().fail(MESSAGE)
  };
};

// --- The prefill connection box -----------------------------------------------------------------

/** The prefill hook with a hub whose connections start when the test says so. */
const mountPrefill = (boxIndex) => {
  const starts = heldReads();
  const block = errorBlockHarness();
  const store = hookStore();
  globalThis.document = {
    hidden: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined
  };
  let sessionLookups = 0;
  const connection = () => {
    const hub = {
      state: 'Disconnected',
      start: () =>
        starts.start().then(() => {
          hub.state = 'Connected';
        }),
      stop: async () => undefined,
      invoke: async (method) => {
        if (method === 'GetMySessions') sessionLookups += 1;
        return [];
      },
      on: () => undefined,
      off: () => undefined,
      onreconnecting: () => undefined,
      onreconnected: () => undefined,
      onclose: () => undefined
    };
    return hub;
  };
  class HeldHubSetup {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return connection();
    }
  }
  const usePrefillSignalR = make(PREFILL_HOOK, 'usePrefillSignalR', {
    ...Object.fromEntries(importedNames(PREFILL_HOOK).map((name) => [name, stub(name)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    HubConnectionBuilder: HeldHubSetup,
    window: { addEventListener: () => undefined, removeEventListener: () => undefined },
    HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
    LogLevel: { Information: 2 },
    SIGNALR_BASE: '/hubs',
    STORAGE_KEYS: { PREFILL_SESSION_ID: 'prefill-session' },
    sessionStore: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined },
    i18n: { t: (key) => key },
    getErrorMessage: (error) => error.message,
    prefillServiceConfig: () => ({ serviceNameKey: 'steam' }),
    registerPrefillEventHandlers: () => undefined,
    InfiniteBackoffRetryPolicy: class InfiniteBackoffRetryPolicy {},
    useReconnectRefetch: () => undefined,
    useErrorHandler: () => ({ notifyError: () => undefined }),
    canStartPrefill: () => false,
    isPrefillRunActive: () => false,
    mergePrefillRuns: (_current, incoming) => incoming,
    supportsConcurrentPrefill: () => false
  });
  const options = {
    addLog: () => undefined,
    onAuthStateChanged: () => undefined,
    clearAllPrefillStorage: () => undefined
  };
  let mounted = false;
  const signalR = () => {
    const effects = store.draw();
    const value = usePrefillSignalR(options);
    if (!mounted) {
      mounted = true;
      effects().forEach((effect) => effect());
    }
    return value;
  };

  const boxes = errorBoxExpressions(PREFILL_PAGE, 'retryConnection');
  assert.equal(boxes.length, 2, 'the prefill page does not draw its connection box in two places');
  const t = (key) => key;
  const retryButtons = () => {
    const current = signalR();
    return block.retryButtons(
      drawLifted(boxes[boxIndex], {
        signalR: current,
        retryConnection: current.retryConnection,
        t,
        ErrorBlock: block.ErrorBlock
      })
    );
  };

  // The page's main hub reconnect handler: a page opened during an outage retries its prefill
  // connection here (PrefillPanel.tsx, the `useReconnectRefetch` call that reloads the games).
  const pageSource = parseSource(PREFILL_PAGE, typescript.ScriptKind.TSX);
  const reconnectCall = findSoleNode(
    pageSource,
    'main hub reconnect handler',
    (node) =>
      typescript.isCallExpression(node) &&
      node.expression.getText(pageSource) === 'useReconnectRefetch' &&
      node.arguments.length === 2 &&
      node.arguments[1].getText(pageSource).includes('retryConnection')
  );
  const retryAfterStartRef = { current: false };
  const mainHubReconnects = () => {
    const current = signalR();
    bindLifted(reconnectCall.arguments[1].getText(pageSource), {
      reloadGamesOnce: async () => undefined,
      signalR: current,
      retryConnection: current.retryConnection,
      retryAfterStartRef
    })();
  };

  return {
    retryButtons,
    mainHubReconnects,
    connectionsStarted: () => starts.count(),
    sessionLookups: () => sessionLookups,
    failFirst: async () => {
      retryButtons();
      await turn();
      starts.next().fail(MESSAGE);
    },
    failAgain: () => starts.next().fail(MESSAGE),
    connect: () => starts.next().succeed()
  };
};

const SITES = {
  'Game cache detection': mountGameCache,
  'the failed games of a scheduled prefill run': mountRecentRun,
  'the prefill connection box above the home page': () => mountPrefill(0),
  'the prefill connection box above the session': () => mountPrefill(1)
};

for (const [site, mount] of Object.entries(SITES)) {
  test(`on ${site}, the error box stays drawn with Retry busy until the retry answers`, async () => {
    const originalError = console.error;
    console.error = () => undefined;
    try {
      const view = mount();
      await view.failFirst();
      await turn();
      await turn();

      const [failed] = view.retryButtons();
      assert.ok(failed, `a failed read on ${site} draws no Retry button`);
      assert.ok(!failed.loading, 'Retry shows busy before it was clicked');

      click(failed);
      await turn();
      const [running] = view.retryButtons();
      assert.ok(
        running,
        `clicking Retry on ${site} removes the error box while the retry is out, so the box vanishes until the retry fails and the box comes back`
      );
      assert.equal(
        running.loading,
        true,
        `while the retry started on ${site} is out, the Retry button is drawn with loading ${String(running.loading)}`
      );

      view.failAgain();
      await turn();
      await turn();
      await turn();
      const [again] = view.retryButtons();
      assert.ok(again, `a retry that failed again on ${site} draws no Retry button`);
      assert.ok(!again.loading, `Retry on ${site} still shows busy after the retry failed again`);
    } finally {
      console.error = originalError;
      delete globalThis.document;
    }
  });
}

for (const [index, place] of ['above the home page', 'above the session'].entries()) {
  for (const order of [
    'the click, then the main hub reconnects',
    'the main hub reconnects, then the click'
  ]) {
    test(`the prefill connection box ${place} shows busy and starts one connection when ${order}`, async () => {
      const originalError = console.error;
      console.error = () => undefined;
      try {
        const view = mountPrefill(index);
        await view.failFirst();
        await turn();
        await turn();
        assert.ok(view.retryButtons()[0], 'a failed connection draws no Retry button');

        let reconnected = false;
        const steps = {
          click: () => {
            const [retry] = view.retryButtons();
            assert.ok(retry, 'the connection box is gone before the user could click Retry');
            if (reconnected)
              assert.equal(
                retry.loading,
                true,
                `while the connection the main hub reconnect started is being made, the connection box ${place} draws Retry with loading ${String(retry.loading)}, so a click starts a second run`
              );
            click(retry);
          },
          reconnect: () => {
            reconnected = true;
            view.mainHubReconnects();
          }
        };
        const sequence =
          order === 'the click, then the main hub reconnects'
            ? ['click', 'reconnect']
            : ['reconnect', 'click'];
        for (const step of sequence) {
          steps[step]();
          await turn();
        }
        await turn();

        const [running] = view.retryButtons();
        assert.ok(
          running,
          `after ${order}, the connection box ${place} is gone while a connection is being made`
        );
        assert.equal(
          running.loading,
          true,
          `after ${order}, the connection box ${place} draws Retry with loading ${String(running.loading)} while a connection is being made`
        );
        assert.equal(
          view.connectionsStarted(),
          1,
          `after ${order}, ${view.connectionsStarted()} connections are being made at once`
        );

        view.connect();
        for (let wait = 0; wait < 5; wait += 1) await turn();
        assert.equal(
          view.sessionLookups(),
          1,
          `after ${order}, the connection that answered was followed by ${view.sessionLookups()} session lookups, so a second run started beside the first`
        );
        assert.equal(
          view.retryButtons().length,
          0,
          'the connection box stays after the connection was made'
        );
      } finally {
        console.error = originalError;
        delete globalThis.document;
      }
    });
  }
}
