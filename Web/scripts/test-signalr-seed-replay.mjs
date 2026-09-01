import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

/**
 * The hub sends its activity snapshot from `OnConnectedAsync`, but the socket opens on mount while
 * every consumer below the config gate is still behind a spinner, so the message used to arrive at
 * an empty handler set and vanish. This file runs the real provider - compiled against a minimal
 * React and a fake hub connection - and holds four properties together: a snapshot that arrives
 * before anyone subscribes reaches the subscriber that follows, only the newest snapshot is kept,
 * nothing is held whose handler reads it as news of a change, and neither of the two ways a
 * connection is replaced can hand the next connection something the previous one sent.
 */

const toUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/**
 * The smallest React that can run the provider: ordered slots for state, refs, memoised callbacks
 * and values, and effects with cleanup, a render loop that keeps going while a state write dirties
 * it, and an element factory that hands the test the context value the provider published.
 */
const reactStubSource = `
let slots = null;
let cursor = 0;
let queued = [];
let currentRender = null;

export const useState = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { value: initial, render: currentRender };
  }
  const slot = slots[cursor++];
  const setValue = (next) => {
    if (Object.is(next, slot.value)) return;
    slot.value = next;
    slot.render();
  };
  return [slot.value, setValue];
};

export const useRef = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { current: initial };
  }
  return slots[cursor++];
};

export const useCallback = (fn, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { fn, deps };
  }
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
  if (!slots[cursor]) {
    slots[cursor] = { deps: null, ran: false, cleanup: null };
  }
  const slot = slots[cursor++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) {
    queued.push({ slot, run });
  }
};

export const createElement = (type, props, ...children) => ({ type, props, children });

export default { createElement };

export const createComponent = (body) => {
  const componentSlots = [];
  const component = { output: null };
  let dirty = false;
  let rendering = false;

  const renderOnce = () => {
    slots = componentSlots;
    cursor = 0;
    queued = [];
    currentRender = component.render;
    component.output = body();
    const effects = queued;
    queued = [];
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
`;

const reactStubUrl = toUrl(reactStubSource);
const { createComponent } = await import(reactStubUrl);

const state = {
  isAuthenticated: true,
  connections: [],
  windowListeners: new Map()
};
globalThis.signalRSeedTestState = state;

// The provider reads page visibility on every render and subscribes to the auth event on the
// window, neither of which node supplies.
globalThis.document = {
  hidden: false,
  addEventListener: () => undefined,
  removeEventListener: () => undefined
};
globalThis.window = {
  addEventListener: (name, listener) => state.windowListeners.set(name, listener),
  removeEventListener: (name) => state.windowListeners.delete(name)
};

/**
 * A hub connection the test drives by hand: `on` collects the dispatcher the provider registers per
 * event name, the lifecycle setters collect the callbacks the reconnect test fires, and `start`
 * hands back a fresh connection id so the test can tell one connection from the next.
 */
const signalRStubSource = `
export const HubConnectionState = {
  Disconnected: 'Disconnected',
  Connecting: 'Connecting',
  Connected: 'Connected'
};

export const LogLevel = { Warning: 'Warning' };

export class HubConnectionBuilder {
  withUrl() { return this; }
  withAutomaticReconnect() { return this; }
  withServerTimeout() { return this; }
  withKeepAliveInterval() { return this; }
  configureLogging() { return this; }

  build() {
    const state = globalThis.signalRSeedTestState;
    const connection = {
      state: HubConnectionState.Disconnected,
      connectionId: null,
      dispatchers: new Map(),
      lifecycle: {},
      on: (name, dispatch) => connection.dispatchers.set(name, dispatch),
      onreconnecting: (callback) => { connection.lifecycle.reconnecting = callback; },
      onreconnected: (callback) => { connection.lifecycle.reconnected = callback; },
      onclose: (callback) => { connection.lifecycle.close = callback; },
      start: async () => {
        connection.state = HubConnectionState.Connected;
        connection.connectionId = 'connection-' + (state.connections.length);
      },
      stop: async () => { connection.state = HubConnectionState.Disconnected; }
    };
    state.connections.push(connection);
    return connection;
  }
}
`;

const authServiceStubUrl = toUrl(`
export default {
  get isAuthenticated() { return globalThis.signalRSeedTestState.isAuthenticated; }
};
`);

const constantsUrl = toUrl(`
export const SIGNALR_BASE = '/hubs';
export const APP_EVENTS = { AUTH_SESSION_UPDATED: 'auth-session-updated' };
`);

const retryPolicyStubUrl = toUrl(`
export class InfiniteBackoffRetryPolicy {
  nextRetryDelayInMilliseconds() { return 1000; }
}
`);

const contextTypesStubUrl = toUrl(`export const SignalRContext = { Provider: 'provider' };`);

const mockModeStubUrl = toUrl(`
export const useMockMode = () => ({ mockMode: false, setMockMode: () => undefined });
`);

const typesUrl = await compileToUrl('../src/contexts/SignalRContext/types.ts');

/**
 * A data-URL import resolves no path alias, so every dependency is substituted by its own URL. The
 * provider holds JSX, which the shared compiler does not emit, so it is compiled here instead.
 */
const compileProvider = () => {
  const source = readFileSync(
    new URL('../src/contexts/SignalRContext/index.tsx', import.meta.url),
    'utf8'
  );
  const compiled = ts.transpileModule(source, {
    fileName: 'index.tsx',
    compilerOptions: {
      module: ts.ModuleKind.ESNext,
      target: ts.ScriptTarget.ES2022,
      jsx: ts.JsxEmit.React
    }
  }).outputText;
  const aliasUrls = {
    react: reactStubUrl,
    '@microsoft/signalr': toUrl(signalRStubSource),
    '@utils/constants': constantsUrl,
    '@services/auth.service': authServiceStubUrl,
    '@contexts/useMockMode': mockModeStubUrl,
    './types': typesUrl,
    './SignalRContext.types': contextTypesStubUrl,
    './retryPolicy': retryPolicyStubUrl
  };
  const resolved = Object.entries(aliasUrls).reduce(
    (text, [alias, url]) => text.split(`'${alias}'`).join(`'${url}'`),
    compiled
  );
  return toUrl(resolved);
};

const { SignalRProvider } = await import(compileProvider());

/** Lets the mount timer and the awaits inside setupConnection run before the test looks. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/** Mounts one provider and waits for its first connection to be up. */
const mount = async () => {
  state.isAuthenticated = true;
  state.connections = [];
  state.windowListeners = new Map();

  const component = createComponent(() => SignalRProvider({ children: null }));
  component.render();
  await settle();

  const published = () => component.output?.props.value;
  const live = () => state.connections[state.connections.length - 1];

  return {
    published,
    live,
    /** Delivers one message from the server, the way the hub sends its connect-time snapshot. */
    emit: (eventName, message) => live().dispatchers.get(eventName)(message),
    /** Records every message a subscriber receives, and returns the list. */
    subscribe: (eventName) => {
      const received = [];
      published().on(eventName, (message) => received.push(message));
      return received;
    },
    /** The socket drops and the client reconnects itself, keeping the same connection object. */
    reconnect: () => {
      const connection = live();
      connection.lifecycle.reconnecting();
      connection.lifecycle.reconnected('connection-reconnected');
    },
    /** Auth is cleared and restored, which builds a whole new connection. */
    reauthenticate: async () => {
      const notify = state.windowListeners.get('auth-session-updated');
      state.isAuthenticated = false;
      notify();
      state.isAuthenticated = true;
      notify();
      await settle();
    }
  };
};

test('a snapshot that arrives before anyone subscribes reaches the subscriber that follows', async () => {
  const harness = await mount();
  const snapshot = { revision: 4, activities: [{ domain: 'download', key: 'a' }] };

  harness.emit('ActivityUpdated', snapshot);

  assert.deepEqual(
    harness.subscribe('ActivityUpdated'),
    [snapshot],
    'this is the seed the config gate used to eat'
  );
});

test('a subscriber that was already up gets the live message once, not twice', async () => {
  const harness = await mount();
  const received = harness.subscribe('ActivityUpdated');

  const snapshot = { revision: 5, activities: [] };
  harness.emit('ActivityUpdated', snapshot);

  assert.deepEqual(received, [snapshot], 'the held copy must not be replayed at the same handler');
});

test('only the newest snapshot is held', async () => {
  const harness = await mount();

  harness.emit('ActivityUpdated', { revision: 1, activities: [] });
  harness.emit('ActivityUpdated', { revision: 2, activities: [] });

  assert.deepEqual(
    harness.subscribe('ActivityUpdated').map((message) => message.revision),
    [2],
    'replaying the older snapshot after the newer one would report state that already moved'
  );
});

test('an event that is not a snapshot is never held', async () => {
  const harness = await mount();

  harness.emit('DownloadsRefresh', { reason: 'ingest' });

  assert.deepEqual(
    harness.subscribe('DownloadsRefresh'),
    [],
    'DownloadsRefresh says something changed, so replaying it later is news that already broke'
  );
});

test('a preference or refresh-rate message is never held', async () => {
  const harness = await mount();
  const cancels = [
    ['UserPreferencesUpdated', { sessionId: 'a', preferences: { selectedTheme: 'dark-default' } }],
    ['DefaultGuestRefreshRateChanged', { refreshRate: 'STANDARD' }],
    ['GuestRefreshRateLockChanged', { locked: true }],
    ['GuestRefreshRateUpdated', { sessionId: 'a', refreshRate: 'SLOW' }]
  ];

  for (const [eventName, message] of cancels) {
    harness.emit(eventName, message);
  }

  for (const [eventName] of cancels) {
    assert.deepEqual(
      harness.subscribe(eventName),
      [],
      `${eventName} cancels the mount fetch its own context makes, so a replay installs the older value`
    );
  }
});

test('a reconnect does not hand the new connection what the old one sent', async () => {
  const harness = await mount();
  harness.emit('ActivityUpdated', { revision: 7, activities: [] });

  harness.reconnect();

  assert.deepEqual(
    harness.subscribe('ActivityUpdated'),
    [],
    'the server seeds the reconnected client itself, and that snapshot is the current one'
  );
});

test('a rebuilt connection does not hand the new connection what the old one sent', async () => {
  const harness = await mount();
  harness.emit('ActivityUpdated', { revision: 8, activities: [] });

  await harness.reauthenticate();

  assert.equal(state.connections.length, 2, 'auth clear and restore builds a second connection');
  assert.deepEqual(harness.subscribe('ActivityUpdated'), []);

  const snapshot = { revision: 9, activities: [] };
  harness.emit('ActivityUpdated', snapshot);
  assert.deepEqual(
    harness.subscribe('ActivityUpdated'),
    [snapshot],
    'the new connection fills the hold again from its own seed'
  );
});
