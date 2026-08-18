import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

/**
 * `globalLocked` and `isControlledByAdmin` decide whether a guest may change their own refresh rate.
 * The lock arrives as a broadcast and nothing replays it, so an unlock sent while the socket was
 * down used to leave the control disabled with nothing on screen to say the state was wrong. This
 * file runs the real provider - compiled against a minimal React and driven through a connection
 * drop and recovery - and holds three properties together: a socket that was already live at mount
 * must not produce a second fetch, a recovery must re-read the lock, and a resync response that was
 * overtaken while in flight must lose to the newer value.
 */

const toUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/**
 * The smallest React that can run the provider: ordered slots for state, refs, memoised callbacks
 * and effects with cleanup, a render loop that keeps going while a state write dirties it, and an
 * element factory that hands the test the context value the provider published.
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
  authMode: 'guest',
  isConnected: true,
  currentPreferences: { refreshRate: null, refreshRateLocked: null },
  requests: [],
  handlers: new Map()
};
globalThis.refreshRateTestState = state;

state.on = (name, handler) => state.handlers.set(name, handler);
state.off = (name) => state.handlers.delete(name);

globalThis.fetch = (url) =>
  new Promise((resolve) => {
    state.requests.push({
      url,
      respond: (body) => resolve({ ok: true, json: async () => body })
    });
  });

const signalRStubUrl = toUrl(`
export const useSignalR = () => {
  const state = globalThis.refreshRateTestState;
  return { on: state.on, off: state.off, isConnected: state.isConnected };
};
`);

const authStubUrl = toUrl(`
export const useAuth = () => ({ authMode: globalThis.refreshRateTestState.authMode });
`);

const sessionPreferencesStubUrl = toUrl(`
export const useSessionPreferences = () => ({
  currentPreferences: globalThis.refreshRateTestState.currentPreferences
});
`);

const apiStubUrl = toUrl(`export default { getJsonFetchOptions: () => ({}) };`);
const apiErrorStubUrl = toUrl(`export const assertOk = async () => {};`);
const contextTypesStubUrl = toUrl(`export const RefreshRateContext = { Provider: 'provider' };`);

// The real constants module reads `import.meta.env`, which node has no value for, so the two names
// the provider uses are supplied directly with the rate keys the app ships.
const constantsUrl = toUrl(`
export const APP_EVENTS = { SHOW_TOAST: 'show-toast' };
export const REFRESH_RATES = {
  LIVE: 0,
  ULTRA: 1000,
  REALTIME: 5000,
  STANDARD: 10000,
  RELAXED: 30000,
  SLOW: 60000
};
`);
const reconnectRefetchUrl = await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
  react: reactStubUrl
});

/**
 * A data-URL import resolves no path alias, so every dependency is substituted by its own URL. The
 * provider holds JSX, which the shared compiler does not emit, so it is compiled here instead.
 */
const compileProvider = () => {
  const source = readFileSync(
    new URL('../src/contexts/RefreshRateContext.tsx', import.meta.url),
    'utf8'
  );
  const compiled = ts.transpileModule(source, {
    fileName: 'RefreshRateContext.tsx',
    compilerOptions: {
      module: ts.ModuleKind.ESNext,
      target: ts.ScriptTarget.ES2022,
      jsx: ts.JsxEmit.React
    }
  }).outputText;
  const aliasUrls = {
    react: reactStubUrl,
    '@utils/constants': constantsUrl,
    '@services/api.service': apiStubUrl,
    '@services/apiError': apiErrorStubUrl,
    '@contexts/SignalRContext/useSignalR': signalRStubUrl,
    '@hooks/useReconnectRefetch': reconnectRefetchUrl,
    '@contexts/useAuth': authStubUrl,
    '@contexts/useSessionPreferences': sessionPreferencesStubUrl,
    './RefreshRateContext.types': contextTypesStubUrl
  };
  const resolved = Object.entries(aliasUrls).reduce(
    (text, [alias, url]) => text.split(`'${alias}'`).join(`'${url}'`),
    compiled
  );
  return toUrl(resolved);
};

const { RefreshRateProvider } = await import(compileProvider());
const { SIGNALR_SEED_EVENTS } = await import(
  await compileToUrl('../src/contexts/SignalRContext/types.ts')
);

/** Lets the awaits inside a fetch handler run before the test looks at the result. */
const settle = () => new Promise((resolve) => setImmediate(resolve));

/** Mounts one provider at the given auth mode and connection state. */
const mount = ({ authMode = 'guest', isConnected = true, refreshRateLocked = null } = {}) => {
  state.authMode = authMode;
  state.isConnected = isConnected;
  state.currentPreferences = authMode === 'guest' ? { refreshRate: null, refreshRateLocked } : null;
  state.requests = [];
  state.handlers = new Map();

  const component = createComponent(() => RefreshRateProvider({ children: null }));
  component.render();
  return {
    component,
    connect: (connected) => {
      state.isConnected = connected;
      component.render();
    },
    published: () => component.output?.props.value
  };
};

test('a socket already live at mount fetches once, for a guest and for an admin', async () => {
  const guest = mount({ authMode: 'guest', isConnected: true });
  assert.deepEqual(
    state.requests.map((request) => request.url),
    ['/api/system/default-guest-refresh-rate'],
    'the mount fetch already ran with the subscription up, so a resync would only duplicate it'
  );
  state.requests[0].respond({ locked: true, refreshRate: 'SLOW' });
  await settle();
  assert.equal(
    state.requests.length,
    1,
    'a second fetch on mount is the regression being ruled out'
  );
  assert.equal(guest.published().isControlledByAdmin, true);

  const admin = mount({ authMode: 'authenticated', isConnected: true });
  assert.deepEqual(
    state.requests.map((request) => request.url),
    ['/api/system/refresh-rate']
  );
  state.requests[0].respond({ refreshRate: 'RELAXED' });
  await settle();
  assert.equal(state.requests.length, 1);
  assert.equal(admin.published().refreshRate, 'RELAXED');
});

test('an unlock sent while the socket was down is picked up on recovery', async () => {
  const guest = mount({ authMode: 'guest', isConnected: true });
  state.requests[0].respond({ locked: true, refreshRate: null });
  await settle();
  assert.equal(guest.published().isControlledByAdmin, true, 'locked before the drop');

  guest.connect(false);
  // The admin unlocks here. The broadcast is sent to a socket that is gone and nothing replays it.
  guest.connect(true);

  assert.equal(state.requests.length, 2, 'recovery must re-read the lock');
  state.requests[1].respond({ locked: false, refreshRate: null });
  await settle();
  assert.equal(
    guest.published().isControlledByAdmin,
    false,
    'without the resync the control stays dead until the user reloads'
  );
});

test('an admin recovers the system rate the same way', async () => {
  const admin = mount({ authMode: 'authenticated', isConnected: true });
  state.requests[0].respond({ refreshRate: 'STANDARD' });
  await settle();

  admin.connect(false);
  admin.connect(true);

  assert.deepEqual(
    state.requests.map((request) => request.url),
    ['/api/system/refresh-rate', '/api/system/refresh-rate']
  );
  state.requests[1].respond({ refreshRate: 'ULTRA' });
  await settle();
  assert.equal(admin.published().refreshRate, 'ULTRA');
});

test('a socket that comes up after mount resyncs, and only once', async () => {
  const guest = mount({ authMode: 'guest', isConnected: false });
  assert.equal(state.requests.length, 1, 'the mount fetch runs with no subscription behind it');
  state.requests[0].respond({ locked: true, refreshRate: null });
  await settle();

  guest.connect(true);
  assert.equal(state.requests.length, 2, 'that first fetch could have missed an event, so re-read');
  state.requests[1].respond({ locked: false, refreshRate: null });
  await settle();
  assert.equal(guest.published().isControlledByAdmin, false);

  guest.component.render();
  assert.equal(state.requests.length, 2, 'a re-render on a steady connection fetches nothing');
});

test('a resync response overtaken while in flight does not win', async () => {
  const guest = mount({ authMode: 'guest', isConnected: true });
  state.requests[0].respond({ locked: true, refreshRate: null });
  await settle();

  guest.connect(false);
  guest.connect(true);
  assert.equal(state.requests.length, 2);

  // The lock changes after the resync was sent and before its answer came back.
  state.handlers.get('GuestRefreshRateLockChanged')({ locked: false });
  assert.equal(guest.published().isControlledByAdmin, false);

  state.requests[1].respond({ locked: true, refreshRate: null });
  await settle();
  assert.equal(
    guest.published().isControlledByAdmin,
    false,
    'the older answer must not roll the lock back over the newer broadcast'
  );
});

/**
 * The generation guard above is right for a broadcast, because a broadcast is news of a change and
 * so is newer than any answer still on the wire. It is wrong for a message the transport held from
 * connect time and handed over the moment these handlers subscribed, which is during the mount
 * fetch: that message is older than the answer it cancels. Neither name may be held for that reason.
 */
test('a message replayed at subscribe time cancels the mount fetch, so neither rate name is held', async () => {
  const guest = mount({ authMode: 'guest', isConnected: true });
  assert.equal(state.requests.length, 1, 'the mount fetch is on the wire');

  state.handlers.get('DefaultGuestRefreshRateChanged')({ refreshRate: 'SLOW' });
  state.handlers.get('GuestRefreshRateLockChanged')({ locked: true });

  state.requests[0].respond({ locked: false, refreshRate: 'RELAXED' });
  await settle();

  assert.equal(
    guest.published().isControlledByAdmin,
    true,
    'the answer said unlocked and was discarded, leaving the control dead with nothing on screen to say so'
  );
  assert.equal(guest.published().refreshRate, 'SLOW', 'and the rate it carried is gone too');

  for (const name of ['DefaultGuestRefreshRateChanged', 'GuestRefreshRateLockChanged']) {
    assert.ok(
      !SIGNALR_SEED_EVENTS.has(name),
      `holding ${name} is what turns every connect into the replay above`
    );
  }
});

test('a rate the user picked mid-flight survives the resync answer', async () => {
  const admin = mount({ authMode: 'authenticated', isConnected: true });
  state.requests[0].respond({ refreshRate: 'STANDARD' });
  await settle();

  admin.connect(false);
  admin.connect(true);
  assert.equal(state.requests.length, 2);

  void admin.published().setRefreshRate('LIVE');
  assert.equal(admin.published().refreshRate, 'LIVE');

  state.requests[1].respond({ refreshRate: 'STANDARD' });
  await settle();
  assert.equal(
    admin.published().refreshRate,
    'LIVE',
    'the pick was made after the request was sent'
  );
});
