import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import { moduleUrl } from './transpile-module.mjs';

/**
 * Two windows of one browser share one session cookie. Window A has a prefill page open, so it holds
 * a prefill hub socket next to its main hub socket. When a sign-in in window B ends the session the
 * browser held, the server aborts every socket of that session on every hub
 * (`ConnectionTrackingService.cs:107-128`; `SessionHubFilter` registers the prefill hubs too), and
 * only then answers B with the new cookie. Window A's prefill page must end up on a live prefill hub
 * socket opened with the browser's new session, with no reload, so live progress, logs and the
 * session-ended event keep reaching it and Start is not left disabled.
 *
 * The abort's Close has no `allowReconnect`, so `@microsoft/signalr` 8.0.29 goes Disconnected and
 * calls only its `onclose` callbacks (`dist/esm/HubConnection.js:475-487`, `:650-652`,
 * `:664-676`); `withAutomaticReconnect` never starts. Both hubs are `[Authorize]`, so a start with
 * the ended session's cookie gets a 401 at negotiate.
 *
 * Each window runs the shipped SignalR provider, auth provider and auth service, and window A also
 * runs the shipped `usePrefillSignalR` hook, each compiled once per window so the two windows hold
 * separate state, with its own `window`, `document` and `fetch`; the windows share the cookie,
 * `localStorage` and `BroadcastChannel`. Window B signs in through its own provider. The harness
 * follows the one in `test-other-window-follows-new-session.mjs`, which does not export it.
 */

const SOURCE_ROOT = new URL('../src/', import.meta.url);
const SIGNALR_CONTEXT_FILE = new URL('contexts/SignalRContext/index.tsx', SOURCE_ROOT);
const AUTH_CONTEXT_FILE = new URL('contexts/AuthContext.tsx', SOURCE_ROOT);
const PREFILL_HOOK_FILE = new URL(
  'components/features/prefill/hooks/usePrefillSignalR.ts',
  SOURCE_ROOT
);
const HELD_SESSION = 'held-session';
const NEW_SESSION = 'new-session';
const MAIN_HUB = 'main hub';
const PREFILL_HUB = 'prefill hub';
/** One turn of the event loop, outside the clock the test mocks: a network hop or a delivery. */
const nextTurn = (callback) => setImmediate(callback);

/** Lets every queued network answer, delivery and continuation run, with the mocked clock held. */
const pump = async (turns = 12) => {
  for (let turn = 0; turn < turns; turn += 1) {
    await new Promise((resolve) => nextTurn(resolve));
  }
};

/**
 * The smallest React that runs the providers and the hook: ordered hook slots, effects with
 * cleanup, and a render loop that keeps going while a state write dirties it.
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

/**
 * Modules that stand in for the browser edge, keyed by the source path they replace. The prefill
 * service config only names the service's display key and icon, and the notification context is
 * the popup the hook reports a failed create to; neither decides when a hub connects.
 */
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
    `export const useSignalR = () => globalThis.prefillHubTestBrowser.windows[${JSON.stringify(windowId)}].signalR;`
  ),
  'contexts/notifications': moduleUrl(
    `export const useNotifications = () => ({ addNotification: () => undefined });`
  ),
  'components/features/prefill/hooks/prefillServiceConfig': moduleUrl(
    `export const prefillServiceConfig = () => ({ serviceNameKey: 'prefill.services.steam' });`
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
    let hubUrl = null;
    const builder = new Proxy({}, {
      get: (_target, name) => {
        if (name === 'withUrl') return (url) => { hubUrl = url; return builder; };
        if (name === 'build')
          return () => globalThis.prefillHubTestBrowser.server.buildConnection(${JSON.stringify(windowId)}, hubUrl);
        return () => builder;
      }
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
    const bound = `const browserWindow = globalThis.prefillHubTestBrowser.windows[${JSON.stringify(windowId)}]; // run ${run}
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

/** What `start()` throws when the connection closes before the handshake answer is handled. */
const handshakeStopped = () => {
  const stopped = new Error('The connection was stopped before the hub handshake could complete.');
  stopped.name = 'AbortError';
  return stopped;
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
 * One browser: the cookie jar its windows share, their shared storage and channels, and the server
 * they talk to. `eventOrder` is the order of the abort and `UserSessionRevoked`, `abortOrder` which
 * hub's socket the server aborts first, `cookieTiming` whether the new cookie lands before window A
 * reacts to the abort.
 */
const openBrowser = ({ eventOrder, abortOrder, cookieTiming }) => {
  const sockets = [];
  const live = new Set([HELD_SESSION]);
  const channels = new Set();
  const storage = new Map();
  const browser = { windows: {}, jar: HELD_SESSION };

  const status = (session) => {
    const signedIn = session !== null && live.has(session);
    return {
      isAuthenticated: signedIn,
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
      sessionType: signedIn ? 'admin' : null,
      sessionId: signedIn ? session : null,
      expiresAt: null,
      accountId: signedIn ? `account-of-${session}` : null,
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

  /** The client handles a Close without allowReconnect: Disconnected, then `onclose`, no reconnect. */
  const deliverClose = (socket) => {
    const { connection } = socket;
    if (connection.socket !== socket || connection.state !== 'Connected') return;
    connection.state = 'Disconnected';
    connection.socket = null;
    for (const callback of [...connection.closeCallbacks]) callback(undefined);
  };

  /** `Context.Abort()` on one registered socket (`ConnectionTrackingService.DisconnectSession`). */
  const abort = (socket) => {
    socket.open = false;
    nextTurn(() => deliverClose(socket));
  };

  /** `NotifyAllAsync` on the main hub: every main hub socket the server still holds open gets it. */
  const broadcast = (eventName, message) => {
    for (const socket of sockets.filter(
      (candidate) => candidate.open && candidate.connection.hub === MAIN_HUB
    )) {
      nextTurn(() => {
        const { connection } = socket;
        if (connection.socket !== socket || connection.state !== 'Connected') return;
        for (const handler of [...(connection.handlers.get(eventName) ?? [])]) handler(message);
      });
    }
  };

  /** `POST /api/auth/login`: a new session, then the held one ended, then the answer. */
  const signIn = async (heldCookie) => {
    live.add(NEW_SESSION);
    live.delete(heldCookie);
    const first = abortOrder === 'the prefill socket aborted first' ? PREFILL_HUB : MAIN_HUB;
    const closeSockets = () => {
      const held = sockets.filter((socket) => socket.open && socket.session === heldCookie);
      for (const socket of [
        ...held.filter((each) => each.connection.hub === first),
        ...held.filter((each) => each.connection.hub !== first)
      ])
        abort(socket);
    };
    const sendRevoked = () =>
      broadcast('UserSessionRevoked', { sessionId: heldCookie, sessionType: 'admin' });
    if (eventOrder === 'the close, then UserSessionRevoked') {
      closeSockets();
      sendRevoked();
    } else {
      sendRevoked();
      closeSockets();
    }
    if (cookieTiming === 'window A reacts before the new cookie lands') await pump();
    browser.jar = NEW_SESSION;
    return { success: true, sessionType: 'admin', expiresAt: '2099-01-01T00:00:00Z' };
  };

  /** What the prefill hub answers: this browser has no prefill session yet. */
  const prefillInvoke = (method) => (method === 'GetMySessions' ? [] : undefined);

  browser.server = {
    sockets,
    buildConnection: (windowId, hubUrl) => {
      const target = browser.windows[windowId];
      const hub = String(hubUrl).endsWith('/downloads') ? MAIN_HUB : PREFILL_HUB;
      const connection = {
        windowId,
        hub,
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
        invoke: async (method) => {
          if (connection.state !== 'Connected')
            throw new Error("Cannot send data if the connection is not in the 'Connected' State.");
          await new Promise((resolve) => nextTurn(resolve));
          return hub === PREFILL_HUB ? prefillInvoke(method) : undefined;
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
              if (!live.has(session)) {
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
            reject(handshakeStopped());
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
  browser.openWindow = async (windowId, run, { prefillPage }) => {
    const documentEvents = eventTarget();
    const windowEvents = eventTarget();
    const ownStorage = new Map();
    const target = { id: windowId, connections: [] };
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
    target.fetch = (url, init = {}) => {
      // The browser attaches the cookie it holds when the request is sent.
      const cookie = browser.jar;
      const path = new URL(String(url), 'http://lancache.test').pathname;
      return new Promise((resolve) => nextTurn(resolve)).then(async () => {
        if (path === '/api/auth/status') return reply(200, status(cookie));
        if (path === '/api/auth/login' && init.method === 'POST')
          return reply(200, await signIn(cookie));
        return reply(404, {});
      });
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
    const appShell = createComponent(() => {
      target.signalR = signalRContext.SignalRProvider({ children: null }).props.value;
      target.auth = authContext.AuthProvider({ children: null }).props.value;
    });
    // The prefill page draws inside the providers, so it renders after them.
    const prefillHook = prefillPage ? await import(compile(PREFILL_HOOK_FILE)) : null;
    const prefillPanel = prefillHook
      ? createComponent(() => {
          target.prefill = prefillHook.usePrefillSignalR({
            addLog: () => undefined,
            onAuthStateChanged: () => undefined,
            clearAllPrefillStorage: () => undefined,
            onSessionEnd: () => undefined,
            hubPath: '/steam-daemon',
            serviceId: 'steam'
          });
        })
      : null;
    target.render = () => {
      appShell.render();
      prefillPanel?.render();
    };
    return target;
  };

  globalThis.prefillHubTestBrowser = browser;
  return browser;
};

/** Advances the mocked clock two minutes in retry-sized steps, letting each step's work finish. */
const runClock = async (t) => {
  for (let step = 0; step < 12; step += 1) {
    t.mock.timers.tick(10_000);
    await pump();
  }
};

/** What window A's prefill page shows: whether it is connected, and the sessions its open prefill sockets carry. */
const prefillShownBy = (browser, target) => ({
  connected: target.prefill.isConnected,
  connectFailed: target.prefill.hubConnectFailed,
  openPrefillSockets: browser.server.sockets
    .filter(
      (socket) =>
        socket.open &&
        socket.connection.windowId === target.id &&
        socket.connection.hub === PREFILL_HUB &&
        socket.connection.state === 'Connected'
    )
    .map((socket) => socket.session)
});

const WINDOW_STATES = ['visible', 'hidden while window B signs in'];
const EVENT_ORDERS = ['the close, then UserSessionRevoked', 'UserSessionRevoked, then the close'];
const ABORT_ORDERS = ['the main socket aborted first', 'the prefill socket aborted first'];
const COOKIE_TIMINGS = [
  'the new cookie lands before window A reacts',
  'window A reacts before the new cookie lands'
];

let run = 0;
for (const windowState of WINDOW_STATES) {
  for (const eventOrder of EVENT_ORDERS) {
    for (const abortOrder of ABORT_ORDERS) {
      for (const cookieTiming of COOKIE_TIMINGS) {
        test(`window A's prefill page, ${windowState}, follows the browser to its new session when window B signs in (${eventOrder}; ${abortOrder}; ${cookieTiming})`, async (t) => {
          t.mock.method(console, 'error', () => undefined);
          t.mock.method(console, 'warn', () => undefined);
          t.mock.timers.enable({ apis: ['setTimeout', 'setInterval'] });
          const browser = openBrowser({ eventOrder, abortOrder, cookieTiming });
          run += 1;
          const windowA = await browser.openWindow('A', run, { prefillPage: true });
          const windowB = await browser.openWindow('B', run, { prefillPage: false });
          windowA.render();
          windowB.render();
          for (let step = 0; step < 3; step += 1) {
            t.mock.timers.tick(1);
            await pump();
          }

          assert.equal(windowA.auth.sessionId, HELD_SESSION, 'window A did not start signed in');
          assert.deepEqual(
            prefillShownBy(browser, windowA),
            { connected: true, connectFailed: false, openPrefillSockets: [HELD_SESSION] },
            "window A's prefill page did not start connected"
          );
          if (windowState.startsWith('hidden')) windowA.setHidden(true);

          const signingIn = windowB.auth.login('key', 'colleague', 'Correct-Horse-9');
          await pump(40);
          const result = await signingIn;
          assert.equal(result.success, true, 'window B could not sign in');
          if (windowState.startsWith('hidden')) windowA.setHidden(false);
          await runClock(t);

          assert.equal(
            windowB.auth.sessionId,
            NEW_SESSION,
            'window B does not hold the new session'
          );
          const shown = prefillShownBy(browser, windowA);
          assert.deepEqual(
            shown,
            { connected: true, connectFailed: false, openPrefillSockets: [NEW_SESSION] },
            `window A's prefill page, ${windowState} (${eventOrder}; ${abortOrder}; ${cookieTiming}): after window B's sign-in ended the session this browser held, the prefill page shows connected ${shown.connected} and connect failed ${shown.connectFailed} with open prefill sockets on [${shown.openPrefillSockets.join(', ')}], so live prefill progress, logs and the session-ended event stop reaching it until a reload`
          );
        });
      }
    }
  }
}
