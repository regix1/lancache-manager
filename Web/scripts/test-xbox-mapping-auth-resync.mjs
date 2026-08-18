import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * Xbox mapping login is a device code flow: the backend polls Microsoft after the browser's own
 * request has already returned, so the browser learns the outcome from one SignalR event and nothing
 * else. A socket that drops during that wait loses it, and the modal keeps showing a code the user
 * has already approved. The hook is run for real here - compiled and driven through the sequence
 * that matters: login started, socket dropped, approved server-side, socket back.
 */

const toUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/**
 * The smallest React that can run this hook: ordered slots for `useState`, `useRef` and
 * `useCallback`, dependency comparison, and a post-render flush for `useEffect`.
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

const reactStubUrl = toUrl(reactStubSource);
const { createComponent } = await import(reactStubUrl);

/** What auth-status currently answers, how many times it has been asked, and whether it can. */
const apiStubUrl = toUrl(`
export default {
  getXboxMappingAuthStatus: async () => {
    globalThis.__server.requests += 1;
    if (globalThis.__server.gate) {
      await globalThis.__server.gate;
    }
    if (globalThis.__server.failing) {
      throw new Error('auth-status unavailable');
    }
    return {
      isAuthenticated: globalThis.__server.isAuthenticated,
      loginInProgress: globalThis.__server.loginInProgress
    };
  },
  startXboxMappingLogin: async () => {
    if (globalThis.__server.loginGate) {
      await globalThis.__server.loginGate;
    }
    return { userCode: 'ABC-123', verificationUri: 'https://aka.ms/link' };
  },
  cancelXboxMappingLogin: async () => {}
};
`);

/** One hub object across renders, so only the flag the hook reads changes. */
const signalRStubUrl = toUrl(`
const handlers = new Map();
const hub = {
  isConnected: false,
  on: (event, handler) => {
    handlers.set(event, [...(handlers.get(event) ?? []), handler]);
  },
  off: (event, handler) => {
    handlers.set(event, (handlers.get(event) ?? []).filter((entry) => entry !== handler));
  }
};
globalThis.__emit = (event, payload) => {
  (handlers.get(event) ?? []).slice().forEach((handler) => handler(payload));
};
export const useSignalR = () => {
  hub.isConnected = globalThis.__socketLive;
  return hub;
};
`);

/** Stable identities, so the subscription effect runs once and no handler is registered twice. */
const i18nStubUrl = toUrl(`
const translation = { t: (key) => key };
export const useTranslation = () => translation;
`);

const notificationsStubUrl = toUrl(`
const notifications = { addNotification: () => {} };
export const useNotifications = () => notifications;
`);

const errorHandlerStubUrl = toUrl(`
const handler = {
  notifyError: (message) => {
    globalThis.__reported.push(message);
  }
};
export const useErrorHandler = () => handler;
`);

const errorUtilStubUrl = toUrl(`export const getErrorMessage = (error) => String(error);`);

const reconnectUrl = await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
  react: reactStubUrl
});

const { useXboxMappingAuth } = await import(
  await compileToUrl('../src/hooks/useXboxMappingAuth.ts', {
    react: reactStubUrl,
    'react-i18next': i18nStubUrl,
    '@contexts/SignalRContext/useSignalR': signalRStubUrl,
    '@services/api.service': apiStubUrl,
    '@contexts/notifications': notificationsStubUrl,
    './useErrorHandler': errorHandlerStubUrl,
    './useReconnectRefetch': reconnectUrl,
    '@utils/error': errorUtilStubUrl
  })
);

/** Starts with a login attempt alive, which is what the backend reports for every scenario below
 *  that gets as far as showing a device code. Tests that end the attempt clear the flag. */
const startServer = (isAuthenticated) => {
  globalThis.__server = {
    requests: 0,
    isAuthenticated,
    loginInProgress: true,
    failing: false,
    gate: null,
    loginGate: null
  };
  globalThis.__reported = [];
  return globalThis.__server;
};

/** Holds every auth-status answer until the returned function is called. */
const holdAnswers = (server) => {
  let release;
  server.gate = new Promise((resolve) => {
    release = resolve;
  });
  return release;
};

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const mount = (connectedAtMount) => {
  const component = createComponent();
  const succeeded = { count: 0 };
  const failed = { count: 0, message: null };
  const onSuccess = () => {
    succeeded.count += 1;
  };
  const onError = (message) => {
    failed.count += 1;
    failed.message = message;
  };
  let hook = null;
  const render = (isConnected) => {
    globalThis.__socketLive = isConnected;
    hook = component.render(() => useXboxMappingAuth({ onSuccess, onError }));
    return hook;
  };
  render(connectedAtMount);
  return { render, read: () => hook, succeeded, failed };
};

/** Gets a login to the point where the device code is on screen and the backend is polling. */
const waitForApproval = async (xbox) => {
  await xbox.read().startLogin();
  const started = xbox.render(true);
  assert.equal(started.state.needsDeviceCode, true);
  globalThis.__emit('XboxMappingAuthStateChanged', { status: 'waiting' });
};

test('an approval that landed while the socket was down is picked up on recovery', async () => {
  const server = startServer(false);
  const xbox = mount(true);
  await waitForApproval(xbox);

  xbox.render(false);
  // The user approves the code here and the backend's completed event lands on a dead socket. The
  // attempt is over by then, so the backend reports it finished and signed in.
  server.isAuthenticated = true;
  server.loginInProgress = false;

  xbox.render(true);
  await settle();

  assert.equal(server.requests, 1);
  assert.equal(
    xbox.succeeded.count,
    1,
    'the login ends the same way the event would have ended it'
  );
  const recovered = xbox.render(true);
  assert.equal(recovered.state.needsDeviceCode, false, 'the modal drops the dead device code');
  assert.equal(recovered.state.loading, false);
});

test('no login in flight means no request on recovery', async () => {
  const server = startServer(true);
  const xbox = mount(true);

  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 0, 'a resync with no login to settle decides nothing');
  assert.equal(xbox.succeeded.count, 0);
});

test('a recovery before the user has approved leaves the code on screen', async () => {
  const server = startServer(false);
  const xbox = mount(true);
  await waitForApproval(xbox);

  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 1);
  assert.equal(xbox.succeeded.count, 0);
  assert.equal(xbox.failed.count, 0, 'a poll still waiting for the user is not a dead login');
  const waiting = xbox.render(true);
  assert.equal(waiting.state.needsDeviceCode, true);
  assert.equal(waiting.state.deviceUserCode, 'ABC-123', 'the code the user is still typing in');
  assert.equal(waiting.state.error, null);
});

test('a login busy with the catalog after approval is not reported dead', async () => {
  // The stretch between approval and the completed event: signed in is still false, because the
  // backend only writes it once the catalog is merged, and there is no second SignalR event to say
  // so. Only loginInProgress separates this from a login that died, and it is the whole reason the
  // resync can end one at all.
  const server = startServer(false);
  const xbox = mount(true);
  await waitForApproval(xbox);

  server.isAuthenticated = false;
  server.loginInProgress = true;

  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 1);
  assert.equal(xbox.failed.count, 0, 'a login mid-harvest must keep waiting, not fail');
  assert.equal(xbox.succeeded.count, 0);
  const busy = xbox.render(true);
  assert.equal(busy.state.needsDeviceCode, true);
  assert.equal(busy.state.error, null);
});

test('a login that died while the socket was down ends on recovery', async () => {
  const server = startServer(false);
  const xbox = mount(true);
  await waitForApproval(xbox);

  xbox.render(false);
  // The poll gave up server-side (declined, or the code expired) and its failed event landed on a
  // dead socket. Nothing else can ever end this login.
  server.loginInProgress = false;

  xbox.render(true);
  await settle();

  assert.equal(server.requests, 1);
  assert.equal(xbox.succeeded.count, 0);
  assert.equal(xbox.failed.count, 1, 'the login ends the way the failed event would have ended it');
  assert.equal(xbox.failed.message, 'modals.xboxAuth.errors.loginFailed');
  const ended = xbox.render(true);
  assert.equal(ended.state.needsDeviceCode, false, 'the modal drops the dead device code');
  assert.equal(ended.state.loading, false);
  assert.equal(ended.state.error, 'modals.xboxAuth.errors.loginFailed');

  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 1, 'the login is over, so a later recovery asks nothing');
  assert.equal(xbox.failed.count, 1);
});

test('a recovery while the login request is still on the wire decides nothing', async () => {
  // The browser marks a login live before the POST goes out, so the backend has not registered the
  // attempt yet and truthfully answers "no login running". Failing here would kill a login that is
  // about to start; the POST's own rejection is what handles it going wrong.
  const server = startServer(false);
  server.loginInProgress = false;
  let releaseLogin;
  server.loginGate = new Promise((resolve) => {
    releaseLogin = resolve;
  });

  const xbox = mount(true);
  const started = xbox.read().startLogin();

  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 1, 'the recovery asked while the login POST was still out');
  assert.equal(xbox.failed.count, 0, 'a login the backend has not seen yet is not a dead login');

  server.loginInProgress = true;
  releaseLogin();
  await started;

  const showing = xbox.render(true);
  assert.equal(showing.state.needsDeviceCode, true, 'the device code still arrives');
  assert.equal(showing.state.error, null);
});

test('the completed event ends the login once, leaving nothing for a later recovery', async () => {
  const server = startServer(false);
  const xbox = mount(true);
  await waitForApproval(xbox);

  globalThis.__emit('XboxMappingAuthStateChanged', { status: 'completed' });
  assert.equal(xbox.succeeded.count, 1);
  assert.equal(xbox.render(true).state.needsDeviceCode, false);

  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 0, 'the login is over, so the resync has nothing to ask about');
  assert.equal(xbox.succeeded.count, 1);
});

test('a login the user backs out of while the ask is on the wire does not complete', async () => {
  const server = startServer(true);
  const release = holdAnswers(server);
  const xbox = mount(true);
  await waitForApproval(xbox);

  xbox.render(false);
  xbox.render(true);
  assert.equal(server.requests, 1, 'the recovery asked and the answer is still out');

  xbox.read().actions.cancelPendingRequest();
  release();
  await settle();

  assert.equal(
    xbox.succeeded.count,
    0,
    'closing the modal aborts nothing here, so only re-reading the flag can stop the answer'
  );
  assert.equal(xbox.render(true).state.needsDeviceCode, false, 'the modal stays closed');
});

test('two recoveries with the ask still out complete the login once', async () => {
  const server = startServer(true);
  const release = holdAnswers(server);
  const xbox = mount(true);
  await waitForApproval(xbox);

  xbox.render(false);
  xbox.render(true);
  xbox.render(false);
  xbox.render(true);
  assert.equal(server.requests, 2, 'each recovery asks, and neither answer is back yet');

  release();
  await settle();

  assert.equal(xbox.succeeded.count, 1, 'one login is one onSuccess, however many answers arrive');
});

test('a status ask that fails keeps the login for the next recovery', async () => {
  const server = startServer(true);
  server.failing = true;
  const xbox = mount(true);
  await waitForApproval(xbox);

  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 1);
  assert.equal(globalThis.__reported.length, 1, 'the failed ask is reported, not swallowed');
  assert.equal(xbox.succeeded.count, 0);
  assert.equal(xbox.render(true).state.needsDeviceCode, true);

  server.failing = false;
  xbox.render(false);
  xbox.render(true);
  await settle();

  assert.equal(server.requests, 2);
  assert.equal(xbox.succeeded.count, 1);
});
