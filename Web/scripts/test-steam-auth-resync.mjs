import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';
import { compileToUrl } from './transpile-module.mjs';

/**
 * Steam auth state moves on two broadcasts and nothing else, so a session the server dropped while
 * this tab was disconnected leaves the tab offering a Steam login that is already gone. The
 * provider is run for real here - compiled with its JSX and driven through a sequence of renders
 * with a stub server behind it - so the sequence that matters is the sequence asserted: connected,
 * dropped, logged out server-side, reconnected. The guest case is here too, because this provider
 * is mounted for every session and the status endpoint is not for guests.
 */

const toUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/**
 * The smallest React that can run this provider: ordered slots for `useState`, `useRef` and
 * `useCallback`, dependency comparison, a post-render flush for `useEffect`, and a `createElement`
 * that hands back the context value instead of rendering it.
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

export const createElement = (type, props) => ({ type, props });

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

export default { createElement };
`;

const reactStubUrl = toUrl(reactStubSource);
const { createComponent } = await import(reactStubUrl);

const apiStubUrl = toUrl(`export default { getFetchOptions: () => ({}) };`);

/** One hub object across renders, so only the flag the provider reads changes. */
const signalRStubUrl = toUrl(`
const hub = { on: () => {}, off: () => {}, isConnected: false };
export const useSignalR = () => {
  hub.isConnected = globalThis.__socketLive;
  return hub;
};
`);

const authStubUrl = toUrl(`
export const useAuth = () => ({ authMode: globalThis.__authMode, isLoading: false });
`);

const contextStubUrl = toUrl(`export const SteamAuthContext = { Provider: 'SteamAuthContext' };`);

const reconnectUrl = await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
  react: reactStubUrl
});

/** `transpile` compiles .ts; the provider is .tsx, so its JSX needs the emit turned on here. */
const compileProvider = async (aliasUrls) => {
  const source = await readFile(
    new URL('../src/contexts/SteamAuthContext.tsx', import.meta.url),
    'utf8'
  );
  const compiled = typescript.transpileModule(source, {
    fileName: 'SteamAuthContext.tsx',
    compilerOptions: {
      module: typescript.ModuleKind.ESNext,
      target: typescript.ScriptTarget.ES2022,
      jsx: typescript.JsxEmit.React
    }
  }).outputText;
  const resolved = Object.entries(aliasUrls).reduce(
    (text, [alias, url]) => text.split(`'${alias}'`).join(`'${url}'`),
    compiled
  );
  return toUrl(resolved);
};

const { SteamAuthProvider } = await import(
  await compileProvider({
    react: reactStubUrl,
    '@services/api.service': apiStubUrl,
    '@contexts/SignalRContext/useSignalR': signalRStubUrl,
    '@contexts/useAuth': authStubUrl,
    '@hooks/useReconnectRefetch': reconnectUrl,
    './SteamAuthContext.types': contextStubUrl
  })
);

/** What /steam-auth/status currently answers, and how many times it has been asked. */
const startServer = (mode, username) => {
  const server = { mode, username, requests: 0 };
  globalThis.fetch = async () => {
    server.requests += 1;
    return {
      ok: true,
      json: async () => ({
        mode: server.mode,
        username: server.username,
        isAuthenticated: server.mode === 'authenticated'
      })
    };
  };
  return server;
};

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const mount = (authMode, connectedAtMount) => {
  globalThis.__authMode = authMode;
  const component = createComponent();
  let value = null;
  const render = (isConnected) => {
    globalThis.__socketLive = isConnected;
    value = component.render(() => SteamAuthProvider({ children: null })).props.value;
    return value;
  };
  render(connectedAtMount);
  return { render, read: () => value };
};

test('an admin mounting into a live socket asks once, not twice', async () => {
  const server = startServer('authenticated', 'lanadmin');
  const provider = mount('authenticated', true);
  await settle();

  assert.equal(server.requests, 1, 'the mount fetch already had the subscription up');
  assert.equal(provider.render(true).steamAuthMode, 'authenticated');
  assert.equal(server.requests, 1, 'a re-render at the same connection state asks nothing');
});

test('a session dropped server-side while the socket was down is noticed on recovery', async () => {
  const server = startServer('authenticated', 'lanadmin');
  const provider = mount('authenticated', true);
  await settle();
  assert.equal(provider.render(true).username, 'lanadmin');

  provider.render(false);
  await settle();
  assert.equal(server.requests, 1, 'a drop asks for nothing');

  // SteamAutoLogout fires here and is never delivered.
  server.mode = 'anonymous';
  server.username = '';

  provider.render(true);
  await settle();

  assert.equal(server.requests, 2);
  const value = provider.render(true);
  assert.equal(value.steamAuthMode, 'anonymous', 'a Steam login offered here is already gone');
  assert.equal(value.username, '');
});

test('a guest never asks, at mount or on recovery', async () => {
  const server = startServer('authenticated', 'lanadmin');
  const provider = mount('guest', false);
  await settle();
  assert.equal(server.requests, 0, 'the status endpoint is not for guests');

  provider.render(true);
  await settle();

  assert.equal(server.requests, 0, 'and a recovered connection does not make it one');
  assert.equal(provider.read().isLoading, false, 'a guest is not left spinning');
});

test('a socket that comes up after an admin mounted refetches', async () => {
  const server = startServer('anonymous', '');
  const provider = mount('authenticated', false);
  await settle();
  assert.equal(server.requests, 1, 'the mount fetch runs whether or not the socket is up');

  // The login completes in the window between that fetch and the subscription going live.
  server.mode = 'authenticated';
  server.username = 'lanadmin';

  provider.render(true);
  await settle();

  assert.equal(server.requests, 2);
  assert.equal(provider.render(true).username, 'lanadmin');
});
