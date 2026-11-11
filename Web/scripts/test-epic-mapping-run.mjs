import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import i18next from 'i18next';
import { compileToUrl } from './transpile-module.mjs';

const translator = i18next.createInstance();
await translator.init({
  lng: 'en',
  resources: {
    en: {
      translation: JSON.parse(
        readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
      )
    }
  },
  interpolation: { escapeValue: false }
});

/**
 * The Epic mapping run the sign-in dialog and the setup step show. The hook runs for real against a stub React, a
 * stub hub and a stub api: it follows one operation by id, keeps an ending until the form clears it, and reads the
 * run's state after a reconnect.
 */

const toUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/** The smallest React that can run this hook: ordered slots, dependency comparison and a post-render effect flush. */
const reactStubUrl = toUrl(`
let slots = null;
let at = 0;
let pendingEffects = [];
export const useRef = (initial) => {
  if (!slots[at]) slots[at] = { current: initial };
  return slots[at++];
};
export const useState = (initial) => {
  if (!slots[at]) slots[at] = { value: initial };
  const slot = slots[at++];
  const set = (next) => {
    slot.value = typeof next === 'function' ? next(slot.value) : next;
  };
  return [slot.value, set];
};
export const useCallback = (fn, deps) => {
  if (!slots[at]) slots[at] = { fn, deps };
  const slot = slots[at++];
  if (deps.some((value, index) => !Object.is(value, slot.deps[index]))) {
    slot.fn = fn;
    slot.deps = deps;
  }
  return slot.fn;
};
export const useEffect = (run, deps) => {
  if (!slots[at]) slots[at] = { deps: null, ran: false };
  const slot = slots[at++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) pendingEffects.push(() => { slot.cleanup?.(); slot.cleanup = run(); });
};
export const createComponent = () => {
  const componentSlots = [];
  return {
    render(body) {
      slots = componentSlots;
      at = 0;
      pendingEffects = [];
      const result = body();
      const effects = pendingEffects;
      pendingEffects = [];
      slots = null;
      effects.forEach((run) => run());
      return result;
    }
  };
};
`);
const { createComponent } = await import(reactStubUrl);

const hubUrl = toUrl(`
const handlers = new Map();
const hub = {
  on: (event, handler) => { handlers.set(event, [...(handlers.get(event) ?? []), handler]); },
  off: (event, handler) => { handlers.set(event, (handlers.get(event) ?? []).filter((entry) => entry !== handler)); }
};
globalThis.__resetHub = () => handlers.clear();
globalThis.__emit = (event, payload) => { (handlers.get(event) ?? []).slice().forEach((handler) => handler(payload)); };
export const useSignalR = () => { hub.isConnected = globalThis.__socketLive; return hub; };
`);
await import(hubUrl);

const apiStubUrl = toUrl(`
export default {
  getEpicScheduleStatus: async () => {
    const server = globalThis.__server;
    server.scheduleReads += 1;
    // The answer is what the server held when the read arrived, also when it reaches the browser later.
    const answer = server.schedule;
    if (server.scheduleGate) await server.scheduleGate;
    if (server.scheduleFailing) throw new Error('schedule status unavailable');
    return answer;
  },
  getTrackedOperation: async (id) => {
    const server = globalThis.__server;
    server.trackedReads.push(id);
    if (server.trackedGate) await server.trackedGate;
    return server.tracked;
  },
  getEpicMappingAuthStatus: async () => {
    const server = globalThis.__server;
    server.authReads += 1;
    if (server.authFailing) throw new Error('auth status unavailable');
    return server.auth;
  }
};
`);

const { useEpicMappingRun, describeEpicMappingRun } = await import(
  await compileToUrl('../src/hooks/useEpicMappingRun.ts', {
    react: reactStubUrl,
    '@services/api.service': apiStubUrl,
    '@contexts/SignalRContext/useSignalR': hubUrl,
    './useReconnectRefetch': await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
      react: reactStubUrl
    })
  })
);

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const startServer = (schedule) => {
  globalThis.__resetHub();
  globalThis.__socketLive = true;
  globalThis.__server = {
    scheduleReads: 0,
    scheduleFailing: false,
    scheduleGate: null,
    // The server leaves a null operation id out of the answer, so the default sends no key.
    schedule: { isProcessing: false, progressPercent: 0, ...schedule },
    trackedReads: [],
    trackedGate: null,
    tracked: null,
    authReads: 0,
    authFailing: false,
    auth: { isAuthenticated: true, gamesDiscovered: 0 }
  };
  return globalThis.__server;
};

const mount = async (armed = false) => {
  const component = createComponent();
  let isArmed = armed;
  let out = null;
  const render = () => {
    out = component.render(() => useEpicMappingRun(isArmed));
    return out;
  };
  render();
  await settle();
  render();
  return {
    render,
    arm: (value) => {
      isArmed = value;
      render();
      render();
    },
    phase: () => {
      const result = render();
      return (result.run ?? result).phase;
    },
    clear: () => {
      out.clear?.();
      render();
    },
    reconnect: async () => {
      globalThis.__socketLive = false;
      render();
      globalThis.__socketLive = true;
      render();
      await settle();
    }
  };
};

const started = (operationId) => ({
  serviceKey: 'epic',
  operationId,
  stageKey: 'signalr.epicMapping.starting',
  context: null
});
const progress = (operationId, percentComplete) => ({
  serviceKey: 'epic',
  operationId,
  status: 'running',
  stageKey: 'signalr.epicMapping.applyingMappings',
  percentComplete,
  context: null
});
const complete = (operationId, ending) => ({
  serviceKey: 'epic',
  operationId,
  success: ending === 'done',
  stageKey: ending === 'done' ? 'signalr.epicMapping.completed' : 'signalr.epicMapping.failed',
  percentComplete: 100,
  error: null,
  context: null,
  canceled: false,
  status: ending === 'done' ? 'completed' : 'failed'
});

test('a run found running at mount follows its own events to the end', async () => {
  startServer({ isProcessing: true, operationId: 'x', progressPercent: 40 });
  const run = await mount();
  assert.equal(run.phase(), 'running');

  globalThis.__emit('EpicMappingProgress', progress('y', 10));
  globalThis.__emit('EpicMappingProgress', progress('x', 80));
  globalThis.__emit('EpicMappingComplete', complete('x', 'done'));

  assert.equal(run.phase(), 'done');
});

test('a run that ended before the status read answered shows that ending', async () => {
  const server = startServer({ isProcessing: true, operationId: 'x', progressPercent: 90 });
  let open;
  server.scheduleGate = new Promise((resolve) => {
    open = resolve;
  });
  const run = await mount();
  globalThis.__emit('EpicMappingComplete', complete('x', 'failed'));
  open();
  await settle();

  assert.equal(run.phase(), 'failed');
});

test('a busy status with no operation id follows the run a later read names after it starts', async () => {
  const server = startServer({ isProcessing: true, progressPercent: 0 });
  const run = await mount();
  assert.equal(run.phase(), 'none');

  globalThis.__emit('EpicMappingProgress', progress('z', 30));
  globalThis.__emit('EpicMappingComplete', complete('z', 'failed'));
  assert.equal(run.phase(), 'none');
  assert.equal(server.scheduleReads, 1);

  server.schedule = { isProcessing: true, operationId: 'w', progressPercent: 0 };
  globalThis.__emit('EpicMappingStarted', started('w'));
  await settle();
  assert.equal(server.scheduleReads, 2);
  assert.equal(run.phase(), 'running');

  globalThis.__emit('EpicMappingProgress', progress('w', 60));
  assert.equal(run.phase(), 'running');
  globalThis.__emit('EpicMappingComplete', complete('w', 'done'));
  assert.equal(run.phase(), 'done');
});

test('a run started while the busy status read was out is followed once a second read names it', async () => {
  const server = startServer({ isProcessing: true, progressPercent: 0 });
  let open;
  server.scheduleGate = new Promise((resolve) => {
    open = resolve;
  });
  const run = await mount();
  globalThis.__emit('EpicMappingStarted', started('w'));
  server.schedule = { isProcessing: true, operationId: 'w', progressPercent: 10 };
  open();
  await settle();
  await settle();

  assert.equal(server.scheduleReads, 2);
  assert.equal(run.phase(), 'running');
  globalThis.__emit('EpicMappingComplete', complete('w', 'done'));
  assert.equal(run.phase(), 'done');
});

test('a busy status with no operation id that ends with no run stays idle', async () => {
  const server = startServer({ isProcessing: true, progressPercent: 0 });
  const run = await mount();

  server.schedule = { isProcessing: false, progressPercent: 0 };
  await run.reconnect();
  assert.equal(run.phase(), 'none');
  assert.deepEqual(server.trackedReads, []);

  server.schedule = { isProcessing: true, operationId: 'w', progressPercent: 50 };
  globalThis.__emit('EpicMappingStarted', started('w'));
  await settle();
  assert.equal(server.scheduleReads, 2);
  assert.equal(run.phase(), 'none');
});

test('a cleared run stops waiting for the busy run to be named', async () => {
  const server = startServer({ isProcessing: true, progressPercent: 0 });
  const run = await mount();
  run.clear();

  server.schedule = { isProcessing: true, operationId: 'w', progressPercent: 0 };
  globalThis.__emit('EpicMappingStarted', started('w'));
  await settle();

  assert.equal(server.scheduleReads, 1);
  assert.equal(run.phase(), 'none');
});

test('a start the form cleared away does not make the read that was out name its run', async () => {
  const server = startServer({ isProcessing: true, progressPercent: 0 });
  let open;
  server.scheduleGate = new Promise((resolve) => {
    open = resolve;
  });
  const run = await mount();
  globalThis.__emit('EpicMappingStarted', started('s'));
  run.clear();
  server.schedule = { isProcessing: true, operationId: 's', progressPercent: 10 };
  open();
  await settle();
  await settle();

  assert.equal(server.scheduleReads, 1);
  assert.equal(run.phase(), 'none');
});

test('a status read that failed leaves the run unknown', async () => {
  const server = startServer();
  server.scheduleFailing = true;
  const run = await mount();

  assert.equal(run.phase(), 'unknown');
});

test('an ended run the form clears is gone, also one found at mount', async () => {
  startServer({ isProcessing: true, operationId: 'x', progressPercent: 40 });
  const run = await mount();
  globalThis.__emit('EpicMappingComplete', complete('x', 'failed'));
  assert.equal(run.phase(), 'failed');

  run.clear();

  assert.equal(run.phase(), 'none');
});

test('another operation does not reopen a finished run', async () => {
  startServer();
  const run = await mount();
  run.arm(true);
  globalThis.__emit('EpicMappingStarted', started('x'));
  globalThis.__emit('EpicMappingComplete', {
    ...complete('x', 'done'),
    stageKey: 'signalr.epicMapping.mappingDone'
  });
  globalThis.__emit('EpicMappingStarted', started('y'));
  globalThis.__emit('EpicMappingProgress', progress('y', 20));

  assert.equal(run.phase(), 'done');
});

test('after a reconnect the run reads its ending from the server', async () => {
  for (const tracked of [
    { id: 'x', active: false, status: 'failed', error: 'boom' },
    { id: 'x', active: false, status: null }
  ]) {
    const server = startServer();
    const run = await mount();
    run.arm(true);
    globalThis.__emit('EpicMappingStarted', started('x'));
    assert.equal(run.phase(), 'running');
    server.tracked = tracked;

    await run.reconnect();

    assert.deepEqual(server.trackedReads, ['x']);
    assert.equal(run.phase(), 'failed');
  }
});

test('a failed ending read after a reconnect is worded without a missing error detail', async () => {
  const server = startServer();
  const run = await mount();
  run.arm(true);
  globalThis.__emit('EpicMappingStarted', started('x'));
  server.tracked = { id: 'x', active: false, status: 'failed', error: 'boom' };

  await run.reconnect();

  assert.equal(describeEpicMappingRun(run.render().run, translator.t), 'Epic game mapping failed');
});

const reconnectToCompletedRun = async (configure) => {
  const server = startServer();
  configure(server);
  const run = await mount();
  run.arm(true);
  globalThis.__emit('EpicMappingStarted', started('x'));
  server.tracked = { id: 'x', active: false, status: 'completed' };
  await run.reconnect();
  await settle();
  return { server, ended: run.render().run };
};

test('a completed run read after a reconnect shows the games the signed-in account mapped', async () => {
  const { server, ended } = await reconnectToCompletedRun((server) => {
    server.auth = { isAuthenticated: true, gamesDiscovered: 36 };
  });

  assert.equal(server.authReads, 1);
  assert.equal(ended.phase, 'done');
  assert.equal(ended.stageKey, 'signalr.epicMapping.mappingDone');
  assert.deepEqual(ended.context, { count: 36 });
  assert.equal(describeEpicMappingRun(ended, translator.t), 'Done. Games mapped: 36');
});

test('a completed run read after a reconnect while signed out shows no count', async () => {
  const { ended } = await reconnectToCompletedRun((server) => {
    server.auth = { isAuthenticated: false, gamesDiscovered: 0 };
  });

  assert.equal(ended.phase, 'done');
  assert.equal(ended.stageKey, 'signalr.epicMapping.completed');
  assert.equal(ended.context, null);
});

test('a completed run whose account read fails after a reconnect still ends without a count', async () => {
  const { ended } = await reconnectToCompletedRun((server) => {
    server.authFailing = true;
  });

  assert.equal(ended.phase, 'done');
  assert.equal(ended.stageKey, 'signalr.epicMapping.completed');
  assert.equal(ended.context, null);
});

test('a reconnect answer for a run the form cleared meanwhile changes nothing', async () => {
  const server = startServer();
  const run = await mount();
  run.arm(true);
  globalThis.__emit('EpicMappingStarted', started('x'));
  server.tracked = { id: 'x', active: false, status: null };
  let open;
  server.trackedGate = new Promise((resolve) => {
    open = resolve;
  });
  await run.reconnect();
  assert.deepEqual(server.trackedReads, ['x']);

  run.arm(false);
  run.clear();
  open();
  await settle();

  assert.equal(run.phase(), 'none');
});
