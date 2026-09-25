import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { compileToUrl, moduleUrl, transpile, MemoryStorage } from './transpile-module.mjs';

const reactUrl = moduleUrl(`
let slots, cursor, effects;
export const useRef = value => { const i=cursor++; return slots[i] ??= {current:value}; };
export const useState = initial => {
  const i=cursor++; const slot=slots[i] ??= {value:typeof initial==='function'?initial():initial};
  return [slot.value, value=>{slot.value=typeof value==='function'?value(slot.value):value;}];
};
export const useCallback = (fn,deps) => {
  const i=cursor++; const slot=slots[i];
  if(!slot || deps.some((d,j)=>!Object.is(d,slot.deps[j]))) slots[i]={fn,deps};
  return slots[i].fn;
};
export const useMemo = (fn,deps) => {
  const i=cursor++; const slot=slots[i];
  if(!slot || deps.some((d,j)=>!Object.is(d,slot.deps[j]))) slots[i]={value:fn(),deps};
  return slots[i].value;
};
export const useEffect = (run,deps) => {
  const i=cursor++; const slot=slots[i] ??= {};
  if(!slot.deps || deps.some((d,j)=>!Object.is(d,slot.deps[j]))) {
    slot.deps=deps; effects.push({slot,run});
  }
};
export const useSyncExternalStore = (_subscribe,read) => read();
export const createComponent = () => {
  const saved=[];
  return { render(body) { slots=saved;cursor=0;effects=[];const result=body();effects.forEach(({slot})=>slot.cleanup?.());effects.forEach(({slot,run})=>{slot.cleanup=run();});return result; },
    unmount(){saved.forEach(slot=>slot.cleanup?.());} };
};
`);
const { createComponent } = await import(reactUrl);
const timeoutUrl = await compileToUrl('../src/hooks/loginAttemptTimeout.ts');
const authStageUrl = await compileToUrl('../src/hooks/authStage.ts');
const { getAuthStage } = await import(authStageUrl);
const aliases = {
  react: reactUrl,
  'react-i18next': moduleUrl(
    'export const useTranslation=()=>({t:globalThis.loginTest.translate});'
  ),
  '@contexts/notifications': moduleUrl(
    'const n={addNotification:()=>"card",removeNotification(){}};export const useNotifications=()=>n;'
  ),
  './useErrorHandler': moduleUrl(
    'const n={notifyError(...args){globalThis.loginTest.notified?.push(args);},notifySuccess(){}};export const useErrorHandler=()=>n;export const useNotifySuccess=()=>n;'
  ),
  '@utils/error': moduleUrl('export const getErrorMessage=error=>error.message;'),
  './loginAttemptTimeout': timeoutUrl,
  './authStage': authStageUrl,
  '@components/features/prefill/hooks/prefillConstants': moduleUrl(
    'export const getEventName=name=>name;'
  )
};
const compileGuest = async (source) => {
  let output = transpile(source);
  for (const [name, url] of Object.entries(aliases))
    output = output.split(`from '${name}'`).join(`from '${url}'`);
  return (await import(moduleUrl(output))).usePrefillSteamAuth;
};
const guestPath = '../src/hooks/usePrefillSteamAuth.ts';
const useGuest = await compileGuest(readFileSync(new URL(guestPath, import.meta.url), 'utf8'));

const messages = {
  noResult: 'prefill.persistent.errors.noResult',
  timedOut: 'prefill.persistent.loginTimedOut'
};
const storePath =
  process.env.PERSISTENT_LOGIN_STORE_SOURCE ??
  '../src/components/features/management/schedules/scheduled-prefill/persistentLoginStore.ts';
const hookPath =
  process.env.PERSISTENT_LOGIN_HOOK_SOURCE ?? '../src/hooks/usePersistentPrefillAuth.ts';
let sequence = 0;
async function persistent(storage, service = 'Steam', rearm = false) {
  const id = ++sequence;
  const page = new EventTarget();
  const subscriptions = [];
  globalThis.window = {
    addEventListener(name, handler) {
      subscriptions.push([name, handler]);
      page.addEventListener(name, handler);
    }
  };
  globalThis.sessionStorage = storage;
  globalThis.localStorage = new MemoryStorage();
  const calls = [];
  let reply = { ...challenge('first', 'password'), sessionId: 'session' };
  let cancel = async () => true;
  let poll = async () => reply;
  let start = async () => reply;
  let provide = async () => undefined;
  globalThis.loginTest = {
    translate: (key) => key,
    api: {
      startPersistentLogin: async (...args) => {
        calls.push(['start', ...args]);
        return start();
      },
      getPersistentChallenge: async (...args) => {
        calls.push(['poll', ...args]);
        return poll();
      },
      providePersistentCredential: async (...args) => {
        calls.push(['submit', ...args]);
        return provide(...args);
      },
      cancelPersistentLogin: async (...args) => {
        calls.push(['cancel', ...args]);
        return cancel();
      }
    }
  };
  const apiUrl = moduleUrl(
    'export default new Proxy({}, {get:(_target,key)=>(...args)=>globalThis.loginTest.api[key](...args)});'
  );
  const storageUrl = `${await compileToUrl('../src/utils/storage.ts')}#${id}`;
  const storeAliases = {
    react: reactUrl,
    '@services/api.service': apiUrl,
    '@utils/storage': storageUrl,
    '@hooks/authStage': authStageUrl,
    './typeGuards': await compileToUrl(
      '../src/components/features/management/schedules/scheduled-prefill/typeGuards.ts'
    )
  };
  let storeUrl = `${await compileToUrl(storePath, storeAliases)}#${id}`;
  if (rearm) {
    const source = readFileSync(new URL(storePath, import.meta.url), 'utf8');
    let copy = source
      .replace(
        'armPersistentLoginTimeout(service, current.loginDeadline, messages);',
        'armPersistentLoginTimeout(service, Date.now() + 600000, messages);'
      )
      .replace(
        'armPersistentLoginTimeout(service, deadline, messages);',
        'if (active !== undefined) armPersistentLoginTimeout(service, deadline, messages);'
      )
      .replace('if (clock) deadline = Math.min(deadline, clock.deadline);', '');
    if (rearm === 'continuous') {
      copy = source
        .replaceAll('sessionStore.removeItem(`persistent-login-deadline:${service}`)', 'true')
        .replace(
          'clockMessages.set(service, messages);',
          'clockMessages.set(service, messages); sessionStore.setJSON(`persistent-login-deadline:${service}`, admitted);'
        )
        .replace(
          "window.addEventListener('pagehide', () => {",
          "window.addEventListener('pagehide', () => { return;"
        );
    }
    assert.notEqual(copy, source);
    let output = transpile(copy);
    for (const [name, url] of Object.entries(storeAliases))
      output = output.split(`from '${name}'`).join(`from '${url}'`);
    storeUrl = `${moduleUrl(output)}#${id}`;
  }
  const store = await import(storeUrl);
  const hooks = await import(
    await compileToUrl(hookPath, {
      react: reactUrl,
      'react-i18next': aliases['react-i18next'],
      '@services/api.service': apiUrl,
      '@services/apiError': moduleUrl('export class ApiError extends Error {}'),
      '@utils/error': aliases['@utils/error'],
      '@utils/uuid': await compileToUrl('../src/utils/uuid.ts'),
      './loginAttemptTimeout': timeoutUrl,
      './authStage': authStageUrl,
      '@components/features/management/schedules/scheduled-prefill/persistentLoginStore': storeUrl
    })
  );
  const instance = createComponent();
  const render = () => instance.render(() => hooks.usePersistentPrefillAuth({ service }));
  const host = await import(
    await compileToUrl(
      '../src/components/features/management/schedules/scheduled-prefill/login/usePersistentLoginHost.ts',
      {
        react: reactUrl,
        'react-i18next': aliases['react-i18next'],
        '../persistentLoginStore': storeUrl
      }
    )
  );
  const hostInstance = createComponent();
  const handlers = new Map();
  const signalUrl = await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/usePersistentLoginChallengeSignalR.ts',
    {
      react: reactUrl,
      'react-i18next': aliases['react-i18next'],
      '@contexts/SignalRContext/useSignalR': moduleUrl(
        `export const useSignalR=()=>globalThis.loginTest.signal;`
      ),
      '@services/api.service': apiUrl,
      './persistentLoginStore': storeUrl,
      './constants': moduleUrl(
        "export const SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS=['steam','epic','xbox'];"
      ),
      './scheduledPrefillPlatformUi': moduleUrl(
        'export const getPersistentServiceId=value=>value[0].toUpperCase()+value.slice(1);'
      ),
      './persistentPrefillSignalREvents': moduleUrl(
        "export const getPersistentPrefillCredentialChallengeEvent=service=>'challenge:'+service;export const getPersistentPrefillAuthStateChangedEvent=service=>'auth:'+service;"
      )
    }
  );
  const signal = await import(signalUrl);
  globalThis.loginTest.signal = {
    on(name, handler) {
      handlers.set(name, handler);
    },
    off(name) {
      handlers.delete(name);
    }
  };
  const listener = createComponent();
  listener.render(() =>
    signal.usePersistentLoginChallengeSignalR({
      enabled: true,
      containersByService: new Map([[service, { sessionId: 'session' }]])
    })
  );
  return {
    store,
    storage: (await import(storageUrl)).sessionStore,
    hide: () => page.dispatchEvent(new Event('pagehide')),
    show: () => page.dispatchEvent(new Event('pageshow')),
    depart() {
      subscriptions.forEach(([name, handler]) => page.removeEventListener(name, handler));
      instance.unmount();
      listener.unmount();
      hostInstance.unmount();
    },
    render,
    calls,
    handlers,
    reveal() {
      const state = render().state;
      return hostInstance.render(() =>
        host.usePersistentLoginHost({
          service,
          state,
          startLogin: async () => {
            calls.push(['host-start']);
          },
          resumeModal: () => {
            calls.push(['resume']);
          },
          isRunning: true,
          isAuthenticated: false,
          onAuthenticated: () => undefined,
          autoStart: true
        })
      );
    },
    get reply() {
      return reply;
    },
    set reply(value) {
      reply = value;
    },
    set poll(value) {
      poll = value;
    },
    set provide(value) {
      provide = value;
    },
    set cancel(value) {
      cancel = value;
    },
    set startReply(value) {
      start = value;
    },
    async start() {
      store.setPersistentLoginStartSessionId(service, 'session');
      await render().actions.start();
      return store.getPersistentLoginState(service);
    },
    async restore() {
      return store.reconcilePersistentLoginFromServer(service, 'session', messages);
    },
    close() {
      store.resetPersistentLoginState(service);
      instance.unmount();
      listener.unmount();
      hostInstance.unmount();
      subscriptions.forEach(([name, handler]) => page.removeEventListener(name, handler));
    }
  };
}

function clock() {
  const original = {
    Date: globalThis.Date,
    setTimeout: globalThis.setTimeout,
    clearTimeout: globalThis.clearTimeout
  };
  let now = Date.parse('2026-09-13T12:00:00Z');
  let next = 0;
  const timers = new Map();
  globalThis.Date = class extends original.Date {
    constructor(...args) {
      super(...(args.length ? args : [now]));
    }
    static now() {
      return now;
    }
  };
  globalThis.setTimeout = (run, delay) => {
    const id = ++next;
    timers.set(id, { run, at: now + Math.max(0, delay) });
    return id;
  };
  globalThis.clearTimeout = (id) => timers.delete(id);
  return {
    get now() {
      return now;
    },
    timers,
    async advance(ms) {
      const end = now + ms;
      while (true) {
        const entry = [...timers]
          .filter(([, timer]) => timer.at <= end)
          .sort((a, b) => a[1].at - b[1].at)[0];
        if (!entry) break;
        now = entry[1].at;
        timers.delete(entry[0]);
        entry[1].run();
        await Promise.resolve();
        await Promise.resolve();
      }
      now = end;
      await Promise.resolve();
      await Promise.resolve();
    },
    restore() {
      Object.assign(globalThis, original);
    }
  };
}

function challenge(id, type = 'device-confirmation', expiry = Date.now() + 86400000) {
  return {
    type: 'challenge',
    challengeId: id,
    credentialType: type,
    serverPublicKey: 'key',
    createdAt: new Date().toISOString(),
    expiresAt: new Date(expiry).toISOString(),
    operationId: 'operation',
    loginAttempt: 7
  };
}

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function guest(useHook = useGuest, serviceId = 'steam') {
  globalThis.loginTest = { translate: (key) => key };
  const instance = createComponent();
  const calls = [];
  const outcomes = { success: 0, errors: [] };
  let reply = challenge('first', serviceId === 'xbox' ? 'device-code' : 'device-confirmation');
  let cancel = async () => undefined;
  let provide = async () => undefined;
  let waits = [];
  const makeSocket = () => {
    const handlers = new Map();
    return {
      handlers,
      on(name, fn) {
        handlers.set(name, fn);
      },
      off(name) {
        handlers.delete(name);
      },
      async invoke(name, ...args) {
        calls.push([name, ...args]);
        if (name === 'StartLoginAsync') return reply;
        if (name === 'CancelLoginAsync') return cancel();
        if (name === 'ProvideCredentialAsync') return provide(...args);
        if (name === 'WaitForChallengeAsync') return waits.shift() ?? null;
        return null;
      }
    };
  };
  let socket = makeSocket();
  const render = () =>
    instance.render(() =>
      useHook({
        sessionId: 'session',
        hubConnection: socket,
        serviceId,
        onSuccess: () => outcomes.success++,
        onError: (message) => outcomes.errors.push(message)
      })
    );
  return {
    calls,
    outcomes,
    render,
    get socket() {
      return socket;
    },
    set reply(value) {
      reply = value;
    },
    set cancel(value) {
      cancel = value;
    },
    set provide(value) {
      provide = value;
    },
    wait(...values) {
      waits.push(...values);
    },
    reconnect() {
      socket = makeSocket();
      render();
    },
    async start() {
      let result = render();
      result.actions.setUsername('user');
      result.actions.setPassword('password');
      result = render();
      await result.actions.handleAuthenticate();
      render();
      return render();
    },
    unmount() {
      instance.unmount();
    }
  };
}

for (const [service, duration] of [
  ['steam', 120000],
  ['xbox', 600000]
]) {
  test(`${service} duplicate, translation and socket changes retain the accepted wait`, async () => {
    const time = clock();
    const flow = guest(useGuest, service);
    try {
      const start = time.now;
      const first = await flow.start();
      assert.equal(first.loginDeadline, start + duration);
      await time.advance(90000);
      globalThis.loginTest.translate = (key) => key;
      flow.render();
      flow.reconnect();
      void flow.socket.handlers.get('CredentialChallenge')({
        sessionId: 'session',
        challenge: challenge('first', service === 'xbox' ? 'device-code' : 'device-confirmation')
      });
      flow.render();
      assert.equal(flow.render().loginDeadline, start + duration);
      await time.advance(duration - 90000);
      assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 1);
      assert.equal(flow.render().loginDeadline, null);
    } finally {
      flow.unmount();
      time.restore();
    }
  });
}

test('same-instant guest challenge replacement keeps its terminal armed', async () => {
  const time = clock();
  const flow = guest();
  try {
    await flow.start();
    void flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: challenge('second')
    });
    flow.render();
    await time.advance(120000);
    assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 1);
  } finally {
    flow.unmount();
    time.restore();
  }
});

test('new guest challenge owns a new wait and an earlier raw expiry narrows it', async () => {
  const time = clock();
  const flow = guest();
  try {
    await flow.start();
    await time.advance(90000);
    void flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: challenge('second', 'device-confirmation', time.now + 10000)
    });
    flow.render();
    assert.equal(flow.render().loginDeadline, time.now + 10000);
    await time.advance(10000);
    assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 1);
  } finally {
    flow.unmount();
    time.restore();
  }
});

test('late guest cancellation and confirmation cannot clear or acknowledge a retry', async () => {
  const time = clock();
  const flow = guest();
  try {
    await flow.start();
    let release;
    flow.cancel = () =>
      new Promise((resolve) => {
        release = resolve;
      });
    await time.advance(120000);
    flow.render().actions.resetAuthForm();
    flow.reply = challenge('retry');
    const retry = await flow.start();
    release();
    await Promise.resolve();
    await Promise.resolve();
    assert.equal(flow.render().loginDeadline, retry.loginDeadline);
    void flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: challenge('retry')
    });
    flow.render().actions.cancelPendingRequest();
    await time.advance(300);
    assert.equal(flow.calls.filter(([name]) => name === 'ProvideCredentialAsync').length, 0);
    assert.equal(flow.render().loginDeadline, null);
  } finally {
    flow.unmount();
    time.restore();
  }
});

test('invalid guest expiry is refused at admission', async () => {
  const time = clock();
  const flow = guest();
  try {
    flow.reply = { ...challenge('invalid'), expiresAt: 'invalid' };
    const result = await flow.start();
    assert.equal(result.loginDeadline, null);
    assert.equal(result.state.error, 'prefill.auth.errors.noChallenge');
  } finally {
    flow.unmount();
    time.restore();
  }
});

test('expired guest challenge terminates once and stale deliveries cannot reopen it', async () => {
  const time = clock();
  const flow = guest();
  try {
    flow.reply = challenge('past', 'device-confirmation', time.now - 1);
    await flow.start();
    await time.advance(0);
    assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 1);
    const handler = flow.socket.handlers.get('CredentialChallenge');
    void handler({ sessionId: 'session', challenge: challenge('past') });
    flow.render();
    assert.equal(flow.render().loginDeadline, null);
    assert.equal(flow.render().state.waitingForMobileConfirmation, false);
  } finally {
    flow.unmount();
    time.restore();
  }
});

test('guest unmount cancels its timer and an unowned delivery cannot start a wait', async () => {
  const time = clock();
  const flow = guest();
  try {
    await flow.start();
    flow.unmount();
    await time.advance(120000);
    assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 0);
    const remount = guest();
    remount.render();
    void remount.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: challenge('first')
    });
    assert.equal(remount.render().loginDeadline, null);
    remount.unmount();
  } finally {
    time.restore();
  }
});

test('duration-based effect rearming extends a Steam wait on translation change', async () => {
  const source = readFileSync(new URL(guestPath, import.meta.url), 'utf8')
    .replace(
      'if (waitingForMobileConfirmation && sessionId && loginDeadline !== null) {',
      'if (waitingForMobileConfirmation && sessionId) { setLoginDeadline(Date.now() + STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS);'
    )
    .replace('Math.max(0, loginDeadline - Date.now())', 'STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS')
    .replace(
      '[waitingForMobileConfirmation, sessionId, loginDeadline, notifyError, endLoginCard, t]',
      '[waitingForMobileConfirmation, sessionId, notifyError, endLoginCard, t]'
    );
  const oldHook = await compileGuest(source);
  const time = clock();
  const flow = guest(oldHook);
  try {
    const start = time.now;
    await flow.start();
    await time.advance(90000);
    globalThis.loginTest.translate = (key) => key;
    flow.render();
    assert.equal(flow.render().loginDeadline, start + 210000);
    await time.advance(30000);
    assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 0);
  } finally {
    flow.unmount();
    time.restore();
  }
});

test('guest Steam credentials stay pending through same-stage and empty challenge reads', async () => {
  const flow = guest();
  try {
    const username = challenge('username', 'username');
    const password = challenge('password', 'password');
    flow.reply = username;
    flow.wait(password, { ...password, challengeId: 'password-repeat' });

    let auth = flow.render();
    auth.actions.setUsername('steam-user');
    auth.actions.setPassword('steam-password');
    auth = flow.render();
    await auth.actions.handleAuthenticate();
    auth = flow.render();

    assert.equal(auth.state.loading, true);
    assert.equal(auth.outcomes?.success ?? flow.outcomes.success, 0);
    assert.equal(flow.calls.filter(([name]) => name === 'ProvideCredentialAsync').length, 2);
    await auth.actions.handleAuthenticate();
    assert.equal(flow.calls.filter(([name]) => name === 'ProvideCredentialAsync').length, 2);
  } finally {
    flow.unmount();
  }
});

test('a failed sign-in shows its reason in the open dialog and raises no popup behind it', async () => {
  const flow = guest(useGuest, 'steam');
  globalThis.loginTest.notified = [];
  try {
    flow.reply = challenge('username', 'username');
    flow.provide = async () => {
      throw new Error('Hub down');
    };
    const auth = await flow.start();

    assert.deepEqual(globalThis.loginTest.notified, []);
    assert.equal(auth.state.error, 'Hub down');
    assert.deepEqual(flow.outcomes.errors, ['Hub down']);
  } finally {
    flow.unmount();
  }
});

test('guest stage changes release one accepted step and authenticated terminal settles once', async () => {
  for (const [currentType, nextType, field, nextFlag] of [
    ['2fa', 'steamguard', 'setTwoFactorCode', 'needsEmailCode'],
    ['steamguard', '2fa', 'setEmailCode', 'needsTwoFactor']
  ]) {
    const flow = guest();
    try {
      const current = challenge(`${currentType}-first`, currentType);
      flow.reply = current;
      let auth = await flow.start();
      auth.actions[field]('12345');
      auth = flow.render();
      await auth.actions.handleAuthenticate();
      await flow.socket.handlers.get('CredentialChallenge')({
        sessionId: 'session',
        challenge: current
      });
      await flow.socket.handlers.get('CredentialChallenge')({
        sessionId: 'session',
        challenge: { ...current, challengeId: `${currentType}-replacement` }
      });
      assert.equal(flow.render().state.loading, true);

      await flow.socket.handlers.get('CredentialChallenge')({
        sessionId: 'session',
        challenge: challenge(`${nextType}-next`, nextType)
      });
      auth = flow.render();
      assert.equal(auth.state.loading, false);
      assert.equal(auth.state[nextFlag], true);

      flow.socket.handlers.get('AuthStateChanged')({
        sessionId: 'session',
        authState: 'Authenticated'
      });
      flow.socket.handlers.get('AuthStateChanged')({
        sessionId: 'session',
        authState: 'Authenticated'
      });
      assert.equal(flow.outcomes.success, 1);
    } finally {
      flow.unmount();
    }
  }
});

for (const [type, field] of [
  ['2fa', 'setTwoFactorCode'],
  ['steamguard', 'setEmailCode']
]) {
  test(`guest ${type} submission stays pending when the challenge wait is empty`, async () => {
    const flow = guest();
    try {
      flow.reply = challenge(type, type);
      let auth = await flow.start();
      auth.actions[field]('12345');
      auth = flow.render();
      await auth.actions.handleAuthenticate();
      auth = flow.render();

      assert.equal(auth.state.loading, true);
      assert.equal(type === '2fa' ? auth.state.needsTwoFactor : auth.state.needsEmailCode, true);
      assert.equal(flow.outcomes.success, 0);
    } finally {
      flow.unmount();
    }
  });
}

test('guest Epic code stays pending when its authorization challenge is redelivered', async () => {
  const flow = guest(useGuest, 'epic');
  try {
    const authorization = {
      ...challenge('epic-code', 'authorization-url'),
      authUrl: 'https://example.test/epic'
    };
    flow.reply = authorization;
    let auth = await flow.start();
    auth.actions.setAuthorizationCode('accepted-code');
    auth = flow.render();
    let release;
    flow.provide = () =>
      new Promise((resolve) => {
        release = resolve;
      });
    const submitted = auth.actions.handleAuthenticate();
    await Promise.resolve();
    const duplicate = auth.actions.handleAuthenticate();
    await duplicate;
    assert.equal(flow.calls.filter(([name]) => name === 'ProvideCredentialAsync').length, 1);
    await flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: authorization
    });
    assert.equal(flow.render().state.loading, true);
    release();
    await submitted;
    await flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: { ...authorization, challengeId: 'epic-code-repeat' }
    });
    auth = flow.render();
    assert.equal(auth.state.loading, true);
    assert.equal(auth.state.error, null);
    assert.equal(flow.outcomes.success, 0);
  } finally {
    flow.unmount();
  }
});

test('guest next-stage delivery fences a late credential failure', async () => {
  const flow = guest();
  try {
    const old = deferred();
    flow.reply = challenge('two-factor-first', '2fa');
    let auth = await flow.start();
    auth.actions.setTwoFactorCode('12345');
    flow.provide = () => old.promise;
    auth = flow.render();
    const submitted = auth.actions.handleAuthenticate();
    await Promise.resolve();

    await flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: challenge('email-next', 'steamguard')
    });
    old.reject(new Error('old failure'));
    await submitted;

    auth = flow.render();
    assert.equal(auth.state.needsEmailCode, true);
    assert.equal(auth.state.loading, false);
    assert.equal(auth.state.error, null);
    assert.deepEqual(flow.outcomes.errors, []);
  } finally {
    flow.unmount();
  }
});

test('guest quick stage keeps the shortest accepted expiry and ends once', async () => {
  const time = clock();
  const flow = guest();
  try {
    const initial = challenge('two-factor-first', '2fa', time.now + 10000);
    flow.reply = initial;
    let auth = await flow.start();
    auth.actions.setTwoFactorCode('12345');
    auth = flow.render();
    await auth.actions.handleAuthenticate();
    flow.render();

    await flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: challenge('two-factor-shorter', '2fa', time.now + 5000)
    });
    flow.render();
    await flow.socket.handlers.get('CredentialChallenge')({
      sessionId: 'session',
      challenge: challenge('two-factor-later', '2fa', time.now + 20000)
    });
    flow.render();
    await time.advance(5000);

    auth = flow.render();
    assert.equal(auth.state.loading, false);
    assert.equal(auth.state.error, 'prefill.auth.errors.noChallenge');
    assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 1);
    await time.advance(20000);
    assert.equal(flow.calls.filter(([name]) => name === 'CancelLoginAsync').length, 1);
  } finally {
    flow.unmount();
    time.restore();
  }
});

test('guest Xbox initial action stays pending after an empty challenge read', async () => {
  const flow = guest(useGuest, 'xbox');
  try {
    flow.reply = null;
    const auth = await flow.start();
    assert.equal(auth.state.loading, true);
    assert.equal(auth.state.needsDeviceCode, false);
    assert.equal(flow.outcomes.success, 0);
  } finally {
    flow.unmount();
  }
});

test('persistent same-stage poll and SignalR delivery retain the accepted step and deadline', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    const deadline = flow.store.getPersistentLoginState('Steam').loginDeadline;
    flow.poll = async () => flow.reply;
    await flow.render().actions.submit('credential');
    assert.equal(flow.store.getPersistentLoginState('Steam').loading, true);

    flow.handlers.get('challenge:Steam')({
      sessionId: 'session',
      challenge: { ...flow.reply, challengeId: 'same-stage-signal' }
    });
    const retained = flow.store.getPersistentLoginState('Steam');
    assert.equal(retained.loading, true);
    assert.equal(retained.loginDeadline, deadline);
    flow.render().actions.dismissModal();
    flow.render().actions.resumeModal();
    await flow.render().actions.submit('credential');
    assert.equal(flow.calls.filter(([name]) => name === 'submit').length, 1);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('persistent next-stage action fences an older credential failure and retired challenge', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    const old = deferred();
    const current = deferred();
    let submissions = 0;
    flow.provide = () => (++submissions === 1 ? old.promise : current.promise);
    const first = flow.render().actions.submit('password');
    await Promise.resolve();

    const next = challenge('two-factor-next', '2fa', time.now + 120000);
    flow.handlers.get('challenge:Steam')({ sessionId: 'session', challenge: next });
    let auth = flow.render();
    auth.actions.setTwoFactorCode('12345');
    auth = flow.render();
    flow.poll = async () => next;
    const second = auth.actions.handleAuthenticate();
    await Promise.resolve();
    assert.equal(flow.store.getPersistentLoginState('Steam').loading, true);

    old.reject(new Error('old failure'));
    await first;
    let state = flow.store.getPersistentLoginState('Steam');
    assert.equal(state.pendingChallenge.challengeId, next.challengeId);
    assert.equal(state.loading, true);
    assert.equal(state.error, null);

    flow.handlers.get('challenge:Steam')({ sessionId: 'session', challenge: flow.reply });
    state = flow.store.getPersistentLoginState('Steam');
    assert.equal(state.pendingChallenge.challengeId, next.challengeId);
    assert.equal(state.loading, true);

    current.resolve();
    await second;
    state = flow.store.getPersistentLoginState('Steam');
    assert.equal(state.loading, true);
    assert.equal(submissions, 2);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('persistent same-stage replacements shorten but never extend accepted expiry', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    const original = flow.store.getPersistentLoginState('Steam').loginDeadline;
    const shorter = challenge('password-shorter', 'password', time.now + 90000);
    const later = challenge('password-later', 'password', time.now + 180000);
    const pending = flow.render().actions.submit('password');
    await pending;
    flow.store.applyPersistentLoginChallenge('Steam', shorter, messages, 'session');
    const shortened = flow.store.getPersistentLoginState('Steam');
    assert.equal(shortened.loading, true);
    assert.ok(shortened.loginDeadline < original);
    flow.store.applyPersistentLoginChallenge('Steam', later, messages, 'session');
    assert.equal(
      flow.store.getPersistentLoginState('Steam').loginDeadline,
      shortened.loginDeadline
    );
  } finally {
    flow?.close();
    time.restore();
  }
});

test('anonymous platform names do not classify as credential stages', () => {
  assert.equal(getAuthStage('battlenet'), null);
  assert.equal(getAuthStage('riot'), null);
});

for (const [service, duration] of [
  ['Steam', 600000],
  ['Epic', 300000],
  ['Xbox', 600000]
]) {
  test(`${service} whole attempt survives hide, duplicate delivery and module reload`, async () => {
    const time = clock();
    const storage = new MemoryStorage();
    let flow;
    try {
      flow = await persistent(storage, service);
      const started = time.now;
      const first = await flow.start();
      assert.equal(first.loginDeadline, started + duration);
      await time.advance(90000);
      flow.render().actions.dismissModal();
      flow.render().actions.resumeModal();
      flow.store.ensurePersistentLoginTimeout(service, messages);
      flow.store.applyPersistentLoginChallenge(service, flow.reply, messages, 'session');
      flow.handlers.get(`challenge:${service}`)({ sessionId: 'session', challenge: flow.reply });
      assert.equal(flow.store.getPersistentLoginState(service).loginDeadline, started + duration);
      assert.equal(storage.getItem(`persistent-login-deadline:${service}`), null);
      flow.hide();
      assert.equal(
        JSON.parse(storage.getItem(`persistent-login-deadline:${service}`)).deadline,
        started + duration
      );
      time.timers.clear();
      flow.depart();
      flow = await persistent(storage, service);
      assert.equal(await flow.restore(), 'challenge');
      assert.equal(flow.store.getPersistentLoginState(service).loginDeadline, started + duration);
      assert.equal(storage.getItem(`persistent-login-deadline:${service}`), null);
      assert.equal([...time.timers.values()][0].at, started + duration);
      await time.advance(duration - 90000);
      assert.equal(flow.store.getPersistentLoginState(service).error, messages.timedOut);
      assert.equal(storage.getItem(`persistent-login-deadline:${service}`), null);
      assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
    } finally {
      flow?.close();
      time.restore();
    }
  });
}

for (const record of [
  'missing',
  'corrupt',
  'invalid',
  'different-operation',
  'different-session',
  'different-challenge'
]) {
  test(`persistent ${record} restoration exposes recovery without credential work`, async (t) => {
    const errors = t.mock.method(console, 'error', () => undefined);
    const time = clock();
    const storage = new MemoryStorage();
    let flow;
    try {
      const key = 'persistent-login-deadline:Steam';
      if (record === 'corrupt') storage.setItem(key, '{');
      else if (record !== 'missing')
        storage.setItem(
          key,
          JSON.stringify({
            version: 1,
            deadline: record === 'invalid' ? 'invalid' : time.now + 600000,
            sessionId: record === 'different-session' ? 'other' : 'session',
            operationId:
              record === 'different-operation'
                ? 'other'
                : record === 'different-challenge'
                  ? null
                  : 'operation',
            challengeId: record === 'different-challenge' ? 'other' : 'first'
          })
        );
      flow = await persistent(storage);
      if (record === 'different-challenge') flow.reply = { ...flow.reply, operationId: undefined };
      assert.equal(await flow.restore(), 'unavailable');
      const state = flow.store.getPersistentLoginState('Steam');
      assert.equal(state.error, messages.noResult);
      assert.equal(state.loginDeadline, null);
      assert.equal(state.sessionId, 'session');
      assert.equal(state.pendingChallenge.challengeId, 'first');
      const pollCount = flow.calls.filter(([name]) => name === 'poll').length;
      assert.equal(flow.reveal(), true);
      assert.equal(flow.calls.filter(([name]) => name === 'host-start').length, 0);
      assert.equal(flow.calls.filter(([name]) => name === 'resume').length, 1);
      assert.equal(await flow.render().actions.submit('credential'), false);
      await assert.rejects(flow.render().actions.poll(), { message: messages.noResult });
      flow.handlers.get('challenge:Steam')({
        sessionId: 'session',
        challenge: { ...flow.reply, credentialType: 'device-confirmation' }
      });
      assert.equal(flow.calls.filter(([name]) => name === 'poll').length, pollCount);
      assert.equal(flow.calls.filter(([name]) => name === 'submit').length, 0);
      assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 0);
      await flow.render().actions.cancel();
      assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
      const first = await flow.start();
      assert.equal(first.loginDeadline, time.now + 600000);
      assert.equal(flow.calls.filter(([name]) => name === 'start').length, 1);
      if (record === 'corrupt') assert.ok(errors.mock.callCount() > 0);
    } finally {
      flow?.close();
      time.restore();
    }
  });
}

test('null-operation restoration requires the exact challenge and retains the stored instant', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    storage.setItem(
      'persistent-login-deadline:Steam',
      JSON.stringify({
        version: 1,
        deadline: time.now + 120000,
        sessionId: 'session',
        operationId: null,
        challengeId: 'first'
      })
    );
    flow = await persistent(storage);
    flow.reply = { ...flow.reply, operationId: undefined };
    assert.equal(await flow.restore(), 'challenge');
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, time.now + 120000);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('first persistent reply pins a previously unknown session without changing its deadline', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    const started = time.now;
    await flow.render().actions.start();
    assert.equal(flow.store.getPersistentLoginState('Steam').sessionId, 'session');
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, started + 600000);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('a stale persistent operation cannot quarantine an admitted successor', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    await flow.start();
    const state = flow.store.getPersistentLoginState('Steam');
    const saved = storage.getItem('persistent-login-deadline:Steam');
    flow.handlers.get('challenge:Steam')({
      sessionId: 'session',
      challenge: { ...flow.reply, operationId: 'old', credentialType: 'device-confirmation' }
    });
    assert.equal(flow.store.getPersistentLoginState('Steam'), state);
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), saved);
    assert.equal(flow.calls.filter(([name]) => name === 'submit').length, 0);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('persistent redelivery without an operation preserves its pinned identity and deadline', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    await flow.start();
    const saved = flow.store.getPersistentLoginState('Steam').loginDeadline;
    assert.equal(
      flow.store.applyPersistentLoginChallenge(
        'Steam',
        { ...flow.reply, operationId: undefined },
        messages,
        'session'
      ),
      true
    );
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), null);
    flow.hide();
    assert.equal(
      JSON.parse(storage.getItem('persistent-login-deadline:Steam')).operationId,
      'operation'
    );
    time.timers.clear();
    flow.depart();
    flow = await persistent(storage);
    flow.reply = { ...flow.reply, operationId: undefined };
    assert.equal(await flow.restore(), 'challenge');
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), null);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, saved);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('persistent soft close stays dismissed across first and different challenge delivery', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    let release;
    flow.startReply = () =>
      new Promise((resolve) => {
        release = resolve;
      });
    const pending = flow.start();
    const deadline = flow.store.getPersistentLoginState('Steam').loginDeadline;
    flow.render().actions.dismissModal();
    assert.equal(flow.store.getPersistentLoginState('Steam').dismissed, true);
    release(flow.reply);
    await pending;
    assert.equal(flow.store.getPersistentLoginState('Steam').dismissed, true);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, deadline);

    const next = { ...flow.reply, challengeId: 'different' };
    flow.store.applyPersistentLoginChallenge('Steam', next, messages, 'session');
    assert.equal(flow.store.getPersistentLoginState('Steam').dismissed, true);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, deadline);

    flow.handlers.get('challenge:Steam')({
      sessionId: 'session',
      challenge: { ...next, challengeId: 'signal', credentialType: 'device-confirmation' }
    });
    assert.equal(flow.store.getPersistentLoginState('Steam').dismissed, true);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, deadline);

    assert.equal(
      flow.store.applyPersistentLoginChallenge(
        'Steam',
        { ...challenge('invalid', 'password'), expiresAt: 'invalid' },
        messages,
        'session'
      ),
      false
    );
    assert.equal(flow.store.getPersistentLoginState('Steam').dismissed, true);
    assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 0);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('a later explicit nonce resumes a pending same-session start without extending its deadline', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    let release;
    flow.startReply = () =>
      new Promise((resolve) => {
        release = resolve;
      });
    const pending = flow.start();
    const deadline = flow.store.getPersistentLoginState('Steam').loginDeadline;
    flow.render().actions.dismissModal();
    flow.store.requestPersistentLoginAttempt('Steam');

    assert.equal(flow.reveal(), true);
    assert.equal(flow.calls.filter(([name]) => name === 'start').length, 1);
    assert.equal(flow.calls.filter(([name]) => name === 'host-start').length, 0);
    assert.equal(flow.calls.filter(([name]) => name === 'resume').length, 1);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, deadline);

    release(flow.reply);
    await pending;
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, deadline);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('dismissal after a request nonce prevents the captured host effect from resuming', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    flow.store.requestPersistentLoginAttempt('Steam');
    flow.render().actions.dismissModal();

    assert.equal(flow.reveal(), false);
    assert.equal(flow.calls.filter(([name]) => name === 'host-start').length, 0);
    assert.equal(flow.calls.filter(([name]) => name === 'resume').length, 0);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('explicit cancellation resets locally before held poll and backend cancellation settle', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    let releasePoll;
    let releaseCancel;
    flow.poll = () =>
      new Promise((resolve) => {
        releasePoll = resolve;
      });
    flow.cancel = () =>
      new Promise((resolve) => {
        releaseCancel = resolve;
      });
    const polling = flow.render().actions.poll();
    const ending = flow.render().actions.cancel();

    const reset = flow.store.getPersistentLoginState('Steam');
    assert.equal(reset.pendingChallenge, null);
    assert.equal(reset.loginDeadline, null);
    assert.equal(reset.loading, false);
    flow.handlers.get('challenge:Steam')({ sessionId: 'session', challenge: flow.reply });
    assert.equal(flow.store.getPersistentLoginState('Steam').pendingChallenge, null);

    releasePoll({ ...flow.reply, challengeId: 'late-poll' });
    await polling;
    assert.equal(flow.store.getPersistentLoginState('Steam').pendingChallenge, null);
    releaseCancel(true);
    await ending;
    assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
    assert.equal(flow.store.getPersistentLoginState('Steam').pendingChallenge, null);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('persistent approval keeps the whole-attempt deadline and acknowledges each challenge once', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    flow.reply = { ...flow.reply, credentialType: 'device-confirmation' };
    const started = time.now;
    await flow.start();
    const event = { sessionId: 'session', challenge: flow.reply };
    flow.handlers.get('challenge:Steam')(event);
    flow.handlers.get('challenge:Steam')(event);
    assert.equal(flow.calls.filter(([name]) => name === 'submit').length, 1);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, started + 600000);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('duration-based restored arming extends the whole attempt after reload', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    const started = time.now;
    await flow.start();
    await time.advance(100000);
    flow.hide();
    flow.depart();
    time.timers.clear();
    flow = await persistent(storage, 'Steam', true);
    assert.equal(await flow.restore(), 'challenge');
    flow.store.ensurePersistentLoginTimeout('Steam', messages);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, started + 700000);
    assert.notEqual(flow.store.getPersistentLoginState('Steam').loginDeadline, started + 600000);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('matching past persistent record times out exactly once and never becomes unavailable', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    storage.setItem(
      'persistent-login-deadline:Steam',
      JSON.stringify({
        version: 1,
        deadline: time.now - 1,
        sessionId: 'session',
        operationId: 'operation',
        challengeId: 'first'
      })
    );
    flow = await persistent(storage);
    assert.equal(await flow.restore(), 'unavailable');
    assert.equal(flow.store.getPersistentLoginState('Steam').error, messages.timedOut);
    assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), null);
    await time.advance(1);
    assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('persistent challenge bounds only narrow the original whole attempt', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    await flow.start();
    const bound = time.now + 100000;
    assert.equal(
      flow.store.applyPersistentLoginChallenge(
        'Steam',
        { ...flow.reply, expiresAt: new Date(bound).toISOString() },
        messages,
        'session'
      ),
      true
    );
    assert.equal(
      flow.store.applyPersistentLoginChallenge(
        'Steam',
        { ...flow.reply, challengeId: 'second' },
        messages,
        'session'
      ),
      true
    );
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, bound);
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), null);
    flow.hide();
    assert.equal(JSON.parse(storage.getItem('persistent-login-deadline:Steam')).deadline, bound);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('persistent success after reload clears matching storage and stale reconciliation cannot replace a retry', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    await flow.start();
    flow.hide();
    flow.depart();
    time.timers.clear();
    flow = await persistent(storage);
    flow.poll = async () => ({ authenticated: true, sessionId: 'session' });
    assert.equal(await flow.restore(), 'authenticated');
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), null);
    flow.store.resetPersistentLoginState('Steam');
    let resolve;
    flow.poll = () =>
      new Promise((done) => {
        resolve = done;
      });
    const restoring = flow.restore();
    flow.store.resetPersistentLoginState('Steam');
    await flow.start();
    const saved = storage.getItem('persistent-login-deadline:Steam');
    resolve({ ...flow.reply, operationId: 'old' });
    assert.equal(await restoring, 'none');
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), saved);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('slow persistent cancellation cannot reset or delete a successor', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    await flow.start();
    let release;
    flow.cancel = () =>
      new Promise((done) => {
        release = done;
      });
    const ending = flow.render().actions.cancel();
    flow.reply = { ...flow.reply, operationId: 'retry', challengeId: 'retry' };
    await flow.start();
    flow.hide();
    const saved = storage.getItem('persistent-login-deadline:Steam');
    assert.equal(JSON.parse(saved).operationId, 'retry');
    release();
    await ending;
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), saved);
    assert.equal(flow.store.getPersistentLoginState('Steam').pendingChallenge.challengeId, 'retry');
  } finally {
    flow?.close();
    time.restore();
  }
});

// X, Escape and Cancel on a container login prompt run cancel() and then close the prompt in the
// same tick, the way the service dialog's dismiss handler writes `dismissed`.
const cancelThenDismiss = async (flow) => {
  let reject;
  flow.cancel = () =>
    new Promise((_done, fail) => {
      reject = fail;
    });
  const ending = flow.render().actions.cancel();
  flow.store.updatePersistentLoginState('Steam', (current) => ({ ...current, dismissed: true }));
  return { ending, reject: () => reject(new Error('cancel request failed')) };
};

test('a failed server cancel after the prompt closes leaves the not-canceled error', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    const { ending, reject } = await cancelThenDismiss(flow);
    reject();
    await ending;
    assert.equal(
      flow.store.getPersistentLoginState('Steam').error,
      'prefill.persistent.loginNotCanceled'
    );
    // The red text shows, but the account does not read "Login failed".
    assert.equal(flow.store.getPersistentLoginState('Steam').endReason, 'cancelFailed');
    assert.equal(
      flow.store.getPersistentLoginFailure(flow.store.getPersistentLoginState('Steam')),
      null
    );
  } finally {
    flow?.close();
    time.restore();
  }
});

test('a cancel names the login attempt of the challenge it ends', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    flow.reply = { ...flow.reply, loginAttempt: 7 };
    await flow.start();
    flow.cancel = async () => undefined;
    await flow.render().actions.cancel();
    const startCall = flow.calls.find(([name]) => name === 'start');
    const loginId = startCall.at(-1);
    assert.match(loginId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
    assert.deepEqual(
      flow.calls.filter(([name]) => name === 'cancel'),
      [['cancel', 'Steam', 'session', { loginId, loginAttempt: 7 }]]
    );
  } finally {
    flow?.close();
    time.restore();
  }
});

test('closing before the first response cancels the exact login on every platform', async () => {
  for (const service of ['Steam', 'Epic', 'Xbox']) {
    const time = clock();
    let flow;
    try {
      flow = await persistent(new MemoryStorage(), service);
      let resolveStart;
      flow.startReply = () =>
        new Promise((resolve) => {
          resolveStart = resolve;
        });
      flow.store.setPersistentLoginStartSessionId(service, 'session');
      const starting = flow.render().actions.start();
      const startCall = flow.calls.find(([name]) => name === 'start');
      const loginId = startCall.at(-1);
      await flow.render().actions.cancel();
      assert.deepEqual(
        flow.calls.filter(([name]) => name === 'cancel'),
        [['cancel', service, 'session', { loginId, loginAttempt: null }]]
      );
      assert.match(
        loginId,
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
      );
      resolveStart({ authenticated: true, sessionId: 'session' });
      await starting;
      assert.equal(flow.store.getPersistentLoginState(service).loginId, null);
    } finally {
      flow?.close();
      time.restore();
    }
  }
});

for (const outcome of ['challenge', 'authenticated', 'failure']) {
  test(`a stale ${outcome} start cleans up its original session without changing a successor`, async () => {
    const time = clock();
    let flow;
    try {
      flow = await persistent(new MemoryStorage());
      const old = deferred();
      flow.startReply = () => old.promise;
      flow.store.setPersistentLoginStartSessionId(
        'Steam',
        'session-A',
        undefined,
        undefined,
        outcome !== 'challenge'
      );
      const starting = flow.render().actions.start();
      const oldId = flow.calls.find(([name]) => name === 'start').at(-1);

      flow.store.resetPersistentLoginState('Steam');
      flow.reply = {
        ...flow.reply,
        sessionId: 'session-B',
        challengeId: 'new',
        operationId: 'new'
      };
      flow.startReply = async () => flow.reply;
      flow.store.setPersistentLoginStartSessionId('Steam', 'session-B');
      await flow.render().actions.start();
      const successor = flow.store.getPersistentLoginState('Steam');

      if (outcome === 'failure') old.reject(new Error('old request failed'));
      else if (outcome === 'authenticated')
        old.resolve({ authenticated: true, sessionId: 'session-A' });
      else old.resolve({ ...challenge('old', 'password'), sessionId: 'session-A' });
      await starting;

      assert.equal(flow.store.getPersistentLoginState('Steam'), successor);
      assert.deepEqual(
        flow.calls.filter(([name]) => name === 'cancel'),
        [
          [
            'cancel',
            'Steam',
            'session-A',
            { loginId: oldId, loginAttempt: outcome === 'challenge' ? 7 : null }
          ]
        ]
      );
    } finally {
      flow?.close();
      time.restore();
    }
  });
}

test('an adopted challenge cancels by numeric attempt and an unidentified session sends no request', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    flow.store.updatePersistentLoginState('Steam', (current) => ({
      ...current,
      sessionId: 'session',
      pendingChallenge: { ...flow.reply, loginAttempt: 9 }
    }));
    assert.equal(await flow.store.endPersistentLogin('Steam'), true);
    assert.deepEqual(
      flow.calls.filter(([name]) => name === 'cancel'),
      [['cancel', 'Steam', 'session', { loginAttempt: 9 }]]
    );

    flow.store.updatePersistentLoginState('Steam', (current) => ({
      ...current,
      sessionId: 'session',
      loading: true
    }));
    assert.equal(await flow.store.endPersistentLogin('Steam'), false);
    assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('a failed server cancel leaves a newer login attempt without an error', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    const { ending, reject } = await cancelThenDismiss(flow);
    flow.cancel = async () => true;
    flow.reply = { ...flow.reply, operationId: 'retry', challengeId: 'retry' };
    await flow.start();
    reject();
    await ending;
    const current = flow.store.getPersistentLoginState('Steam');
    assert.equal(current.pendingChallenge.challengeId, 'retry');
    assert.equal(current.error, null);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('login stays blocked until the server cancel answers, whether it succeeds or fails', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    const { ending, reject } = await cancelThenDismiss(flow);
    // The store is at rest at once, but a login started now would race the server's cancel.
    assert.equal(flow.store.getPersistentLoginState('Steam').loading, false);
    assert.equal(flow.store.usePersistentLoginCanceling('Steam'), true);
    reject();
    await ending;
    assert.equal(flow.store.usePersistentLoginCanceling('Steam'), false);

    await flow.start();
    let release;
    flow.cancel = () =>
      new Promise((done) => {
        release = done;
      });
    const succeeding = flow.render().actions.cancel();
    assert.equal(flow.store.usePersistentLoginCanceling('Steam'), true);
    release();
    await succeeding;
    assert.equal(flow.store.usePersistentLoginCanceling('Steam'), false);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('a canceled login with a deadline leaves no timer to send a second cancel', async () => {
  const time = clock();
  let flow;
  try {
    flow = await persistent(new MemoryStorage());
    await flow.start();
    const deadline = flow.store.getPersistentLoginState('Steam').loginDeadline;
    assert.ok(deadline > time.now);
    await flow.render().actions.cancel();
    await time.advance(deadline - time.now + 1000);
    assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('initially blocked storage refuses a persistent lifetime and login request', async (t) => {
  const warnings = t.mock.method(console, 'warn', () => undefined);
  const time = clock();
  let flow;
  const storage = {
    getItem() {
      throw new Error('blocked');
    },
    setItem() {
      throw new Error('blocked');
    },
    removeItem() {
      throw new Error('blocked');
    }
  };
  try {
    flow = await persistent(storage);
    await flow.start();
    await time.advance(100000);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, null);
    assert.equal(flow.store.getPersistentLoginState('Steam').error, messages.noResult);
    assert.equal(flow.calls.filter(([name]) => name === 'start').length, 0);
    flow.depart();
    time.timers.clear();
    flow = await persistent(storage);
    assert.equal(await flow.restore(), 'unavailable');
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, null);
    assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 0);
    assert.ok(warnings.mock.callCount() > 0);
  } finally {
    flow?.close();
    time.restore();
  }
});

for (const mode of ['recovered', 'blocked', 'continuous'])
  test(`${mode} snapshot cannot silently lose an accepted lower bound`, async (t) => {
    t.mock.method(console, 'error', () => undefined);
    t.mock.method(console, 'warn', () => undefined);
    const time = clock();
    const backing = new MemoryStorage();
    let blocked = false;
    let flow;
    const storage = {
      getItem: (key) => backing.getItem(key),
      removeItem(key) {
        if (blocked && mode === 'blocked') throw new Error('blocked');
        backing.removeItem(key);
      },
      setItem(key, value) {
        if (blocked) throw new Error('blocked');
        backing.setItem(key, value);
      }
    };
    try {
      flow = await persistent(storage, 'Steam', mode === 'continuous' ? mode : false);
      await flow.start();
      const original = time.now + 600000;
      flow.hide();
      assert.equal(
        JSON.parse(backing.getItem('persistent-login-deadline:Steam')).deadline,
        original
      );
      flow.depart();
      time.timers.clear();
      flow = await persistent(storage, 'Steam', mode === 'continuous' ? mode : false);
      assert.equal(await flow.restore(), 'challenge');
      if (mode !== 'continuous')
        assert.equal(backing.getItem('persistent-login-deadline:Steam'), null);
      const narrowed = time.now + 100000;
      blocked = true;
      flow.store.applyPersistentLoginChallenge(
        'Steam',
        { ...flow.reply, expiresAt: new Date(narrowed).toISOString() },
        messages,
        'session'
      );
      assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, narrowed);
      if (mode !== 'blocked') blocked = false;
      flow.hide();
      flow.depart();
      time.timers.clear();
      flow = await persistent(storage);
      flow.reply = { ...flow.reply, challengeId: 'second' };
      if (mode === 'blocked') {
        assert.equal(await flow.restore(), 'unavailable');
        assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, null);
        assert.equal(backing.getItem('persistent-login-deadline:Steam'), null);
        assert.equal(await flow.render().actions.submit('credential'), false);
        await assert.rejects(flow.render().actions.poll());
        flow.handlers.get('challenge:Steam')({
          sessionId: 'session',
          challenge: { ...flow.reply, credentialType: 'device-confirmation' }
        });
        assert.deepEqual(
          flow.calls.map(([name]) => name),
          ['poll']
        );
        blocked = false;
        flow.depart();
        flow = await persistent(storage);
        assert.equal(await flow.restore(), 'unavailable');
        assert.equal(backing.getItem('persistent-login-deadline:Steam'), null);
      } else {
        assert.equal(await flow.restore(), 'challenge');
        assert.equal(
          flow.store.getPersistentLoginState('Steam').loginDeadline,
          mode === 'continuous' ? original : narrowed
        );
        if (mode === 'continuous')
          assert.notEqual(flow.store.getPersistentLoginState('Steam').loginDeadline, narrowed);
      }
    } finally {
      flow?.close();
      time.restore();
    }
  });

test('failed removal refuses restore and fresh start until the original snapshot can be consumed', async (t) => {
  t.mock.method(console, 'error', () => undefined);
  const time = clock();
  const backing = new MemoryStorage();
  const key = 'persistent-login-deadline:Steam';
  const saved = JSON.stringify({
    version: 1,
    deadline: time.now + 400000,
    sessionId: 'session',
    operationId: 'operation',
    challengeId: 'first'
  });
  backing.setItem(key, saved);
  let blocked = true;
  const storage = {
    getItem: (key) => backing.getItem(key),
    setItem: (key, value) => backing.setItem(key, value),
    removeItem(key) {
      if (blocked && key !== '__storage_test__') throw new Error('blocked');
      backing.removeItem(key);
    }
  };
  let flow;
  try {
    flow = await persistent(storage);
    flow.reply = { ...flow.reply, expiresAt: new Date(time.now + 100000).toISOString() };
    assert.equal(await flow.restore(), 'unavailable');
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, null);
    assert.equal(backing.getItem(key), saved);
    flow.store.resetPersistentLoginState('Steam');
    await flow.start();
    assert.equal(flow.store.getPersistentLoginState('Steam').error, messages.noResult);
    assert.equal(flow.calls.filter(([name]) => name === 'start').length, 0);
    assert.equal(backing.getItem(key), saved);
    blocked = false;
    flow.reply = { ...flow.reply, expiresAt: new Date(time.now + 86400000).toISOString() };
    assert.equal(await flow.restore(), 'challenge');
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, time.now + 400000);
    assert.equal(backing.getItem(key), null);
  } finally {
    flow?.close();
    time.restore();
  }
});

for (const mode of ['resume', 'elapsed', 'remove-failed'])
  test(`back-forward cache ${mode} consumes before any progress`, async (t) => {
    t.mock.method(console, 'error', () => undefined);
    const time = clock();
    const backing = new MemoryStorage();
    const key = 'persistent-login-deadline:Steam';
    let blocked = false;
    const storage = {
      getItem: (key) => backing.getItem(key),
      setItem: (key, value) => backing.setItem(key, value),
      removeItem(key) {
        if (blocked) throw new Error('blocked');
        backing.removeItem(key);
      }
    };
    let flow;
    try {
      flow = await persistent(storage);
      await flow.start();
      const original = flow.store.getPersistentLoginState('Steam').loginDeadline;
      let release;
      flow.poll = () =>
        new Promise((resolve) => {
          release = resolve;
        });
      const pending = flow.render().actions.poll();
      flow.hide();
      const saved = backing.getItem(key);
      assert.equal(JSON.parse(saved).deadline, original);
      assert.equal(time.timers.size, 0);
      flow.handlers.get('challenge:Steam')({
        sessionId: 'session',
        challenge: {
          ...flow.reply,
          credentialType: 'device-confirmation',
          expiresAt: new Date(time.now + 100000).toISOString()
        }
      });
      release({ authenticated: true, sessionId: 'session' });
      await assert.rejects(pending);
      assert.equal(await flow.render().actions.submit('credential'), false);
      assert.equal(await flow.render().actions.start(), null);
      await assert.rejects(flow.render().actions.poll());
      assert.equal(await flow.restore(), 'none');
      assert.deepEqual(
        flow.calls.map(([name]) => name),
        ['start', 'poll']
      );
      assert.equal(backing.getItem(key), saved);
      assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, original);
      if (mode === 'elapsed') await time.advance(600001);
      if (mode === 'remove-failed') blocked = true;
      flow.show();
      if (mode === 'resume') {
        assert.equal(backing.getItem(key), null);
        assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, original);
        const narrowed = time.now + 100000;
        assert.equal(
          flow.store.applyPersistentLoginChallenge(
            'Steam',
            { ...flow.reply, expiresAt: new Date(narrowed).toISOString() },
            messages,
            'session'
          ),
          true
        );
        flow.hide();
        assert.equal(JSON.parse(backing.getItem(key)).deadline, narrowed);
        flow.show();
        assert.equal(backing.getItem(key), null);
      } else if (mode === 'elapsed') {
        assert.equal(flow.store.getPersistentLoginState('Steam').error, messages.timedOut);
        flow.show();
        assert.equal(flow.calls.filter(([name]) => name === 'cancel').length, 1);
        assert.equal(backing.getItem(key), null);
      } else {
        assert.equal(flow.store.getPersistentLoginState('Steam').error, messages.noResult);
        assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, null);
        assert.equal(await flow.render().actions.submit('credential'), false);
        await assert.rejects(flow.render().actions.poll());
        assert.equal(backing.getItem(key), saved);
        assert.deepEqual(
          flow.calls.map(([name]) => name),
          ['start', 'poll']
        );
      }
    } finally {
      blocked = false;
      flow?.close();
      time.restore();
    }
  });

for (const mode of ['abrupt', 'legacy'])
  test(`${mode} document cannot reconstruct a reload lifetime`, async () => {
    const time = clock();
    const storage = new MemoryStorage();
    const key = 'persistent-login-deadline:Steam';
    let flow;
    try {
      flow = await persistent(storage);
      await flow.start();
      flow.render().actions.dismissModal();
      flow.depart();
      assert.equal(storage.getItem(key), null);
      time.timers.clear();
      if (mode === 'legacy')
        storage.setItem(
          key,
          JSON.stringify({
            deadline: time.now + 600000,
            sessionId: 'session',
            operationId: 'operation',
            challengeId: 'first'
          })
        );
      flow = await persistent(storage);
      assert.equal(await flow.restore(), 'unavailable');
      assert.equal(flow.store.getPersistentLoginState('Steam').error, messages.noResult);
      assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, null);
      assert.deepEqual(
        flow.calls.map(([name]) => name),
        ['poll']
      );
    } finally {
      flow?.close();
      time.restore();
    }
  });

test('suspended start and authentication pushes cannot replace or remove a saved clock', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  const key = 'persistent-login-deadline:Steam';
  let flow;
  try {
    flow = await persistent(storage);
    let release;
    flow.startReply = () =>
      new Promise((resolve) => {
        release = resolve;
      });
    const pending = flow.start();
    const deadline = flow.store.getPersistentLoginState('Steam').loginDeadline;
    flow.hide();
    const saved = storage.getItem(key);
    release({ ...flow.reply, expiresAt: new Date(time.now + 100000).toISOString() });
    await pending;
    flow.handlers.get('auth:Steam')({ sessionId: 'session', authState: 'Authenticated' });
    assert.equal(storage.getItem(key), saved);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, deadline);
    assert.equal(flow.store.getPersistentLoginState('Steam').authenticated, false);
    assert.deepEqual(
      flow.calls.map(([name]) => name),
      ['start']
    );
    flow.show();
    assert.equal(storage.getItem(key), null);
    assert.equal(flow.store.getPersistentLoginState('Steam').loginDeadline, deadline);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('terminal cleanup removes only the matching suspended service snapshot', async () => {
  const time = clock();
  const storage = new MemoryStorage();
  let flow;
  try {
    flow = await persistent(storage);
    await flow.start();
    flow.store.setPersistentLoginStartSessionId('Epic', 'epic-session');
    assert.equal(flow.store.armPersistentLoginTimeout('Epic', time.now + 300000, messages), true);
    flow.hide();
    const epic = storage.getItem('persistent-login-deadline:Epic');
    assert.equal(JSON.parse(epic).deadline, time.now + 300000);
    flow.store.resetPersistentLoginState('Steam');
    assert.equal(storage.getItem('persistent-login-deadline:Steam'), null);
    assert.equal(storage.getItem('persistent-login-deadline:Epic'), epic);
    flow.show();
    flow.store.resetPersistentLoginState('Epic');
    assert.equal(storage.getItem('persistent-login-deadline:Epic'), null);
  } finally {
    flow?.close();
    time.restore();
  }
});

test('storage receipts report backing mutations while preserving fallback contents', async (t) => {
  const errors = t.mock.method(console, 'error', () => undefined);
  const warnings = t.mock.method(console, 'warn', () => undefined);
  const backing = new MemoryStorage();
  let writes = true,
    reads = true,
    deletes = true;
  const storage = {
    getItem(key) {
      if (!reads) throw new Error('blocked');
      return backing.getItem(key);
    },
    setItem(key, value) {
      if (!writes) throw new Error('blocked');
      backing.setItem(key, value);
    },
    removeItem(key) {
      if (!deletes) throw new Error('blocked');
      backing.removeItem(key);
    }
  };
  let flow;
  try {
    flow = await persistent(storage);
    const safe = flow.storage;
    assert.equal(safe.setItem('key', 'original'), true);
    assert.equal(safe.setJSON('json', { a: 1 }), true);
    assert.equal(safe.removeItem('json'), true);
    assert.equal(safe.removeItem('absent'), true);
    writes = false;
    assert.equal(safe.setItem('key', 'memory'), false);
    assert.equal(safe.getItem('key'), 'original');
    reads = false;
    assert.equal(safe.getItem('key'), 'memory');
    deletes = false;
    assert.equal(safe.removeItem('key'), false);
    assert.equal(safe.getItem('key'), null);
    assert.equal(safe.setJSON('bad', 1n), false);
    assert.equal(safe.isAvailable(), true);
    flow.depart();
    flow = await persistent(storage);
    assert.equal(flow.storage.isAvailable(), false);
    assert.equal(flow.storage.setJSON('key', { a: 2 }), false);
    assert.deepEqual(flow.storage.getJSON('key'), { a: 2 });
    assert.equal(flow.storage.removeItem('key'), false);
    assert.equal(flow.storage.getItem('key'), null);
    assert.ok(errors.mock.callCount() > 0);
    assert.ok(warnings.mock.callCount() > 0);
  } finally {
    flow?.close();
  }
});
