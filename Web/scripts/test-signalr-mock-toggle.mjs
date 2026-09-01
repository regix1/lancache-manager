import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * Mock mode promises that nothing reaches the server, and the socket used to ignore it because the
 * provider read a prop nobody passed. Wiring that prop up on its own makes things worse: the effect
 * cleanup used to empty the handler registry, and `on` is a stable callback, so every consumer's own
 * effect stays put and nothing re-subscribes. This file runs the real provider - compiled against a
 * minimal React and a fake hub connection - and holds the lifecycle properties that separates the
 * connection from the registry: the registry outlives a toggle, the reconnected socket serves it,
 * nothing from the server reaches a subscriber while the toggle is on, repeated and mid-handshake
 * toggles leave exactly one connection, and a Strict Mode double invoke still opens one.
 */

/**
 * The smallest React that can run the provider: ordered slots for state, refs, memoised callbacks
 * and values, and effects with cleanup, a render loop that keeps going while a state write dirties
 * it, and an element factory that hands the test the context value the provider published.
 * `strictRemount` replays what React Strict Mode does on mount - every effect torn down and run a
 * second time before any timer fires.
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
    slots[cursor] = { isEffect: true, deps: null, ran: false, cleanup: null, run: null };
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
      slot.run = run;
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

  component.strictRemount = () => {
    for (const slot of componentSlots) {
      if (!slot.isEffect || !slot.run) continue;
      if (slot.cleanup) slot.cleanup();
      slot.cleanup = slot.run() || null;
    }
  };

  return component;
};
`;

const reactStubUrl = moduleUrl(reactStubSource);
const { createComponent } = await import(reactStubUrl);

const CONNECTED = 'Connected';
const DISCONNECTED = 'Disconnected';

const state = {
  isAuthenticated: true,
  mockMode: false,
  connections: [],
  windowListeners: new Map(),
  /** When set, the hub handshake waits on it so a test can flip mock mode part-way through. */
  startGate: null
};
globalThis.signalRMockToggleTestState = state;

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
 * event name, `start` optionally waits on the gate so the test can act mid-handshake, and `stop`
 * counts, so a test can tell a socket that was opened and thrown away from one never opened.
 */
const signalRStubSource = `
export const HubConnectionState = {
  Disconnected: '${DISCONNECTED}',
  Connecting: 'Connecting',
  Connected: '${CONNECTED}'
};

export const LogLevel = { Warning: 'Warning' };

export class HubConnectionBuilder {
  withUrl() { return this; }
  withAutomaticReconnect() { return this; }
  withServerTimeout() { return this; }
  withKeepAliveInterval() { return this; }
  configureLogging() { return this; }

  build() {
    const state = globalThis.signalRMockToggleTestState;
    const connection = {
      state: HubConnectionState.Disconnected,
      connectionId: null,
      stopCount: 0,
      dispatchers: new Map(),
      lifecycle: {},
      on: (name, dispatch) => connection.dispatchers.set(name, dispatch),
      onreconnecting: (callback) => { connection.lifecycle.reconnecting = callback; },
      onreconnected: (callback) => { connection.lifecycle.reconnected = callback; },
      onclose: (callback) => { connection.lifecycle.close = callback; },
      start: async () => {
        if (state.startGate) await state.startGate;
        connection.state = HubConnectionState.Connected;
        connection.connectionId = 'connection-' + state.connections.length;
      },
      stop: async () => {
        connection.stopCount += 1;
        connection.state = HubConnectionState.Disconnected;
      }
    };
    state.connections.push(connection);
    return connection;
  }
}
`;

const authServiceStubUrl = moduleUrl(`
export default {
  get isAuthenticated() { return globalThis.signalRMockToggleTestState.isAuthenticated; }
};
`);

const mockModeStubUrl = moduleUrl(`
export const useMockMode = () => ({
  mockMode: globalThis.signalRMockToggleTestState.mockMode,
  setMockMode: () => undefined
});
`);

const constantsUrl = moduleUrl(`
export const SIGNALR_BASE = '/hubs';
export const APP_EVENTS = { AUTH_SESSION_UPDATED: 'auth-session-updated' };
`);

const retryPolicyStubUrl = moduleUrl(`
export class InfiniteBackoffRetryPolicy {
  nextRetryDelayInMilliseconds() { return 1000; }
}
`);

const contextTypesStubUrl = moduleUrl(`export const SignalRContext = { Provider: 'provider' };`);

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
    '@microsoft/signalr': moduleUrl(signalRStubSource),
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
  return moduleUrl(resolved);
};

const { SignalRProvider } = await import(compileProvider());

/** Lets the mount timer and the awaits inside setupConnection run before the test looks. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/**
 * Mounts one provider. Nothing is awaited here, so a test can act between the render and the timer
 * that opens the socket. `holdStart` leaves the handshake unfinished until `releaseStart`.
 */
const mount = ({ holdStart = false } = {}) => {
  state.isAuthenticated = true;
  state.mockMode = false;
  state.connections = [];
  state.windowListeners = new Map();
  state.startGate = null;

  let openHandshake = () => undefined;
  if (holdStart) {
    state.startGate = new Promise((resolve) => {
      openHandshake = resolve;
    });
  }

  const component = createComponent(() => SignalRProvider({ children: null }));
  component.render();

  const published = () => component.output?.props.value;

  return {
    published,
    /** The most recently built connection. */
    live: () => state.connections[state.connections.length - 1],
    /** Every connection the provider has built, oldest first. */
    all: () => state.connections,
    /** Flips the toggle the way MockModeProvider does: new value, then a re-render. */
    setMockMode: (next) => {
      state.mockMode = next;
      component.render();
    },
    /** Lets a held handshake finish. */
    releaseStart: () => {
      state.startGate = null;
      openHandshake();
    },
    /** React Strict Mode's second pass: every effect torn down and run again. */
    strictRemount: () => component.strictRemount(),
    /** Delivers one message from the server on the connection the caller names. */
    emit: (connection, eventName, message) => connection.dispatchers.get(eventName)(message),
    /** Records every message a subscriber receives, and returns the list. */
    subscribe: (eventName) => {
      const received = [];
      published().on(eventName, (message) => received.push(message));
      return received;
    }
  };
};

test('a handler subscribed before the toggle is still served after mock mode goes off again', async () => {
  const harness = mount();
  await settle();
  const first = harness.live();
  const received = harness.subscribe('DownloadsRefresh');

  harness.setMockMode(true);
  assert.equal(first.state, DISCONNECTED, 'the socket has to be closed while the toggle is on');
  assert.equal(harness.published().isConnected, false);

  harness.setMockMode(false);
  await settle();
  const second = harness.live();
  assert.equal(harness.all().length, 2, 'switching mock mode off builds a fresh connection');
  assert.equal(second.state, CONNECTED);
  assert.equal(harness.published().isConnected, true);

  const message = { reason: 'ingest' };
  harness.emit(second, 'DownloadsRefresh', message);
  assert.deepEqual(
    received,
    [message],
    'the consumer never re-subscribes - `on` is stable - so emptying the registry loses it forever'
  );
});

test('nothing the server sends reaches a subscriber while mock mode is on', async () => {
  const harness = mount();
  await settle();
  const first = harness.live();
  const received = harness.subscribe('DownloadSpeedUpdate');

  harness.setMockMode(true);
  harness.emit(first, 'DownloadSpeedUpdate', { clientIp: '10.0.0.5', bytesPerSecond: 1024 });

  assert.deepEqual(received, [], 'closing the socket is a round trip, so a message can still land');
});

test('a snapshot held before the toggle is not handed to a consumer that subscribes in mock mode', async () => {
  const harness = mount();
  await settle();
  harness.emit(harness.live(), 'ActivityUpdated', { revision: 3, activities: [] });

  harness.setMockMode(true);

  assert.deepEqual(
    harness.subscribe('ActivityUpdated'),
    [],
    'the held snapshot is real server state, and `on` replays it to whoever subscribes next'
  );
});

test('toggling repeatedly leaves one open connection and the handler still subscribed', async () => {
  const harness = mount();
  await settle();
  const received = harness.subscribe('DownloadsRefresh');

  for (let round = 0; round < 3; round += 1) {
    harness.setMockMode(true);
    harness.setMockMode(false);
    await settle();
  }

  assert.equal(harness.all().length, 4, 'one on mount and one for each switch back off');
  assert.deepEqual(
    harness.all().map((connection) => connection.state),
    [DISCONNECTED, DISCONNECTED, DISCONNECTED, CONNECTED],
    'every connection but the newest is closed'
  );

  harness.emit(harness.live(), 'DownloadsRefresh', { reason: 'ingest' });
  assert.equal(received.length, 1, 'three round trips through mock mode kept the subscription');
});

test('flipping the toggle back and forth faster than the socket opens builds no spare connection', async () => {
  const harness = mount();
  await settle();

  harness.setMockMode(true);
  harness.setMockMode(false);
  harness.setMockMode(true);
  harness.setMockMode(false);
  await settle();

  assert.equal(
    harness.all().length,
    2,
    'the pending open from the first switch off is canceled by the next switch on'
  );
  assert.equal(harness.live().state, CONNECTED);
  assert.equal(harness.published().isConnected, true);
});

test('mock mode switched on mid-handshake does not adopt the socket that finishes opening', async () => {
  const harness = mount({ holdStart: true });
  await settle();
  const opening = harness.live();
  assert.equal(opening.state, DISCONNECTED, 'the handshake is still in flight');

  harness.setMockMode(true);
  harness.releaseStart();
  await settle();

  assert.equal(opening.stopCount, 1, 'the socket that finished opening has to be closed again');
  assert.equal(harness.published().isConnected, false);
});

test('mock mode switched on and back off mid-handshake leaves one open socket, not two', async () => {
  const harness = mount({ holdStart: true });
  await settle();
  const received = harness.subscribe('DownloadsRefresh');

  harness.setMockMode(true);
  harness.setMockMode(false);
  await settle();

  harness.releaseStart();
  await settle();
  await settle();

  assert.equal(harness.all().length, 2, 'the switch back off starts a second socket');
  const open = harness.all().filter((connection) => connection.state === CONNECTED);
  assert.equal(open.length, 1, 'the socket that lost the race has to be closed, not left open');
  assert.equal(
    harness.all().find((connection) => connection.state === DISCONNECTED).stopCount,
    1,
    'whichever socket finished second closes itself'
  );
  assert.equal(harness.published().isConnected, true);

  harness.emit(open[0], 'DownloadsRefresh', { reason: 'ingest' });
  assert.equal(received.length, 1, 'one message from the server reaches a subscriber once');
});

test('a Strict Mode double invoke opens exactly one connection', async () => {
  const harness = mount();
  harness.strictRemount();
  await settle();

  assert.equal(harness.all().length, 1, 'the torn-down first pass must not leave a socket behind');
  assert.equal(harness.live().state, CONNECTED);
});
