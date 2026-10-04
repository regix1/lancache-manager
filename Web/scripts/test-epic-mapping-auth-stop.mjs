import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
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
      canManage: globalThis.__server.canManage,
      ownershipReason: globalThis.__server.ownershipReason,
      attemptId: globalThis.__server.pendingAttempt,
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
    expiresAtUtc: globalThis.__server.expiresAtUtc
  }),
  completeEpicMappingAuth: async () => {
    globalThis.__server.codeChecks += 1;
    if (globalThis.__server.checkGate) {
      await globalThis.__server.checkGate;
    }
    throw globalThis.__server.checkError ?? Object.assign(new Error('Request cancelled'), { name: 'AbortError' });
  },
  cancelEpicMappingLogin: async () => {}
};
`);

const i18nStubUrl = toUrl(`
const translation = { t: (key) => key };
export const useTranslation = () => translation;
`);

/** The shape handleResponse gives a refused request: an ApiError whose body names the reason. */
const apiErrorUrl = toUrl(`
export class ApiError extends Error {
  constructor(message, body) {
    super(message);
    this.body = body;
  }
}
`);
const { ApiError } = await import(apiErrorUrl);

/** One hub across renders; the test emits the server's events and flips the connection. */
const hubUrl = toUrl(`
const handlers = new Map();
const hub = {
  on: (event, handler) => {
    handlers.set(event, [...(handlers.get(event) ?? []), handler]);
  },
  off: (event, handler) => {
    handlers.set(event, (handlers.get(event) ?? []).filter((entry) => entry !== handler));
  }
};
globalThis.__resetHub = () => handlers.clear();
globalThis.__emit = (event, payload) => {
  (handlers.get(event) ?? []).slice().forEach((handler) => handler(payload));
};
export const useSignalR = () => {
  hub.isConnected = globalThis.__socketLive;
  return hub;
};
`);

await import(hubUrl);

const hookAliases = {
  react: reactStubUrl,
  'react-i18next': i18nStubUrl,
  '@contexts/SignalRContext/useSignalR': hubUrl,
  './useReconnectRefetch': await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
    react: reactStubUrl
  })
};
hookAliases['./useSignInEndingWait'] = await compileToUrl(
  '../src/hooks/useSignInEndingWait.ts',
  hookAliases
);

/**
 * Holds timers of a minute or more: a sign-in's own deadline. A test runs it when it chooses, and none keeps the
 * process alive. A 5 second timer would be a retry loop, which the dialog does not have.
 */
const heldTimers = { retries: [], deadlines: [] };
const realSetTimeout = globalThis.setTimeout;
const realClearTimeout = globalThis.clearTimeout;
globalThis.setTimeout = (callback, delay, ...args) => {
  if (delay === 5000) {
    heldTimers.retries.push(callback);
    return 0;
  }
  if (delay >= 60000) {
    const held = { callback, delay, cleared: false };
    heldTimers.deadlines.push(held);
    return held;
  }
  return realSetTimeout(callback, delay, ...args);
};
globalThis.clearTimeout = (timer) => {
  if (timer && typeof timer === 'object' && 'cleared' in timer) timer.cleared = true;
  else realClearTimeout(timer);
};
const liveDeadlines = () => heldTimers.deadlines.filter((held) => !held.cleared);

const { useEpicMappingAuth } = await import(
  await compileToUrl('../src/hooks/useEpicMappingAuth.ts', {
    ...hookAliases,
    '@services/apiError': apiErrorUrl,
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
  globalThis.__resetHub();
  globalThis.__socketLive = true;
  heldTimers.retries.length = 0;
  heldTimers.deadlines.length = 0;
  globalThis.__server = {
    statusReads: [],
    statusFailing: false,
    isAuthenticated: false,
    canManage: true,
    ownershipReason: null,
    // The attempt the server still runs for this caller; the status answer names it until it ends.
    pendingAttempt: null,
    codeChecks: 0,
    // The server's 5 minute window for the pasted code, as the start answer reports it.
    expiresAtUtc: new Date(Date.now() + 5 * 60 * 1000).toISOString(),
    endings: {},
    checkGate: null,
    checkError: null
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

const asked = (server) => server.statusReads.filter((attemptId) => attemptId !== null).length;

const completedEnding = {
  attemptId: 'attempt-a',
  status: 'completed',
  stageKey: 'signalr.epicMapping.completed'
};

test('a code check whose answer was lost reads the sign-in that the server saved', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.checkError = new TypeError('Failed to fetch');
  server.isAuthenticated = true;
  server.endings['attempt-a'] = completedEnding;

  const accepted = await epic.read().actions.handleAuthenticate();

  assert.equal(accepted, true);
  assert.equal(epic.succeeded.count, 1);
  assert.equal(epic.failed.count, 0, 'the transport error is not what the server did');
});

test('a code the server refuses with its own reason shows that reason and reads nothing', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.checkError = new ApiError('That code was refused.', {
    stageKey: 'errors.epic.codeRefused'
  });

  const accepted = await epic.read().actions.handleAuthenticate();
  const after = epic.render();

  assert.equal(accepted, false);
  assert.equal(epic.failed.count, 1);
  assert.equal(epic.failed.message, 'Error: That code was refused.');
  assert.equal(after.state.needsAuthorizationCode, false);
  assert.equal(asked(server), 0);
});

test('a failed read of a stopped sign-in keeps the form until the server announces the ending', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;

  const accepted = await epic.read().actions.handleAuthenticate();
  const waiting = epic.render();

  assert.equal(accepted, false);
  assert.equal(
    waiting.state.needsAuthorizationCode,
    true,
    'the form stays while nothing is decided'
  );
  assert.equal(waiting.state.loading, true, 'the form stays busy while the ending is awaited');
  assert.equal(waiting.state.error, null);
  assert.equal(epic.failed.count, 0);
  assert.equal(
    asked(server),
    2,
    'one read when the answer was lost, one when the wait starts listening'
  );
  assert.equal(heldTimers.retries.length, 0, 'no timer reads again');

  server.statusFailing = false;
  server.endings['attempt-a'] = {
    attemptId: 'attempt-a',
    status: 'cancelled',
    stageKey: 'errors.integration.attemptExpired'
  };
  globalThis.__emit('IntegrationLoginEnded', { attemptId: 'someone-else' });
  await settle();
  assert.equal(asked(server), 2, 'another attempt ending is not this dialog');

  globalThis.__emit('IntegrationLoginEnded', { attemptId: 'attempt-a' });
  await settle();
  const ended = epic.render();

  assert.equal(asked(server), 3);
  assert.equal(epic.failed.message, 'errors.integration.attemptExpired');
  assert.equal(ended.state.needsAuthorizationCode, false);
  assert.equal(ended.state.error, 'errors.integration.attemptExpired');
});

test('a sign-in whose ending was missed is read once when the hub connection comes back', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  epic.render();
  server.statusFailing = false;
  server.isAuthenticated = true;
  server.endings['attempt-a'] = completedEnding;

  globalThis.__socketLive = false;
  epic.render();
  globalThis.__socketLive = true;
  epic.render();
  await settle();

  assert.equal(asked(server), 3);
  assert.equal(epic.succeeded.count, 1);
});

test('a sign-in nothing decided by its deadline ends as ended or expired', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  epic.render();

  const deadlines = liveDeadlines();
  assert.equal(deadlines.length, 1, 'one deadline timer for the waiting attempt');
  deadlines[0].callback();
  await settle();
  const ended = epic.render();

  assert.equal(asked(server), 3, 'the deadline reads once');
  assert.equal(epic.failed.message, 'errors.integration.attemptExpired');
  assert.equal(ended.state.needsAuthorizationCode, false);
});

test('a second Epic Submit while a lost answer is awaited sends nothing and the first one success shows', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  epic.render();
  assert.equal(epic.read().state.loading, true);
  assert.equal(server.codeChecks, 1);

  const accepted = await epic.read().actions.handleAuthenticate();
  assert.equal(accepted, false);
  assert.equal(server.codeChecks, 1, 'no second code check is sent');

  server.statusFailing = false;
  server.isAuthenticated = true;
  server.endings['attempt-a'] = completedEnding;
  globalThis.__emit('IntegrationLoginEnded', { attemptId: 'attempt-a' });
  await settle();

  assert.equal(epic.succeeded.count, 1);
  assert.equal(epic.failed.count, 0);
});

test('a sign-in ending pushed before the wait starts listening is read without a push', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  server.statusFailing = false;
  server.endings['attempt-a'] = {
    attemptId: 'attempt-a',
    status: 'cancelled',
    stageKey: 'errors.integration.attemptExpired'
  };

  epic.render();
  await settle();
  const ended = epic.render();

  assert.equal(epic.failed.message, 'errors.integration.attemptExpired');
  assert.equal(ended.state.needsAuthorizationCode, false);
});

test('a deadline read that still names the attempt keeps the Epic dialog waiting', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  epic.render();
  server.statusFailing = false;
  server.pendingAttempt = 'attempt-a';

  const deadlines = liveDeadlines();
  assert.equal(deadlines.length, 1, 'one deadline timer for the waiting attempt');
  deadlines[0].callback();
  await settle();
  const waiting = epic.render();

  assert.equal(epic.failed.count, 0);
  assert.equal(waiting.state.needsAuthorizationCode, true);
  assert.equal(waiting.state.error, null);

  // The server records no ending for an attempt no code request ran. The push comes from the window-end announcement in
  // EpicMappingService.Authentication.cs, and by then the status no longer names the attempt, so the read ends it as expired.
  server.pendingAttempt = null;
  globalThis.__emit('IntegrationLoginEnded', { attemptId: 'attempt-a' });
  await settle();
  assert.equal(epic.failed.message, 'errors.integration.attemptExpired');
});

/**
 * The wizard's Back cancels the attempt (EpicAuthStep handleRetry), so it stays usable while the dialog only waits for a
 * lost answer's ending. The hook reports that wait apart from a request in flight; the step reads it for Back.
 */
test('a lost Epic answer reports the wait so the setup wizard can leave it', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  const waiting = epic.render();

  assert.equal(waiting.state.loading, true);
  assert.equal(waiting.state.awaitingEnding, true);
  // A network drop during Submit fails the status read too, so the step cannot rely on a readable status.
  assert.equal(waiting.state.canAuthenticate, false);

  const stepSource = readFileSync(
    new URL('../src/components/initialization/steps/EpicAuthStep.tsx', import.meta.url),
    'utf8'
  );
  const back = /onClick=\{handleRetry\}\s+disabled=\{([^}]*)\}/.exec(stepSource);
  assert.ok(back, 'the Back button is found by its handler');
  assert.equal(
    new Function('state', `return (${back[1]});`)(waiting.state),
    false,
    'Back is usable during the wait'
  );
});

test('the Epic status is read again when the hub connection comes back', async () => {
  const server = startServer();
  server.statusFailing = true;
  const epic = await mount();
  assert.equal(epic.read().state.canAuthenticate, false);

  server.statusFailing = false;
  globalThis.__socketLive = false;
  epic.render();
  globalThis.__socketLive = true;
  epic.render();
  await settle();
  const after = epic.render();

  assert.equal(after.state.canAuthenticate, true);
});

test('a failed read after the server announces the ending ends the wait', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  assert.equal(epic.render().state.loading, true);

  globalThis.__emit('IntegrationLoginEnded', { attemptId: 'attempt-a' });
  await settle();
  const ended = epic.render();

  assert.equal(epic.failed.message, 'errors.integration.attemptExpired');
  assert.equal(ended.state.loading, false);
  assert.equal(heldTimers.retries.length, 0);
});

test('the Epic status is read again when a wait ends', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.statusFailing = true;
  await epic.read().actions.handleAuthenticate();
  epic.render();
  assert.equal(epic.read().state.canAuthenticate, false);

  server.statusFailing = false;
  server.pendingAttempt = null;
  globalThis.__emit('IntegrationLoginEnded', { attemptId: 'attempt-a' });
  await settle();
  await settle();
  const ended = epic.render();

  assert.equal(ended.state.canAuthenticate, true);
});

test('a saved sign-in that was signed out before the read shows the sign-in failed text', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.isAuthenticated = false;
  server.endings['attempt-a'] = completedEnding;

  const accepted = await epic.read().actions.handleAuthenticate();

  assert.equal(accepted, false);
  assert.equal(epic.succeeded.count, 0);
  assert.equal(epic.failed.message, 'modals.epicAuth.errors.loginFailed');
});

test('a saved sign-in beside another session pending sign-in still closes with success', async () => {
  const server = startServer();
  const epic = await mount();
  await reachCodeForm(epic);
  server.isAuthenticated = true;
  server.canManage = false;
  server.ownershipReason = 'login-in-progress';
  server.endings['attempt-a'] = completedEnding;

  const accepted = await epic.read().actions.handleAuthenticate();

  assert.equal(accepted, true);
  assert.equal(epic.succeeded.count, 1);
});
