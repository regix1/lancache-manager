import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, transpile } from './transpile-module.mjs';

/**
 * A preference changed while the socket was down reaches nobody and nothing replays it, so the
 * session preferences have to be asked for again once the connection is back. That reload is an
 * HTTP answer, not a broadcast, and it meets two kinds of optimistic pick: one whose save is still
 * on the wire, which is newer than the answer and has to win, and one the server has already said
 * yes to, which has nothing left to shield and has to be let go. This file runs the real load
 * function and the real pending store against both, and counts the requests a mount makes.
 */

const SESSION = 'session-1';

const contextSource = readFileSync(
  new URL('../src/contexts/SessionPreferencesContext.tsx', import.meta.url),
  'utf8'
);
const contextFile = ts.createSourceFile(
  'SessionPreferencesContext.tsx',
  contextSource,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

const collect = (matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(contextFile, visit);
  return found;
};

/** The function a `const name = useCallback(fn, deps)` declaration holds, as source text. */
const callbackNamed = (name) => {
  const declared = collect(
    (node) => ts.isVariableDeclaration(node) && node.name.getText(contextFile) === name
  );
  assert.equal(declared.length, 1, `expected one declaration of ${name}`);
  const initializer = declared[0].initializer;
  const fn = ts.isCallExpression(initializer) ? initializer.arguments[0] : initializer;
  return fn.getText(contextFile);
};

/** Compiles one lifted function against named values supplied in place of its closure. */
const lift = (source, bindings) => {
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${source});`);
  return new Function(...names, `${compiled}\nreturn lifted;`)(...names.map((n) => bindings[n]));
};

const pendingUrl = await compileToUrl('../src/utils/pendingPreferences.ts', {
  '../types/userPreferences.ts': await compileToUrl('../src/types/userPreferences.ts')
});
const {
  setPendingTimezone,
  confirmPendingTimezone,
  dropPendingTimezone,
  preferPendingTimezone,
  getPendingValue
} = await import(pendingUrl);
const { applyGuestClockChanges } = await import(
  await compileToUrl('../src/utils/guestDefaultPreferenceGate.ts', {
    '../types/userPreferences.ts': await compileToUrl('../src/types/userPreferences.ts')
  })
);
const { DEFAULT_PREFERENCES } = await import(await compileToUrl('../src/types/userPreferences.ts'));
const { SIGNALR_SEED_EVENTS } = await import(
  await compileToUrl('../src/contexts/SignalRContext/types.ts')
);

/**
 * The smallest React that can run one hook: ordered slots for `useRef`, dependency comparison for
 * `useEffect`, and a flush after the body. A data-URL import resolves no bare specifier, so the
 * hook is compiled against this in place of the real one.
 */
const reactStubSource = `
let slots = null;
let slotIndex = 0;
let queued = [];

export const useRef = (initial) => {
  if (!slots[slotIndex]) slots[slotIndex] = { current: initial };
  return slots[slotIndex++];
};

export const useEffect = (run, deps) => {
  if (!slots[slotIndex]) slots[slotIndex] = { deps: null, ran: false };
  const slot = slots[slotIndex++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) queued.push(run);
};

export const createComponent = () => {
  const componentSlots = [];
  return {
    render(body) {
      slots = componentSlots;
      slotIndex = 0;
      queued = [];
      body();
      const effects = queued;
      queued = [];
      slots = null;
      effects.forEach((run) => run());
    }
  };
};
`;
const reactStubUrl = `data:text/javascript;base64,${Buffer.from(reactStubSource).toString('base64')}`;
const { useEffect, createComponent } = await import(reactStubUrl);
const { useReconnectRefetch } = await import(
  await compileToUrl('../src/hooks/useReconnectRefetch.ts', { react: reactStubUrl })
);

const SERVER_12H = {
  selectedTheme: 'dark',
  useLocalTimezone: false,
  useUtcTimezone: false,
  use24HourFormat: false
};

/**
 * The context's own load, resync and optimistic write, taken out of the file and given the refs
 * and callbacks they close over. `gate` lets a test hold a response on the wire.
 */
const mountPreferences = ({ body = SERVER_12H, gate = Promise.resolve() } = {}) => {
  const requests = [];
  const complaints = [];
  let state = {};

  const refs = {
    loadingIds: { current: new Set() },
    loadedIds: { current: new Set() },
    failedIds: { current: new Set() },
    pendingDefaultClocks: { current: new Map() },
    loadGenerations: { current: new Map() },
    preferencesRef: { current: {} }
  };

  const shared = {
    ...refs,
    isAdmin: false,
    getCurrentSessionId: () => SESSION,
    setPreferences: (next) => {
      state = typeof next === 'function' ? next(state) : next;
      refs.preferencesRef.current = state;
    },
    console: { warn: (text) => complaints.push(text), error: (text) => complaints.push(text) }
  };

  const nextLoadGeneration = lift(callbackNamed('nextLoadGeneration'), {
    loadGenerations: refs.loadGenerations
  });

  const loadSessionPreferences = lift(callbackNamed('loadSessionPreferences'), {
    ...shared,
    nextLoadGeneration,
    preferPendingTimezone,
    applyGuestClockChanges,
    ApiService: { getFetchOptions: () => ({}) },
    fetch: async (url) => {
      requests.push(url);
      await gate;
      return { ok: true, status: 200, json: async () => body };
    }
  });

  const resyncPreferences = lift(callbackNamed('resyncPreferences'), {
    ...shared,
    loadSessionPreferences
  });

  const applyOptimisticPreferences = lift(callbackNamed('applyOptimisticPreferences'), {
    ...shared,
    nextLoadGeneration,
    DEFAULT_PREFERENCES
  });

  const dispatched = [];
  const handleUserPreferencesUpdated = lift(callbackNamed('handleUserPreferencesUpdated'), {
    ...shared,
    nextLoadGeneration,
    preferPendingTimezone,
    DEFAULT_PREFERENCES,
    APP_EVENTS: { PREFERENCE_CHANGED: 'preference-changed' },
    CustomEvent: class {
      constructor(name, init) {
        this.detail = init.detail;
      }
    },
    window: { dispatchEvent: (event) => dispatched.push(event.detail.key) }
  });

  return {
    requests,
    complaints,
    refs,
    dispatched,
    loadSessionPreferences,
    resyncPreferences,
    applyOptimisticPreferences,
    handleUserPreferencesUpdated,
    clock: () => {
      const held = state[SESSION];
      return {
        useLocal: held.useLocalTimezone,
        useUtc: held.useUtcTimezone,
        use24Hour: held.use24HourFormat
      };
    },
    theme: () => state[SESSION]?.selectedTheme
  };
};

/** No pick outstanding, which is what a session that has clicked nothing looks like. */
const atRest = () => dropPendingTimezone(setPendingTimezone('server-12h'));

test('a pick still waiting on its save outranks the clock a resync fetches', async () => {
  atRest();
  const context = mountPreferences();
  await context.loadSessionPreferences(SESSION);
  setPendingTimezone('utc');

  context.resyncPreferences();
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(context.requests.length, 2, 'the resync has to reach the server');
  assert.deepEqual(
    context.clock(),
    { useLocal: false, useUtc: true, use24Hour: true },
    'the save behind the click has not answered, so the server answer is older than the click'
  );
  assert.equal(
    getPendingValue('useUtcTimezone'),
    true,
    'an outstanding pick is still held after the reload, or the next broadcast takes it back'
  );
});

test('a pick the server has confirmed is released by a resync', async () => {
  atRest();
  const context = mountPreferences();
  await context.loadSessionPreferences(SESSION);
  confirmPendingTimezone(setPendingTimezone('utc'));

  context.resyncPreferences();
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(
    context.clock(),
    { useLocal: false, useUtc: false, use24Hour: false },
    'the client just asked the server directly, so a confirmed pick has nothing left to shield'
  );
  assert.equal(
    getPendingValue('useUtcTimezone'),
    null,
    'a released pick must not survive to mask the next reset or default change'
  );
});

test('the resync clears every marker that makes a second load a no-op', async () => {
  atRest();
  const context = mountPreferences();
  await context.loadSessionPreferences(SESSION);
  context.refs.failedIds.current.add(SESSION);

  await context.loadSessionPreferences(SESSION);
  assert.equal(context.requests.length, 1, 'the markers left by the first load block a plain call');

  context.resyncPreferences();
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(context.requests.length, 2);
  assert.deepEqual(context.complaints, [], 'the reload is an ordinary load, not a recovery');
  assert.deepEqual(
    context.clock(),
    { useLocal: false, useUtc: false, use24Hour: false },
    'clearing the markers is only worth anything if the answer reaches the readers'
  );
});

test('the markers the load reads are the markers the resync clears', () => {
  const guard = callbackNamed('loadSessionPreferences').split('return')[0];
  const read = ['loadingIds', 'loadedIds', 'failedIds'].filter((name) => guard.includes(name));

  assert.deepEqual(read, ['loadingIds', 'loadedIds', 'failedIds']);
  assert.ok(
    !guard.includes('initialLoadDone'),
    'initialLoadDone gates the mount effect only, so clearing it cannot let a load through'
  );

  const resync = callbackNamed('resyncPreferences');
  assert.ok(resync.includes('loadedIds.current.delete(sessionId)'));
  assert.ok(resync.includes('failedIds.current.delete(sessionId)'));
  assert.ok(
    !resync.includes('loadingIds'),
    'a load already on the wire owns the session and this one folds into it'
  );
});

test('a write made while the answer was on the wire is not overwritten by it', async () => {
  atRest();
  let release;
  const gate = new Promise((resolve) => {
    release = resolve;
  });
  const context = mountPreferences({ gate });

  const load = context.loadSessionPreferences(SESSION);
  context.applyOptimisticPreferences({ selectedTheme: 'ocean' });
  release();
  await load;

  assert.equal(
    context.theme(),
    'ocean',
    'the click happened after the request went out, so it is newer than what came back'
  );
  assert.ok(
    context.refs.loadedIds.current.has(SESSION),
    'the session holds preferences either way, so a discarded answer must not leave it unloaded'
  );
});

/** A whole row that matches the defaults everywhere except the theme, as the broadcast carries one. */
const WHOLE_ROW = {
  selectedTheme: 'held',
  sharpCorners: false,
  disableFocusOutlines: true,
  disableTooltips: false,
  picsAlwaysVisible: false,
  disableStickyNotifications: false,
  showDatasourceLabels: true,
  useLocalTimezone: false,
  useUtcTimezone: false,
  use24HourFormat: true,
  refreshRate: null,
  refreshRateLocked: null,
  allowedTimeFormats: null
};

/**
 * The transport can hold a message and hand it to a handler the moment that handler subscribes,
 * which for this context is while the mount load is still on the wire. The load claims a generation
 * before it fetches and drops its answer if anything claimed one meanwhile, so a held message wins a
 * race it was never in: the answer is thrown away, and the queued default clock only the load ever
 * applies goes out with it. Three costs in one run, and the reason the name below is not held.
 */
test('a message replayed while the mount load is on the wire discards the answer and the queued clock', async () => {
  atRest();
  let release;
  const gate = new Promise((resolve) => {
    release = resolve;
  });
  const context = mountPreferences({ body: { ...WHOLE_ROW, selectedTheme: 'fetched' }, gate });

  context.refs.pendingDefaultClocks.current.set(SESSION, [
    {
      previousClock: { useUtcTimezone: false, useLocalTimezone: false, use24HourFormat: true },
      clock: { useUtcTimezone: true, useLocalTimezone: false, use24HourFormat: true }
    }
  ]);

  const load = context.loadSessionPreferences(SESSION);
  context.handleUserPreferencesUpdated({ sessionId: SESSION, preferences: WHOLE_ROW });
  release();
  await load;

  assert.equal(
    context.theme(),
    'held',
    'the answer was read off the server later and still lost to what the connection began with'
  );
  assert.deepEqual(
    context.clock(),
    { useLocal: false, useUtc: false, use24Hour: true },
    'the queued clock change is applied by the load alone, and the load it cancelled was the only one coming'
  );
  assert.ok(
    !context.refs.pendingDefaultClocks.current.has(SESSION),
    'the discarded load drops the queue on its way out, so no later load picks it up either'
  );
  assert.deepEqual(
    context.dispatched,
    ['selectedTheme'],
    'a first observation is diffed against the defaults, so it reports a change nobody made'
  );
  assert.ok(
    !SIGNALR_SEED_EVENTS.has('UserPreferencesUpdated'),
    'holding this name is what turns every connect into the replay above'
  );
});

/** Mounts the load effect and the resync hook the way the context declares them, in that order. */
const mountWithConnection = async (connectedAtMount) => {
  const context = mountPreferences();
  const component = createComponent();
  const render = (isConnected) =>
    component.render(() => {
      useEffect(() => {
        context.loadSessionPreferences(SESSION);
      }, [SESSION]);
      useReconnectRefetch(isConnected, context.resyncPreferences);
    });

  render(connectedAtMount);
  await new Promise((resolve) => setImmediate(resolve));
  return { context, render };
};

test('a mount into a live connection fetches once', async () => {
  atRest();
  const { context, render } = await mountWithConnection(true);

  render(true);
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(
    context.requests.length,
    1,
    'the mount fetch went out with the subscription live, so a second one would be a duplicate'
  );
});

test('a connection that comes up after the mount fetch replaces it', async () => {
  atRest();
  const { context, render } = await mountWithConnection(false);
  assert.equal(context.requests.length, 1);

  render(true);
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(
    context.requests.length,
    2,
    'that mount fetch was taken before the subscription existed and could have missed a change'
  );
});

test('the resync hangs off the shared hook and nothing else', () => {
  const calls = collect(
    (node) => ts.isCallExpression(node) && node.expression.getText(contextFile)
  ).map((node) => node.expression.getText(contextFile));

  assert.equal(
    calls.filter((name) => name === 'useReconnectRefetch').length,
    1,
    'one resync, through the hook every other context uses'
  );
  assert.ok(
    !contextSource.includes('connectionState'),
    'a second shape watching the same connection is the duplication this replaces'
  );
  assert.ok(
    contextSource.indexOf('useReconnectRefetch(isConnected, resyncPreferences)') >
      contextSource.indexOf('if (sessionId && !initialLoadDone.current)'),
    'declared after the load effect, so a session and a connection in one commit load once'
  );
});
