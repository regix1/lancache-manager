import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import { moduleUrl } from './transpile-module.mjs';

/**
 * Two windows of one browser share one session cookie, and that session stays live. While the
 * database is down, the server cannot look the session up, so every request is treated as signed
 * out: the status read answers 200 signed out (`SessionAuthenticationHandler.cs:93-110` returns no
 * result, and `AuthController.cs:92` answers with the null session), and a hub negotiate gets a 401.
 * Sockets that are already open stay open. When an API call in window A gets a 401 during that
 * outage, window A reads its status and is told it is signed out. Window B is still signed in on
 * the live session with its socket open, and it must stay that way: after the database is back,
 * window B shows the session the cookie names, with a live socket, and no reload.
 *
 * Each window runs the shipped SignalR provider, auth provider and auth service, each compiled once
 * per window so the two hold separate state the way two browser windows do, with its own `window`,
 * `document` and `fetch`; the windows share the cookie, `localStorage` and `BroadcastChannel`, as
 * windows of one browser do. The hub connection is a fake that behaves as `@microsoft/signalr`
 * 8.0.29 does on the paths it reaches. The harness follows the one in
 * `test-other-window-follows-new-session.mjs`, which does not export it.
 */

const SOURCE_ROOT = new URL('../src/', import.meta.url);
const SIGNALR_CONTEXT_FILE = new URL('contexts/SignalRContext/index.tsx', SOURCE_ROOT);
const AUTH_CONTEXT_FILE = new URL('contexts/AuthContext.tsx', SOURCE_ROOT);
const LIVE_SESSION = 'live-session';
/** One turn of the event loop, outside the clock the test mocks: a network hop or a delivery. */
const nextTurn = (callback) => setImmediate(callback);

/** Lets every queued network answer, delivery and continuation run, with the mocked clock held. */
const pump = async (turns = 12) => {
  for (let turn = 0; turn < turns; turn += 1) {
    await new Promise((resolve) => nextTurn(resolve));
  }
};

/**
 * The smallest React that runs both providers: ordered hook slots, effects with cleanup, and a
 * render loop that keeps going while a state write dirties it.
 */
const reactUrl = moduleUrl(`
let slots = null;
let cursor = 0;
let pendingEffects = [];
let currentRender = null;

export const useState = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { value: typeof initial === 'function' ? initial() : initial, render: currentRender };
  }
  const slot = slots[cursor++];
  const setValue = (update) => {
    const next = typeof update === 'function' ? update(slot.value) : update;
    if (Object.is(next, slot.value)) return;
    slot.value = next;
    slot.render();
  };
  return [slot.value, setValue];
};

export const useRef = (initial) => {
  if (!slots[cursor]) slots[cursor] = { current: initial };
  return slots[cursor++];
};

export const useCallback = (fn, deps) => {
  if (!slots[cursor]) slots[cursor] = { fn, deps };
  const slot = slots[cursor++];
  if (deps.some((value, index) => !Object.is(value, slot.deps[index]))) {
    slot.fn = fn;
    slot.deps = deps;
  }
  return slot.fn;
};

export const useMemo = (build, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { value: build(), deps };
    return slots[cursor++].value;
  }
  const slot = slots[cursor++];
  if (deps.some((value, index) => !Object.is(value, slot.deps[index]))) {
    slot.value = build();
    slot.deps = deps;
  }
  return slot.value;
};

export const useEffect = (run, deps) => {
  if (!slots[cursor]) slots[cursor] = { deps: null, ran: false, cleanup: null };
  const slot = slots[cursor++];
  const changed =
    !slot.ran || !deps || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) pendingEffects.push({ slot, run });
};

export const useLayoutEffect = useEffect;

export const createElement = (type, props, ...children) => ({ type, props, children });

export default { createElement, useState, useRef, useCallback, useMemo, useEffect, useLayoutEffect };

export const createComponent = (body) => {
  const componentSlots = [];
  const component = {};
  let dirty = false;
  let rendering = false;
  const renderOnce = () => {
    slots = componentSlots;
    cursor = 0;
    pendingEffects = [];
    currentRender = component.render;
    body();
    const effects = pendingEffects;
    pendingEffects = [];
    slots = null;
    currentRender = null;
    for (const { slot, run } of effects) {
      if (slot.cleanup) slot.cleanup();
      slot.cleanup = run() || null;
    }
  };
  component.render = () => {
    if (rendering) {
      dirty = true;
      return;
    }
    rendering = true;
    try {
      do {
        dirty = false;
        renderOnce();
      } while (dirty);
    } finally {
      rendering = false;
    }
  };
  return component;
};
`);
const { createComponent } = await import(reactUrl);

/** Modules that stand in for the browser edge, keyed by the source path they replace. */
const sourceStubs = (windowId) => ({
  i18n: moduleUrl(`export default { t: (key) => key, language: 'en', on: () => undefined };`),
  'contexts/useMockMode': moduleUrl(
    `export const useMockMode = () => ({ mockMode: false, setMockMode: () => undefined });`
  ),
  'contexts/SignalRContext/SignalRContext.types': moduleUrl(
    `export const SignalRContext = { Provider: 'signalr-context' };`
  ),
  'contexts/AuthContext.types': moduleUrl(
    `export const AuthContext = { Provider: 'auth-context' };`
  ),
  'contexts/SignalRContext/useSignalR': moduleUrl(
    `export const useSignalR = () => globalThis.signedOutAnswerTestBrowser.windows[${JSON.stringify(windowId)}].signalR;`
  )
});

/** The hub client of one window: every connection it builds is a fake the browser model drives. */
const signalRUrl = (windowId) =>
  moduleUrl(`
export const HubConnectionState = {
  Disconnected: 'Disconnected',
  Connecting: 'Connecting',
  Connected: 'Connected',
  Disconnecting: 'Disconnecting',
  Reconnecting: 'Reconnecting'
};
export const LogLevel = { Trace: 0, Debug: 1, Information: 2, Warning: 3, Error: 4, Critical: 5, None: 6 };
export const HttpTransportType = { None: 0, WebSockets: 1, ServerSentEvents: 2, LongPolling: 4 };
export class HubConnectionBuilder {
  constructor() {
    const builder = new Proxy({}, {
      get: (_target, name) =>
        name === 'build'
          ? () => globalThis.signedOutAnswerTestBrowser.server.buildConnection(${JSON.stringify(windowId)})
          : () => builder
    });
    return builder;
  }
}
`);

/** Strips the extension and a trailing index so an import and a stub key name the same module. */
const sourceKey = (url) =>
  url.href
    .slice(SOURCE_ROOT.href.length)
    .replace(/\/index(?:\.[cm]?[jt]sx?)?$/, '')
    .replace(/\.[cm]?[jt]sx?$/, '');

/** Where an import names a file under src, or null for a package. */
const sourceBase = (name, fromUrl) => {
  if (name.startsWith('.')) return new URL(name, fromUrl);
  if (name.startsWith('@/')) return new URL(name.slice(2), SOURCE_ROOT);
  if (/^@(components|services|utils|contexts|hooks|types)\//.test(name))
    return new URL(name.slice(1), SOURCE_ROOT);
  return null;
};

/** Each file's JavaScript, compiled once; only its imports and bindings differ per window. */
const transpiled = new Map();

/**
 * Compiles one source file for one window. Every file under src is compiled again for each window,
 * so each window gets its own module instances, as each browser window does; the window's own
 * `window`, `document`, `fetch`, storage and channel are bound at the top of every file.
 */
const compileForWindow = (windowId, run) => {
  const compiled = new Map();
  const stubs = sourceStubs(windowId);
  const compile = (fileUrl) => {
    if (compiled.has(fileUrl.href)) return compiled.get(fileUrl.href);
    if (!transpiled.has(fileUrl.href))
      transpiled.set(
        fileUrl.href,
        typescript
          .transpileModule(readFileSync(fileUrl, 'utf8'), {
            fileName: fileUrl.pathname,
            compilerOptions: {
              module: typescript.ModuleKind.ESNext,
              target: typescript.ScriptTarget.ES2022,
              jsx: typescript.JsxEmit.React
            }
          })
          .outputText.replaceAll('import.meta.env', '({})')
      );
    let source = transpiled.get(fileUrl.href);
    // Only real import specifiers are rewritten, from the end so earlier positions stay put.
    const specifiers = typescript
      .preProcessFile(source, true, true)
      .importedFiles.sort((left, right) => right.pos - left.pos);
    for (const { fileName: name, pos } of specifiers) {
      // `pos` is the opening quote; the specifier and its closing quote follow it.
      const end = pos + name.length + 2;
      assert.equal(source.slice(pos, end).slice(1, -1), name, `import ${name} was not found`);
      let replacement;
      if (name === 'react') replacement = reactUrl;
      else if (name === '@microsoft/signalr') replacement = signalRUrl(windowId);
      else {
        const base = sourceBase(name, fileUrl);
        if (!base) replacement = import.meta.resolve(name);
        else if (stubs[sourceKey(base)]) replacement = stubs[sourceKey(base)];
        else {
          const file = ['', '.ts', '.tsx', '/index.ts', '/index.tsx']
            .map((suffix) => new URL(base.href + suffix))
            .find(
              (candidate) => existsSync(candidate) && /\.[cm]?[jt]sx?$/.test(candidate.pathname)
            );
          assert.ok(file, `unresolved import ${name} from ${fileUrl.pathname}`);
          replacement = compile(file);
        }
      }
      source = `${source.slice(0, pos)}'${replacement}'${source.slice(end)}`;
    }
    const bound = `const browserWindow = globalThis.signedOutAnswerTestBrowser.windows[${JSON.stringify(windowId)}]; // run ${run}
const window = browserWindow.window;
const document = browserWindow.document;
const fetch = browserWindow.fetch;
const localStorage = browserWindow.window.localStorage;
const sessionStorage = browserWindow.window.sessionStorage;
const BroadcastChannel = browserWindow.window.BroadcastChannel;
${source}`;
    const url = moduleUrl(bound);
    compiled.set(fileUrl.href, url);
    return url;
  };
  return compile;
};

/** A listener registry with the browser's add, remove and dispatch. */
const eventTarget = () => {
  const listeners = new Map();
  return {
    addEventListener: (name, listener) => {
      if (!listeners.has(name)) listeners.set(name, new Set());
      listeners.get(name).add(listener);
    },
    removeEventListener: (name, listener) => listeners.get(name)?.delete(listener),
    dispatchEvent: (event) => {
      for (const listener of [...(listeners.get(event.type) ?? [])]) listener(event);
      return true;
    }
  };
};

/** A JSON answer the way `fetch` resolves one. */
const reply = (status, body) => ({
  ok: status >= 200 && status < 300,
  status,
  statusText: '',
  headers: new Headers({ 'content-type': 'application/json' }),
  json: async () => body,
  text: async () => JSON.stringify(body),
  clone: () => reply(status, body)
});

/**
 * One browser: the cookie its windows share, their shared storage and channels, and the server they
 * talk to. `server.databaseDown` is the outage; a window's `holdStatus` keeps its status reads
 * waiting at the server until the test lets them through, and each is answered from the server's
 * state at that moment.
 */
const openBrowser = () => {
  const sockets = [];
  const channels = new Set();
  const storage = new Map();
  const browser = { windows: {}, jar: LIVE_SESSION };

  /** Whether the server finds a live session behind a cookie right now. */
  const signedIn = (session) => session === LIVE_SESSION && !browser.server.databaseDown;

  const status = (session) => {
    const live = signedIn(session);
    return {
      isAuthenticated: live,
      authenticationEnabled: true,
      accountMode: 'apiKeyPassword',
      authenticationSetupRequired: false,
      signInModeFromEnvironment: false,
      oidcDisplayName: '',
      oidcPending: false,
      ownerOidcEnabled: false,
      ownerPasswordEnabled: true,
      loginServices: [],
      incompleteLoginServices: [],
      ownerLoginServices: [],
      loginSetupPending: false,
      pendingLoginKind: null,
      sessionType: live ? 'admin' : null,
      sessionId: live ? session : null,
      expiresAt: null,
      accountId: live ? `account-of-${session}` : null,
      isMainAdmin: false,
      hasData: true,
      hasBeenInitialized: true,
      hasDataLoaded: true,
      guestAccessEnabled: true,
      guestDurationHours: 6,
      prefillEnabled: false,
      prefillExpiresAt: null,
      steamPrefillEnabled: false,
      steamPrefillExpiresAt: null,
      epicPrefillEnabled: false,
      epicPrefillExpiresAt: null,
      battlenetPrefillEnabled: false,
      battlenetPrefillExpiresAt: null,
      riotPrefillEnabled: false,
      riotPrefillExpiresAt: null,
      xboxPrefillEnabled: false,
      xboxPrefillExpiresAt: null
    };
  };

  browser.server = {
    databaseDown: false,
    sockets,
    buildConnection: (windowId) => {
      const target = browser.windows[windowId];
      const connection = {
        windowId,
        state: 'Disconnected',
        connectionId: null,
        socket: null,
        handlers: new Map(),
        closeCallbacks: [],
        on: (name, handler) => {
          if (!connection.handlers.has(name)) connection.handlers.set(name, []);
          connection.handlers.get(name).push(handler);
        },
        off: (name, handler) => {
          const list = connection.handlers.get(name) ?? [];
          connection.handlers.set(
            name,
            handler ? list.filter((candidate) => candidate !== handler) : []
          );
        },
        onclose: (callback) => connection.closeCallbacks.push(callback),
        onreconnecting: () => undefined,
        onreconnected: () => undefined,
        invoke: async () => {
          if (connection.state !== 'Connected')
            throw new Error("Cannot send data if the connection is not in the 'Connected' State.");
        },
        send: async () => {
          if (connection.state !== 'Connected')
            throw new Error("Cannot send data if the connection is not in the 'Connected' State.");
        },
        start: () => {
          if (connection.state !== 'Disconnected')
            return Promise.reject(
              new Error("Cannot start a HubConnection that is not in the 'Disconnected' state.")
            );
          connection.state = 'Connecting';
          // Negotiate and the socket upgrade carry the cookie the browser holds as they are sent.
          const session = browser.jar;
          return new Promise((resolve, reject) => {
            const socket = { connection, session, open: false, pending: { resolve, reject } };
            connection.socket = socket;
            nextTurn(() => {
              if (connection.socket !== socket || !socket.pending) return;
              // DownloadHub is [Authorize]: a negotiate the server cannot match to a live session is a 401.
              if (!signedIn(session)) {
                connection.state = 'Disconnected';
                connection.socket = null;
                socket.pending = null;
                reject(
                  new Error(
                    "Failed to complete negotiation with the server: Error: Unauthorized: Status code '401'"
                  )
                );
                return;
              }
              socket.open = true;
              sockets.push(socket);
              connection.state = 'Connected';
              connection.connectionId = `${windowId}-socket-${sockets.indexOf(socket)}`;
              socket.pending = null;
              resolve();
            });
          });
        },
        stop: async () => {
          const socket = connection.socket;
          connection.socket = null;
          if (socket) socket.open = false;
          if (socket?.pending) {
            connection.state = 'Disconnected';
            const { reject } = socket.pending;
            socket.pending = null;
            const stopped = new Error(
              'The connection was stopped before the hub handshake could complete.'
            );
            stopped.name = 'AbortError';
            reject(stopped);
            return;
          }
          const wasConnected = connection.state === 'Connected';
          connection.state = 'Disconnected';
          if (wasConnected)
            for (const callback of [...connection.closeCallbacks]) callback(undefined);
        }
      };
      target.connections.push(connection);
      return connection;
    }
  };

  /** One window of this browser, with its own globals and its own copy of the app's modules. */
  browser.openWindow = async (windowId, run) => {
    const documentEvents = eventTarget();
    const windowEvents = eventTarget();
    const ownStorage = new Map();
    const target = { id: windowId, connections: [], holdStatus: false, heldStatus: [] };
    target.document = {
      hidden: false,
      visibilityState: 'visible',
      cookie: '',
      ...documentEvents
    };
    /** A `localStorage` write fires `storage` on every other window of the browser. */
    const tellOtherWindows = (key, oldValue, newValue) => {
      for (const other of Object.values(browser.windows).filter((each) => each !== target))
        nextTurn(() =>
          other.window.dispatchEvent({
            type: 'storage',
            key,
            oldValue,
            newValue,
            storageArea: other.window.localStorage
          })
        );
    };
    const storageOver = (map, shared) => ({
      get length() {
        return map.size;
      },
      key: (index) => [...map.keys()][index] ?? null,
      getItem: (key) => (map.has(key) ? map.get(key) : null),
      setItem: (key, value) => {
        const oldValue = map.has(key) ? map.get(key) : null;
        map.set(key, String(value));
        if (shared) tellOtherWindows(key, oldValue, String(value));
      },
      removeItem: (key) => {
        const oldValue = map.has(key) ? map.get(key) : null;
        map.delete(key);
        if (shared) tellOtherWindows(key, oldValue, null);
      },
      clear: () => map.clear()
    });
    class WindowChannel {
      constructor(name) {
        this.name = name;
        this.onmessage = null;
        this.events = eventTarget();
        channels.add(this);
      }
      postMessage(message) {
        for (const other of [...channels].filter(
          (channel) => channel !== this && channel.name === this.name
        ))
          nextTurn(() => {
            const event = { type: 'message', data: message };
            other.onmessage?.(event);
            other.events.dispatchEvent(event);
          });
      }
      addEventListener(name, listener) {
        this.events.addEventListener(name, listener);
      }
      removeEventListener(name, listener) {
        this.events.removeEventListener(name, listener);
      }
      close() {
        channels.delete(this);
      }
    }
    target.window = {
      ...windowEvents,
      document: target.document,
      location: {
        origin: 'http://lancache.test',
        href: 'http://lancache.test/',
        pathname: '/',
        search: '',
        hash: ''
      },
      navigator: { userAgent: 'node' },
      localStorage: storageOver(storage, true),
      sessionStorage: storageOver(ownStorage, false),
      BroadcastChannel: WindowChannel,
      setTimeout: (...args) => globalThis.setTimeout(...args),
      clearTimeout: (...args) => globalThis.clearTimeout(...args),
      setInterval: (...args) => globalThis.setInterval(...args),
      clearInterval: (...args) => globalThis.clearInterval(...args)
    };
    target.fetch = (url) => {
      // The browser attaches the cookie it holds when the request is sent.
      const cookie = browser.jar;
      const path = new URL(String(url), 'http://lancache.test').pathname;
      return new Promise((resolve) => nextTurn(resolve)).then(() => {
        if (path !== '/api/auth/status') return reply(404, {});
        if (!target.holdStatus) return reply(200, status(cookie));
        return new Promise((resolve) =>
          target.heldStatus.push(() => resolve(reply(200, status(cookie))))
        );
      });
    };
    /** Lets every status read this window has waiting at the server through, answered as of now. */
    target.releaseStatus = () => {
      target.holdStatus = false;
      for (const answer of target.heldStatus.splice(0)) answer();
    };
    target.setHidden = (hidden) => {
      target.document.hidden = hidden;
      target.document.visibilityState = hidden ? 'hidden' : 'visible';
      target.document.dispatchEvent({ type: 'visibilitychange' });
    };
    browser.windows[windowId] = target;

    const compile = compileForWindow(windowId, run);
    // Drawn in the order AppProviders.tsx nests them: the auth provider inside the SignalR one.
    const signalRContext = await import(compile(SIGNALR_CONTEXT_FILE));
    const authContext = await import(compile(AUTH_CONTEXT_FILE));
    const component = createComponent(() => {
      target.signalR = signalRContext.SignalRProvider({ children: null }).props.value;
      target.auth = authContext.AuthProvider({ children: null }).props.value;
    });
    target.render = () => component.render();
    return target;
  };

  globalThis.signedOutAnswerTestBrowser = browser;
  return browser;
};

/** Advances the mocked clock two minutes in retry-sized steps, letting each step's work finish. */
const runClock = async (t) => {
  for (let step = 0; step < 12; step += 1) {
    t.mock.timers.tick(10_000);
    await pump();
  }
};

/** What a window shows: its connection state, the sessions its open sockets carry, its identity. */
const shownBy = (browser, target) => ({
  connection: target.signalR.connectionState,
  openSockets: browser.server.sockets
    .filter(
      (socket) =>
        socket.open &&
        socket.connection.windowId === target.id &&
        socket.connection.state === 'Connected'
    )
    .map((socket) => socket.session),
  authMode: target.auth.authMode,
  sessionId: target.auth.sessionId
});

const WINDOW_B_STATES = ['visible', 'hidden until the database is back'];
const ANSWER_ORDERS = [
  "window B's status read is answered while the database is still down",
  "the database is back before window B's status read is answered"
];

let run = 0;
for (const windowBState of WINDOW_B_STATES) {
  for (const answerOrder of ANSWER_ORDERS) {
    test(`window B, ${windowBState}, stays on its live session when window A is told it is signed out during a database outage (${answerOrder})`, async (t) => {
      t.mock.method(console, 'error', () => undefined);
      t.mock.method(console, 'warn', () => undefined);
      t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
      const browser = openBrowser();
      run += 1;
      const windowA = await browser.openWindow('A', run);
      const windowB = await browser.openWindow('B', run);
      windowA.render();
      windowB.render();
      for (let step = 0; step < 3; step += 1) {
        t.mock.timers.tick(1);
        await pump();
      }

      for (const target of [windowA, windowB])
        assert.deepEqual(
          shownBy(browser, target),
          {
            connection: 'connected',
            openSockets: [LIVE_SESSION],
            authMode: 'authenticated',
            sessionId: LIVE_SESSION
          },
          `window ${target.id} did not start signed in with a live socket`
        );

      if (windowBState.startsWith('hidden')) windowB.setHidden(true);
      browser.server.databaseDown = true;
      windowB.holdStatus = true;

      // An API call in window A gets a 401 while the database is down (apiError.ts dispatches this).
      windowA.window.dispatchEvent({ type: 'auth-state-changed' });
      await pump(40);
      assert.equal(
        windowA.auth.authMode,
        'unauthenticated',
        'window A was not told it is signed out by its status read during the outage'
      );

      if (answerOrder.startsWith("window B's status read")) {
        windowB.releaseStatus();
        await pump(40);
        browser.server.databaseDown = false;
      } else {
        browser.server.databaseDown = false;
        windowB.releaseStatus();
        await pump(40);
      }
      if (windowBState.startsWith('hidden')) windowB.setHidden(false);
      await runClock(t);

      const shown = shownBy(browser, windowB);
      assert.deepEqual(
        shown,
        {
          connection: 'connected',
          openSockets: [LIVE_SESSION],
          authMode: 'authenticated',
          sessionId: LIVE_SESSION
        },
        `window B, ${windowBState} (${answerOrder}): after window A alone was told it is signed out during a database outage, window B shows connection "${shown.connection}" with open sockets on [${shown.openSockets.join(', ')}] and is ${shown.authMode} as ${shown.sessionId}, though the cookie still names the live session and the database is back, so it sits signed out with no socket until a reload`
      );
    });
  }
}
