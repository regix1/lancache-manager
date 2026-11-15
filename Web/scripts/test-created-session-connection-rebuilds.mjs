import assert from 'node:assert/strict';
import test, { mock } from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * The prefill page connects to its hub on mount. When that first connection fails, the connection
 * box offers Retry, and pressing Start opens the connection instead (`createSession`). The server
 * accepts that connection and creates the session on it. When the server later closes it (a host
 * restart, the session's cookie replaced in another window), the page treats it like any accepted
 * connection the server ended: no Retry box, and the connection is built again after 10 s.
 *
 * The shipped hook runs here on a small hook store that keeps effect and callback dependencies, the
 * way `test-retry-sites-keep-error-box.mjs` drives it (its harness is private to it), with a fake hub
 * whose connections the test can close the way the server does. The 10 s wait runs on a mocked
 * `setTimeout`.
 */

const PREFILL_HOOK = 'src/components/features/prefill/hooks/usePrefillSignalR.ts';
const REBUILD_AFTER_MS = 10_000;

/** Draws nothing; stands in for any import the test does not bind itself. */
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

const turn = () => new Promise((resolve) => setImmediate(resolve));

/** Whether a dependency list changed since the last render. */
const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

/** One mounted hook's state; `render` runs it and the effects whose dependencies moved. */
const hookStore = () => {
  const slots = [];
  let cursor = 0;
  let effects = [];
  const hooks = {
    useState: (initial) => {
      const slot = (slots[cursor++] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => (slots[cursor++] ??= { current: initial }),
    useMemo: (build, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!('value' in slot) || changed(slot.deps, deps)) {
        slot.value = build();
        slot.deps = deps;
      }
      return slot.value;
    },
    useCallback: (callback, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.callback || changed(slot.deps, deps)) {
        slot.callback = callback;
        slot.deps = deps;
      }
      return slot.callback;
    },
    useEffect: (effect, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.ran || changed(slot.deps, deps)) {
        slot.ran = true;
        slot.deps = deps;
        effects.push({ slot, effect });
      }
    }
  };
  hooks.useLayoutEffect = hooks.useEffect;
  const render = (run) => {
    cursor = 0;
    effects = [];
    const value = run();
    for (const { slot, effect } of effects) {
      if (typeof slot.cleanup === 'function') slot.cleanup();
      slot.cleanup = effect();
    }
    return value;
  };
  return { hooks, render };
};

const CREATED_SESSION = {
  id: 'created-session',
  status: 'Active',
  authState: 'NotAuthenticated',
  containerName: 'steam-prefill',
  expiresAt: new Date(Date.now() + 3_600_000).toISOString()
};

/** The prefill hook on a hub whose first connection fails and whose later ones the server accepts. */
const mountPrefill = () => {
  const store = hookStore();
  const built = [];
  const connection = () => {
    const closeHandlers = [];
    const hub = {
      state: 'Disconnected',
      start: async () => {
        if (built.length === 1) throw new Error('The hub did not answer');
        hub.state = 'Connected';
      },
      stop: async () => {
        hub.state = 'Disconnected';
      },
      invoke: async (method) => {
        if (method === 'GetMySessions') return [];
        if (method === 'OpenSessionAsync')
          return { session: CREATED_SESSION, stageKey: null, context: null };
        return undefined;
      },
      on: () => undefined,
      off: () => undefined,
      onreconnecting: () => undefined,
      onreconnected: () => undefined,
      onclose: (handler) => {
        closeHandlers.push(handler);
      },
      /** The server ends the connection; the client is not allowed to reconnect it itself. */
      closeFromServer: () => {
        hub.state = 'Disconnected';
        closeHandlers.forEach((handler) => handler());
      }
    };
    built.push(hub);
    return hub;
  };
  class HubSetup {
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
    HubConnectionBuilder: HubSetup,
    HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
    LogLevel: { Information: 2 },
    SIGNALR_BASE: '/hubs',
    STORAGE_KEYS: { PREFILL_SESSION_ID: 'prefill-session' },
    APP_EVENTS: { AUTH_SESSION_UPDATED: 'auth-session-updated' },
    sessionStore: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined },
    i18n: { t: (key) => key },
    getErrorMessage: (error) => error.message,
    formatTimeRemaining: (seconds) => `${seconds} s`,
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
  const page = () => store.render(() => usePrefillSignalR(options));
  const settle = async () => {
    for (let step = 0; step < 8; step += 1) {
      await turn();
      page();
    }
  };
  return { page, settle, built };
};

test('a connection Start opened after a failed first connect is rebuilt when the server closes it', async () => {
  const consoleError = console.error;
  const consoleWarn = console.warn;
  console.error = () => undefined;
  console.warn = () => undefined;
  globalThis.window = { addEventListener: () => undefined, removeEventListener: () => undefined };
  globalThis.document = {
    hidden: false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined
  };
  mock.timers.enable({ apis: ['setTimeout'] });
  const wrong = [];
  try {
    const prefill = mountPrefill();
    prefill.page();
    await prefill.settle();
    assert.equal(prefill.page().hubConnectFailed, true, 'the failed first connect shows no Retry');

    // Start opens the connection, and the server creates the session on it.
    await prefill.page().createSession(() => undefined);
    await prefill.settle();
    const started = prefill.page();
    assert.equal(started.session?.id, CREATED_SESSION.id, 'Start did not create the session');
    assert.equal(started.isConnected, true, 'the connection Start opened is not connected');
    assert.equal(started.hubConnectFailed, false, 'the connection Start opened still shows Retry');

    // The server ends that connection.
    const builtBeforeClose = prefill.built.length;
    prefill.built.at(-1).closeFromServer();
    await prefill.settle();
    if (prefill.page().hubConnectFailed) wrong.push('the page shows the connection box with Retry');

    mock.timers.tick(REBUILD_AFTER_MS);
    await prefill.settle();
    if (prefill.built.length === builtBeforeClose)
      wrong.push(`no new connection was started ${REBUILD_AFTER_MS / 1000} s after the close`);
  } finally {
    mock.timers.reset();
    console.error = consoleError;
    console.warn = consoleWarn;
    delete globalThis.window;
    delete globalThis.document;
  }

  assert.deepEqual(
    wrong,
    [],
    `after the server closed the connection Start opened: ${wrong.join('; ')}`
  );
});
