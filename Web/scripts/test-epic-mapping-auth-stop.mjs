import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * The card's X, pressed in another tab, stops an Epic sign-in whose code the dialog is checking. The
 * server answers that request 499, which the api service turns into an AbortError. The hook is run
 * for real here: when a current request ends that way, the dialog reads how its own sign-in ended
 * by attempt id and shows it, instead of returning with a dead attempt and no message.
 */

const toUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

/** The smallest React that can run this hook: ordered slots, dependency comparison and a post-render effect flush. */
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
    queued.push(() => { slot.cleanup?.(); slot.cleanup = run(); });
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

/**
 * Like the server, the status answer carries the ending of the attempt it is asked about, and the
 * REST answer leaves out every null field (Program.cs DefaultIgnoreCondition = WhenWritingNull).
 * The code check is rejected the way handleResponse rejects a 499 (api.service.ts).
 */
const apiStubUrl = toUrl(`
export default {
  getEpicMappingAuthStatus: async (attemptId) => {
    globalThis.__server.statusReads.push(attemptId ?? null);
    if (globalThis.__server.statusFailing) {
      throw new Error('auth-status unavailable');
    }
    const body = {
      canManage: true,
      canSignIn: true,
      isAuthenticated: globalThis.__server.isAuthenticated,
      loginEnding: attemptId ? globalThis.__server.endings[attemptId] : null
    };
    return Object.fromEntries(
      Object.entries(body).filter(([, value]) => value !== null && value !== undefined)
    );
  },
  startEpicMappingLogin: async (_signal, request) => ({
    authorizationUrl: 'https://epic.example/auth',
    attemptId: request.attemptId,
    expiresAtUtc: '2030-01-01T00:00:00Z'
  }),
  completeEpicMappingAuth: async () => {
    if (globalThis.__server.checkGate) {
      await globalThis.__server.checkGate;
    }
    throw Object.assign(new Error('Request cancelled'), { name: 'AbortError' });
  },
  cancelEpicMappingLogin: async () => {}
};
`);

const i18nStubUrl = toUrl(`
const translation = { t: (key) => key };
export const useTranslation = () => translation;
`);

const { useEpicMappingAuth } = await import(
  await compileToUrl('../src/hooks/useEpicMappingAuth.ts', {
    react: reactStubUrl,
    'react-i18next': i18nStubUrl,
    '@services/api.service': apiStubUrl,
    '@utils/error': toUrl('export const getErrorMessage = (error) => String(error);'),
    '@contexts/useAuth': toUrl(
      "export const useAuth = () => ({authenticationEnabled:true,authMode:'authenticated',accountId:'a',sessionId:'a',isLoading:false});"
    ),
    '@utils/uuid': toUrl("export const createUuid = () => 'attempt-a';"),
    '../types': toUrl(
      "export const getIntegrationReasonKey = (reason) => { throw new Error('Unrecognized integration reason: ' + reason); };"
    )
  })
);

const startServer = () => {
  globalThis.__server = {
    statusReads: [],
    statusFailing: false,
    isAuthenticated: false,
    endings: {},
    checkGate: null
  };
  return globalThis.__server;
};

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const mount = async () => {
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
  const render = () => {
    hook = component.render(() => useEpicMappingAuth({ onSuccess, onError }));
    return hook;
  };
  render();
  await settle();
  render();
  return { render, read: () => hook, succeeded, failed };
};

/** Gets the dialog to the point where the code is typed and ready to submit. */
const reachCodeForm = async (epic) => {
  await epic.read().startLogin();
  epic.render();
  assert.equal(epic.read().state.needsAuthorizationCode, true);
  epic.read().actions.setAuthorizationCode('code-1');
  epic.render();
  globalThis.__server.statusReads.length = 0;
};

test('a sign-in stopped from another tab ends with its reason', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.endings['attempt-a'] = {
    attemptId: 'attempt-a',
    status: 'cancelled',
    stageKey: 'errors.integration.attemptExpired'
  };

  const accepted = await epic.read().actions.handleAuthenticate();
  const after = epic.render();

  assert.equal(accepted, false);
  assert.equal(epic.failed.count, 1);
  assert.equal(epic.failed.message, 'errors.integration.attemptExpired');
  assert.equal(after.state.needsAuthorizationCode, false);
  assert.equal(after.state.error, 'errors.integration.attemptExpired');
  assert.equal(epic.succeeded.count, 0);
});

test('a sign-in saved before the stop closes with success', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.isAuthenticated = true;
  server.endings['attempt-a'] = {
    attemptId: 'attempt-a',
    status: 'completed',
    stageKey: 'signalr.epicMapping.completed'
  };

  const accepted = await epic.read().actions.handleAuthenticate();

  assert.equal(accepted, true);
  assert.equal(epic.succeeded.count, 1);
  assert.equal(epic.failed.count, 0);
});

test('a dialog closed while its code is checked reads nothing', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  let release;
  server.checkGate = new Promise((resolve) => {
    release = resolve;
  });

  const submitted = epic.read().actions.handleAuthenticate();
  await settle();
  epic.render();
  epic.read().actions.resetAuthForm();
  release();
  const accepted = await submitted;

  assert.equal(accepted, false);
  assert.deepEqual(
    server.statusReads.filter((attemptId) => attemptId !== null),
    []
  );
  assert.equal(epic.failed.count, 0);
  assert.equal(epic.succeeded.count, 0);
});
