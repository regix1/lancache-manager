import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { HttpClient, HttpResponse } from '@microsoft/signalr';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  loadNotificationModules,
  parseSource
} from './transpile-module.mjs';

/**
 * The hub sends its activity snapshot from `OnConnectedAsync`, but the socket opens on mount while
 * every consumer below the config gate is still behind a spinner, so the message used to arrive at
 * an empty handler set and vanish. This file runs the real provider - compiled against a minimal
 * React and a fake hub connection - and holds four properties together: a snapshot that arrives
 * before anyone subscribes reaches the subscriber that follows, only the newest snapshot is kept,
 * nothing is held whose handler reads it as news of a change, and neither of the two ways a
 * connection is replaced can hand the next connection something the previous one sent.
 *
 * The last two cases swap the stub for the installed client and show why the server's Close must
 * allow reconnecting: that Close takes the page through reconnecting to a new connection and a
 * reload of the run list, while a Close without it leaves the page on its outage state with no new
 * connection and no reload.
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
const compileProvider = (signalRUrl, retryPolicyUrl) => {
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
    '@microsoft/signalr': signalRUrl,
    '@utils/constants': constantsUrl,
    '@services/auth.service': authServiceStubUrl,
    '@contexts/useMockMode': mockModeStubUrl,
    './types': typesUrl,
    './SignalRContext.types': contextTypesStubUrl,
    './retryPolicy': retryPolicyUrl
  };
  const resolved = Object.entries(aliasUrls).reduce(
    (text, [alias, url]) => text.split(`'${alias}'`).join(`'${url}'`),
    compiled
  );
  return toUrl(resolved);
};

const { SignalRProvider } = await import(
  compileProvider(toUrl(signalRStubSource), retryPolicyStubUrl)
);

/** Lets the mount timer and the awaits inside setupConnection run before the test looks. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/** Mounts one provider and waits for its first connection to be up. */
const mount = async (body = () => SignalRProvider({ children: null })) => {
  state.isAuthenticated = true;
  state.connections = [];
  state.windowListeners = new Map();

  const component = createComponent(body);
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

/**
 * The installed client in place of the stub. A data-URL module cannot import a package by name, so
 * this one imports the URL node resolves the name to. The hub path is made absolute because node
 * has no page to resolve it against, and every built connection is kept so the test can stop it:
 * its keep-alive and server-timeout timers would otherwise hold node open.
 */
const installedClientUrl = import.meta.resolve('@microsoft/signalr');
const realClientUrl = toUrl(`
import { HubConnectionBuilder as InstalledBuilder } from '${installedClientUrl}';
export { HubConnectionState, LogLevel } from '${installedClientUrl}';

export class HubConnectionBuilder extends InstalledBuilder {
  withUrl(url, options) {
    const state = globalThis.signalRSeedTestState;
    return super.withUrl(new URL(url, 'http://localhost').href, {
      ...options,
      transport: state.transport,
      httpClient: state.httpClient
    });
  }

  build() {
    const connection = super.build();
    globalThis.signalRSeedTestState.connections.push(connection);
    return connection;
  }
}
`);

const RECORD_SEPARATOR = '\x1e';

/**
 * The socket, held in memory: the client's handshake is answered at once and the test delivers each
 * server frame by hand. The client sets `onreceive` and `onclose` again on every connect.
 */
const transport = {
  connects: 0,
  onreceive: null,
  onclose: null,
  async connect() {
    transport.connects += 1;
  },
  async send(message) {
    // The handshake request is the one send that needs an answer; a keep-alive ping needs none.
    if (message.includes('"protocol"')) {
      setTimeout(() => transport.onreceive(`{}${RECORD_SEPARATOR}`), 0);
    }
  },
  async stop() {
    setTimeout(() => transport.onclose(), 0);
  },
  deliver(frame) {
    transport.onreceive(`${frame}${RECORD_SEPARATOR}`);
  }
};

/** Answers each negotiate with a new connection id, as the server does for every new connection. */
class NegotiateClient extends HttpClient {
  negotiations = 0;

  async send() {
    this.negotiations += 1;
    return new HttpResponse(
      200,
      'OK',
      JSON.stringify({
        negotiateVersion: 1,
        connectionId: `connection-${this.negotiations}`,
        connectionToken: `token-${this.negotiations}`,
        availableTransports: []
      })
    );
  }
}

const installedClient = await import(
  compileProvider(
    realClientUrl,
    await compileToUrl('../src/contexts/SignalRContext/retryPolicy.ts')
  )
);
const { useReconnectRefetch } = await import(
  await compileToUrl('../src/hooks/useReconnectRefetch.ts', { react: reactStubUrl })
);
const { createRunStoreState, nextGeneration } = await loadNotificationModules();

// The notification context's reconnect callback: the second argument of its one useReconnectRefetch call.
const notificationsSource = parseSource(
  'src/contexts/notifications/NotificationsContext.tsx',
  ts.ScriptKind.TSX
);
const reconnectCallback = findSoleNode(
  notificationsSource,
  'useReconnectRefetch call',
  (node) =>
    ts.isCallExpression(node) &&
    node.expression.getText(notificationsSource) === 'useReconnectRefetch'
).arguments[1].getText(notificationsSource);

/** Settles until `condition` holds, and fails after a bounded number of turns instead of hanging. */
const until = async (condition, message) => {
  for (let turn = 0; turn < 500 && !condition(); turn += 1) {
    await settle();
  }
  assert.ok(condition(), message);
};

/**
 * Mounts the provider over the installed client, with the real reconnect refetch running the
 * notification context's reconnect callback against a real run store, and waits for the first
 * connection.
 */
const mountInstalledClient = async (t) => {
  transport.connects = 0;
  state.transport = transport;
  state.httpClient = new NegotiateClient();
  const storeRef = { current: createRunStoreState() };
  const snapshotRequests = { count: 0 };
  const onReconnect = bindLifted(reconnectCallback, {
    connectedBeforeRef: { current: false },
    storeRef,
    nextGeneration,
    isAdminRef: { current: true },
    requestSnapshot: async () => {
      snapshotRequests.count += 1;
    }
  });
  const published = [];
  const harness = await mount(() => {
    const element = installedClient.SignalRProvider({ children: null });
    published.push(element.props.value);
    useReconnectRefetch(element.props.value.isConnected, onReconnect);
    return element;
  });
  const built = state.connections;
  t.after(() => Promise.all(built.map((connection) => connection.stop())));

  await until(
    () => harness.published().connectionState === 'connected',
    'the first connection never came up'
  );
  return { harness, published, storeRef, snapshotRequests };
};

test('a Close that allows reconnecting shows reconnecting, reconnects and reloads the runs', async (t) => {
  const { harness, published, storeRef, snapshotRequests } = await mountInstalledClient(t);
  const firstId = harness.published().connectionId;
  const requestsBefore = snapshotRequests.count;
  const generationBefore = storeRef.current.generation;
  const publishedBefore = published.length;

  transport.deliver('{"type":7,"allowReconnect":true}');
  await until(
    () =>
      harness.published().connectionState === 'connected' &&
      harness.published().connectionId !== firstId,
    'the client never came back on a new connection'
  );

  assert.ok(
    published
      .slice(publishedBefore)
      .some((value) => value.connectionState === 'reconnecting' && !value.isConnected),
    'the page never showed its reconnecting state'
  );
  assert.equal(transport.connects, 2);
  assert.equal(snapshotRequests.count, requestsBefore + 1, 'the run list was not requested again');
  assert.equal(
    storeRef.current.generation,
    generationBefore + 1,
    'the run store kept the old generation'
  );
});

test('a Close that forbids reconnecting leaves the page on its outage state without reconnecting', async (t) => {
  const { harness, snapshotRequests } = await mountInstalledClient(t);
  const requestsBefore = snapshotRequests.count;

  // A close of the live connection is an outage, so the page keeps the reconnecting state the
  // connection banner reads until the next start answers.
  transport.deliver('{"type":7}');
  await until(
    () =>
      harness.published().connectionState === 'reconnecting' && !harness.published().isConnected,
    'the client never reported the close'
  );

  assert.equal(transport.connects, 1);
  assert.equal(snapshotRequests.count, requestsBefore);
});
