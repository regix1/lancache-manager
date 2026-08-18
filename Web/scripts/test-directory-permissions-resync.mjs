import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * These permissions decide whether a write is offered at all, and the only thing that moves them is
 * a DirectoryPermissionsChanged that a disconnected client never receives. So the hook is run for
 * real here - compiled and driven through a sequence of renders with a stub server behind it -
 * across the three sequences that matter: a mount into a live socket, which must fetch once and
 * only once; a mount before the socket came up, whose fetch missed the subscription window; and a
 * drop, a change made server-side while it was down, and the recovery that has to notice.
 */

/**
 * The smallest React that can run this hook: ordered slots for `useState`, `useRef` and
 * `useCallback`, dependency comparison, and a post-render flush for `useEffect`. A data-URL import
 * resolves no bare specifier, so the hook is compiled against this module in place of the real one.
 */
const reactStubSource = `
let slots = null;
let cursor = 0;
let queued = [];

export const useRef = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { current: initial };
  }
  return slots[cursor++];
};

export const useState = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { value: typeof initial === 'function' ? initial() : initial };
  }
  const slot = slots[cursor++];
  const set = (next) => {
    slot.value = typeof next === 'function' ? next(slot.value) : next;
  };
  return [slot.value, set];
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
    slots[cursor] = { deps: null, ran: false };
  }
  const slot = slots[cursor++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) {
    queued.push(run);
  }
};

export const createComponent = () => {
  const componentSlots = [];
  return {
    render(body) {
      slots = componentSlots;
      cursor = 0;
      queued = [];
      const result = body();
      const effects = queued;
      queued = [];
      slots = null;
      effects.forEach((run) => run());
      return result;
    }
  };
};
`;

const toUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

const reactStubUrl = toUrl(reactStubSource);
const { createComponent } = await import(reactStubUrl);

/**
 * The connection flag the caller reads is swapped between renders, so the hook sees a transition.
 * A data-URL module cannot close over anything here, so the flag travels on the global.
 */
const signalRStubUrl = toUrl(`
const on = () => {};
const off = () => {};
export const useSignalR = () => ({ on, off, isConnected: globalThis.__socketLive });
`);

const apiStubUrl = toUrl(`
export default { getFetchOptions: (options) => options ?? {} };
`);
const apiErrorStubUrl = toUrl(`export const assertOk = async () => {};`);

const reconnectUrl = await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
  react: reactStubUrl
});

const { useDirectoryPermissions } = await import(
  await compileToUrl('../src/hooks/useDirectoryPermissions.ts', {
    react: reactStubUrl,
    '@services/api.service': apiStubUrl,
    '@services/apiError': apiErrorStubUrl,
    '@contexts/SignalRContext/useSignalR': signalRStubUrl,
    '@hooks/useReconnectRefetch': reconnectUrl
  })
);

/** What the stub server currently answers with, and how many times it has been asked. */
const startServer = (cacheWritable) => {
  const server = { cacheWritable, requests: 0 };
  globalThis.fetch = async () => {
    server.requests += 1;
    return {
      ok: true,
      json: async () => ({
        logs: { readOnly: false, exists: true, writable: true, path: '/logs' },
        cache: { readOnly: false, exists: true, writable: server.cacheWritable, path: '/cache' },
        dockerSocket: { available: true }
      })
    };
  };
  return server;
};

/** Lets the fetch promise chain inside the hook settle before the next render reads its state. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const mount = (connectedAtMount) => {
  globalThis.__socketLive = connectedAtMount;
  const component = createComponent();
  let permissions = null;
  const render = (isConnected) => {
    globalThis.__socketLive = isConnected;
    permissions = component.render(() => useDirectoryPermissions());
    return permissions;
  };
  render(connectedAtMount);
  return { render, read: () => permissions };
};

test('a mount into a live socket asks once, not twice', async () => {
  const server = startServer(true);
  const caller = mount(true);
  await settle();

  assert.equal(server.requests, 1, 'the mount fetch already had the subscription up');
  assert.equal(caller.render(true).cacheWritable, true);
  assert.equal(server.requests, 1, 'a re-render at the same connection state asks nothing');
});

test('a socket that comes up after the mount fetch replaces what that fetch could have missed', async () => {
  const server = startServer(false);
  const caller = mount(false);
  await settle();

  assert.equal(server.requests, 1, 'the mount fetch runs whether or not the socket is up');

  // The change lands in the window between that fetch and the subscription going live, which is
  // exactly the window no event covers.
  server.cacheWritable = true;

  caller.render(true);
  await settle();

  assert.equal(server.requests, 2);
  assert.equal(caller.render(true).cacheWritable, true, 'the write is offered again');
});

test('a drop, a change made while it was down, and the recovery that notices', async () => {
  const server = startServer(true);
  const caller = mount(true);
  await settle();
  assert.equal(caller.render(true).cacheWritable, true);

  caller.render(false);
  await settle();
  assert.equal(server.requests, 1, 'a drop asks for nothing');

  // The cache directory is remounted read-only while this client cannot hear about it.
  server.cacheWritable = false;

  caller.render(true);
  await settle();

  assert.equal(server.requests, 2);
  assert.equal(
    caller.render(true).cacheWritable,
    false,
    'a write offered here starts and then fails server-side'
  );
});
