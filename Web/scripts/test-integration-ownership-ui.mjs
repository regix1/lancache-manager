import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync, writeFileSync, mkdirSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import ts from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  liftHookCallback,
  moduleUrl,
  parseSource,
  collectNodes,
  transpile
} from './transpile-module.mjs';

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
export const useEffect = (run,deps) => {
  const i=cursor++; const slot=slots[i] ??= {};
  if(!slot.deps || deps.some((d,j)=>!Object.is(d,slot.deps[j]))) {
    slot.deps=deps; effects.push(()=>{slot.cleanup?.();slot.cleanup=run();});
  }
};
export const createComponent = () => {
  const saved=[];
  return { render(body) { slots=saved;cursor=0;effects=[];const result=body();const work=effects;work.forEach(run=>run());return result; },
    unmount(){saved.forEach(slot=>slot.cleanup?.());} };
};
export default {useState,useRef,useEffect};
`);
const { createComponent } = await import(reactUrl);
const reasonUrl = await compileToUrl('../src/types.ts');
const { getIntegrationReasonKey, integrationReasonKeys, isIntegrationReason } = await import(
  reasonUrl
);
const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});
const { ApiError, buildApiError } = await import(apiErrorUrl);
const apiSource = parseSource('src/services/api.service.ts');
const apiMethods = collectNodes(
  apiSource,
  (node) =>
    ts.isMethodDeclaration(node) &&
    [
      'assertIntegrationAccess',
      'getEpicMappingAuthStatus',
      'getXboxMappingAuthStatus',
      'getPersistentIntegrationLoginAvailability',
      'handleResponse'
    ].includes(node.name.getText(apiSource))
);
assert.equal(apiMethods.length, 5);
const ApiService = bindLifted(
  `() => {
  class ApiService {
    static getFetchOptions() { return {}; }
    ${apiMethods.map((node) => node.getText(apiSource)).join('\n')}
  }
  return ApiService;
}`,
  { ApiError, buildApiError, isIntegrationReason, i18n: { t: (key) => key }, API_BASE: '/api' }
)();
const aliases = {
  react: reactUrl,
  'react-i18next': moduleUrl('const t=key=>key; export const useTranslation=()=>({t});'),
  '@contexts/useAuth': moduleUrl('export const useAuth=()=>globalThis.integrationTest.auth;'),
  '@contexts/SignalRContext/useSignalR': moduleUrl(
    'const hub={on(){},off(){},isConnected:true};export const useSignalR=()=>hub;'
  ),
  '@contexts/notifications': moduleUrl(
    'const n={addNotification:()=>"card",updateNotification(){},removeNotification(){},scheduleAutoDismiss(){}};export const useNotifications=()=>n;export const NOTIFICATION_IDS={};'
  ),
  '@services/api.service': moduleUrl(
    'export default new Proxy({}, {get:(_target,key)=>(...args)=>globalThis.integrationTest.api[key](...args)});'
  ),
  '@services/apiError': apiErrorUrl,
  '@utils/uuid': moduleUrl(
    'export const createUuid=()=>`attempt-${++globalThis.integrationTest.sequence}`;'
  ),
  '@utils/error': moduleUrl('export const getErrorMessage=error=>error.message;'),
  '../types': reasonUrl,
  './useErrorHandler': moduleUrl(
    'const handler={notifyError(){}};export const useErrorHandler=()=>handler;'
  ),
  './useReconnectRefetch': moduleUrl('export const useReconnectRefetch=()=>{};'),
  './loginAttemptTimeout': moduleUrl('export const STEAM_DEVICE_CONFIRMATION_TIMEOUT_MS=60000;'),
  '@contexts/useSteamWebApiStatus': moduleUrl(
    'const refresh=async()=>{};export const useSteamWebApiStatus=()=>({status:globalThis.integrationTest.keyStatus,error:globalThis.integrationTest.keyError,refresh});'
  )
};
const { useEpicMappingAuth } = await import(
  await compileToUrl('../src/hooks/useEpicMappingAuth.ts', aliases)
);
const { useXboxMappingAuth } = await import(
  await compileToUrl('../src/hooks/useXboxMappingAuth.ts', aliases)
);
const { useSteamLoginFlow } = await import(
  await compileToUrl('../src/hooks/useSteamLoginFlow.ts', aliases)
);
const { useSteamApiKey } = await import(
  await compileToUrl('../src/hooks/useSteamApiKey.ts', aliases)
);
const { useSteamWebApiStatusState } = await import(
  await compileToUrl('../src/hooks/useSteamWebApiStatus.ts', {
    ...aliases,
    '@hooks/useReconnectRefetch': aliases['./useReconnectRefetch']
  })
);
const settle = () => new Promise((resolve) => setImmediate(resolve));
const deferred = () => {
  let resolve;
  const promise = new Promise((done) => {
    resolve = done;
  });
  return { promise, resolve };
};
const setup = (changes = {}) => {
  const state = {
    auth: {
      authenticationEnabled: true,
      authMode: 'authenticated',
      accountId: 'a',
      sessionId: 'session-a',
      isLoading: false
    },
    status: {
      canManage: true,
      canSignIn: true,
      canCancel: false,
      canLogout: false,
      isAuthenticated: false,
      attemptId: null
    },
    keyStatus: { canManage: true },
    keyError: null,
    sequence: 0,
    calls: [],
    api: {},
    ...changes
  };
  for (const platform of ['Epic', 'Xbox']) {
    state.api[`get${platform}MappingAuthStatus`] = async () => state.status;
    state.api[`start${platform}MappingLogin`] = async (signal, request) => {
      state.calls.push(['start', platform, request, signal]);
      state.status = {
        ...state.status,
        canSignIn: false,
        canCancel: true,
        attemptId: request.attemptId,
        loginInProgress: true
      };
      return {
        attemptId: request.attemptId,
        expiresAtUtc: '2030-01-01T00:00:00Z',
        authorizationUrl: 'https://example.test/auth',
        userCode: 'ABC-123',
        verificationUri: 'https://example.test/auth'
      };
    };
    state.api[`cancel${platform}MappingLogin`] = async (id) => {
      state.calls.push(['cancel', platform, id]);
    };
  }
  state.api.completeEpicMappingAuth = async (code, signal, id) => {
    state.calls.push(['complete', code, id, signal]);
  };
  state.api.getJsonFetchOptions = (body, options) => ({ ...options, body: JSON.stringify(body) });
  state.api.getFetchOptions = () => ({});
  state.api.handleResponse = ApiService.handleResponse;
  state.api.assertIntegrationAccess = ApiService.assertIntegrationAccess;
  state.api.cancelSteamLogin = async (id) => {
    state.calls.push(['cancel', 'Steam', id]);
  };
  state.api.testSteamApiKey = async () => ({ valid: true });
  state.api.saveSteamApiKey = async (key) => {
    state.calls.push(['save', key]);
  };
  globalThis.integrationTest = state;
  return state;
};
const mount = async (hook) => {
  const component = createComponent();
  let value;
  const render = () => (value = component.render(hook));
  render();
  await settle();
  render();
  return { render, read: () => value, unmount: () => component.unmount() };
};

const make = (path, name, bindings) => {
  const source = parseSource(path, ts.ScriptKind.TSX);
  const body = source.statements
    .filter((node) => !ts.isImportDeclaration(node) && !ts.isExportAssignment(node))
    .map((node) => node.getText(source))
    .join('\n')
    .replace(/\bexport\s+/g, '');
  const code = transpile(body, ts.ModuleKind.CommonJS, { jsx: ts.JsxEmit.React });
  const names = Object.keys(bindings).filter((key) => key !== name);
  return new Function(...names, `${code}\nreturn ${name};`)(...names.map((key) => bindings[key]));
};

const noop = () => undefined;
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);
const renderBindings = {
  React,
  useEffect: noop,
  useRef: React.useRef,
  useState: React.useState,
  useTranslation: () => ({ t: (key) => key }),
  getIntegrationReasonKey,
  Button: ({ children, disabled, onClick }) =>
    React.createElement('button', { disabled, onClick }, children),
  Modal: ({ opened, children }) =>
    opened ? React.createElement('div', { role: 'dialog' }, children) : null,
  CustomScrollbar: passthrough,
  FormField: () => null,
  LoginSteps: () => null,
  LoginAttemptStatus: () => null,
  Alert: passthrough,
  StepHeader: () => null,
  LoadingSpinner: () => null,
  Key: () => null,
  KeyRound: () => null,
  Shield: () => null,
  CheckCircle: () => null,
  ExternalLink: () => null,
  SteamIcon: () => null,
  EpicIcon: () => null,
  XboxIcon: () => null,
  noAutofill: {},
  useSignalR: () => ({ on: noop, off: noop }),
  useCopyFeedback: () => [false, noop],
  copyText: noop,
  cancelAuthModalLogin: noop
};

test('status ingress distinguishes login permissions from management and rejects malformed reasons', async () => {
  const allowed = {
    canManage: true,
    canSignIn: true,
    canLogout: false,
    canCancel: false,
    canRecover: false
  };
  for (const reason of [undefined, null, ...Object.keys(integrationReasonKeys)]) {
    ApiService.assertIntegrationAccess({ ...allowed, ownershipReason: reason }, 'login', 200);
    ApiService.assertIntegrationAccess(
      { canManage: true, ownershipReason: reason },
      'management',
      200
    );
  }
  for (const value of [
    null,
    [],
    'response',
    {},
    { ...allowed, canManage: 'true' },
    { ...allowed, canRecover: null },
    { ...allowed, canSignIn: undefined },
    { ...allowed, canSignIn: false },
    { ...allowed, canManage: false },
    ...['', ' ', 'unknown', 'status-unavailable', 'toString', '__proto__'].map(
      (ownershipReason) => ({ ...allowed, ownershipReason })
    )
  ]) {
    assert.throws(
      () => ApiService.assertIntegrationAccess(value, 'login', 200),
      (error) => {
        assert.equal(error.kind, 'parse');
        assert.equal(error.status, 200);
        assert.equal(error.body.stageKey, 'errors.integration.statusUnavailable');
        assert.equal(error.cause, value);
        return true;
      }
    );
  }
  for (const reason of Object.keys(integrationReasonKeys)) {
    ApiService.assertIntegrationAccess(
      { ...allowed, canSignIn: false, ownershipReason: reason },
      'login',
      200
    );
  }
  assert.throws(() => getIntegrationReasonKey(null), /recognized reason/);
  assert.throws(() => getIntegrationReasonKey('status-unavailable'), /recognized reason/);
  const originalFetch = globalThis.fetch;
  try {
    for (const platform of ['Epic', 'Xbox']) {
      globalThis.fetch = async () => new Response(JSON.stringify({ ...allowed, canSignIn: false }));
      await assert.rejects(ApiService[`get${platform}MappingAuthStatus`](), { kind: 'parse' });
      globalThis.fetch = async () => new Response(JSON.stringify(allowed));
      assert.deepEqual(await ApiService[`get${platform}MappingAuthStatus`](), allowed);
    }
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('saved-login ingress accepts optional account and reason only on available responses', async () => {
  const originalFetch = globalThis.fetch;
  try {
    for (const value of [
      { available: true },
      { available: true, account: null, reason: null },
      { available: true, account: 'saved' },
      { available: false, reason: 'no-saved-login' }
    ]) {
      globalThis.fetch = async () => new Response(JSON.stringify(value));
      assert.deepEqual(await ApiService.getPersistentIntegrationLoginAvailability('Steam'), value);
    }
    for (const value of [
      null,
      [],
      {},
      { available: 'true' },
      { available: true, account: 1 },
      { available: true, reason: 'unknown' },
      { available: false },
      ...[null, '', ' ', 'unknown', '__proto__'].map((reason) => ({ available: false, reason }))
    ]) {
      globalThis.fetch = async () => new Response(JSON.stringify(value));
      await assert.rejects(ApiService.getPersistentIntegrationLoginAvailability('Steam'), {
        kind: 'parse'
      });
    }
  } finally {
    globalThis.fetch = originalFetch;
  }
});

for (const [platform, hook] of [
  ['Epic', useEpicMappingAuth],
  ['Xbox', useXboxMappingAuth]
]) {
  test(`${platform} initial, malformed, loading and changed-identity states render unavailable`, async () => {
    for (const shared of [false, true]) {
      const f = setup();
      if (shared)
        f.auth = {
          authenticationEnabled: false,
          authMode: 'none',
          accountId: null,
          sessionId: null,
          isLoading: false
        };
      const originalFetch = globalThis.fetch;
      const originalWarn = console.warn;
      console.warn = noop;
      f.api[`get${platform}MappingAuthStatus`] = () =>
        ApiService[`get${platform}MappingAuthStatus`]();
      globalThis.fetch = async () => new Response(JSON.stringify({ canManage: false }));
      const component = createComponent();
      const Modal = make(
        `src/components/modals/auth/${platform}AuthModal.tsx`,
        `${platform}AuthModal`,
        renderBindings
      );
      const show = (value) => {
        assert.equal(value.state.canAuthenticate, false);
        assert.equal(value.state.accessUnavailable, true);
        const markup = renderToStaticMarkup(
          React.createElement(Modal, { opened: true, onClose: noop, ...value })
        );
        assert.match(markup, /errors.integration.statusUnavailable/);
        const Step = make(
          `src/components/initialization/steps/${platform}AuthStep.tsx`,
          `${platform}AuthStep`,
          {
            ...renderBindings,
            [`use${platform}MappingAuth`]: () => value
          }
        );
        assert.match(
          renderToStaticMarkup(React.createElement(Step, { onComplete: noop, onSkip: noop })),
          /errors.integration.statusUnavailable/
        );
      };
      try {
        show(component.render(() => hook()));
        await settle();
        show(component.render(() => hook()));
        globalThis.fetch = async () =>
          new Response(
            JSON.stringify({
              canManage: true,
              canSignIn: true,
              canCancel: false,
              canLogout: false,
              canRecover: false
            })
          );
        await component.render(() => hook()).refreshStatus();
        assert.equal(component.render(() => hook()).state.canAuthenticate, true);
        f.auth = { ...f.auth, isLoading: true };
        show(component.render(() => hook()));
        f.auth = { ...f.auth, isLoading: false, sessionId: 'changed' };
        const changed = component.render(() => hook());
        show(changed);
        await changed.startLogin();
        assert.equal(f.calls.length, 0);
      } finally {
        component.unmount();
        globalThis.fetch = originalFetch;
        console.warn = originalWarn;
      }
    }
  });
}

test('Steam integration loading and changed identity render without a refusal reason', async () => {
  setup();
  let integration = { identity: 'a', access: null, refresh: async () => undefined };
  const view = await mount(() => useSteamLoginFlow({ loginUrl: '/login', integration }));
  const Modal = make(
    'src/components/modals/auth/SteamAuthModal.tsx',
    'SteamAuthModal',
    renderBindings
  );
  const show = () => {
    const value = view.render();
    assert.equal(value.state.accessUnavailable, true);
    assert.match(
      renderToStaticMarkup(React.createElement(Modal, { opened: true, onClose: noop, ...value })),
      /errors.integration.statusUnavailable/
    );
  };
  try {
    show();
    integration = {
      ...integration,
      access: { canManage: true, canSignIn: true, ownershipReason: null }
    };
    assert.equal(view.render().state.canAuthenticate, true);
    integration = { ...integration, identity: 'b' };
    show();
  } finally {
    view.unmount();
  }
});

for (const [platform, hook] of [
  ['Epic', useEpicMappingAuth],
  ['Xbox', useXboxMappingAuth]
]) {
  test(`${platform} refuses missing flags and another owner's sign-in without dispatch`, async () => {
    const f = setup();
    f.status = { isAuthenticated: true, ownershipReason: 'owned-by-another-account' };
    const view = await mount(() => hook());
    assert.equal(view.read().state.canAuthenticate, false);
    await view.read().startLogin();
    assert.deepEqual(f.calls, []);
    assert.equal(view.render().state.ownershipReason, 'owned-by-another-account');
    view.unmount();
  });
  test(`${platform} rejects a prior account response and old caller handlers`, async () => {
    const f = setup();
    const answer = deferred();
    f.api[`get${platform}MappingAuthStatus`] = () => answer.promise;
    const component = createComponent();
    const old = component.render(() => hook());
    f.auth = { ...f.auth, accountId: 'b', sessionId: 'session-b' };
    f.api[`get${platform}MappingAuthStatus`] = async () => ({
      canManage: false,
      canSignIn: false,
      ownershipReason: 'owned-by-another-account',
      isAuthenticated: true
    });
    component.render(() => hook());
    await settle();
    answer.resolve({
      canManage: true,
      canSignIn: true,
      isAuthenticated: true,
      displayName: 'private-a'
    });
    await settle();
    const current = component.render(() => hook());
    assert.equal(current.authStatus.canManage, false);
    assert.equal(current.authStatus.displayName, undefined);
    await old.startLogin();
    assert.deepEqual(f.calls, []);
    component.unmount();
  });
  test(`${platform} shared mode starts and cancels its captured attempt without an account`, async () => {
    const f = setup();
    f.auth = {
      authenticationEnabled: false,
      authMode: 'none',
      accountId: null,
      sessionId: null,
      isLoading: false
    };
    const view = await mount(() => hook());
    await view.read().startLogin();
    await settle();
    view.render();
    const old = view.read();
    const first = old.state.attemptId;
    old.actions.cancelPendingRequest();
    old.actions.resetAuthForm();
    await (old.cancelLogin ?? old.actions.cancelLogin)();
    await settle();
    f.status = { canManage: true, canSignIn: true, isAuthenticated: false };
    await view.read().refreshStatus();
    view.render();
    await view.read().startLogin();
    await settle();
    view.render();
    const second = view.read().state.attemptId;
    assert.notEqual(first, second);
    old.actions.cancelPendingRequest();
    old.actions.resetAuthForm();
    await (old.cancelLogin ?? old.actions.cancelLogin)();
    assert.equal(view.render().state.attemptId, second);
    assert.ok(f.calls.filter((call) => call[0] === 'cancel').every((call) => call[2] === first));
    view.unmount();
  });
  test(`${platform} explicit recovery is transmitted and late start cannot populate another session`, async () => {
    const f = setup();
    f.status = {
      canRecover: true,
      canSignIn: false,
      canManage: false,
      ownershipReason: 'reauthentication-required',
      isAuthenticated: false
    };
    const answer = deferred();
    f.api[`start${platform}MappingLogin`] = async (_signal, request) => {
      f.calls.push(request);
      return answer.promise;
    };
    const view = await mount(() => hook());
    const pending = view.read().startLogin();
    assert.equal(f.calls[0].recover, true);
    f.auth = { ...f.auth, sessionId: 'session-b' };
    view.render();
    answer.resolve({
      attemptId: f.calls[0].attemptId,
      expiresAtUtc: '2030-01-01T00:00:00Z',
      userCode: 'PRIVATE',
      authorizationUrl: 'private',
      verificationUri: 'private'
    });
    await pending;
    await settle();
    assert.equal(view.render().state.attemptId, null);
    assert.equal(view.read().state.authorizationUrl ?? view.read().state.deviceUserCode, '');
    view.unmount();
  });
}

test('Steam continuation and stale cancellation keep the displayed opaque attempt', async () => {
  const f = setup();
  let access = { canManage: true, canSignIn: true };
  let identity = 'a';
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (_url, options) => {
    const body = JSON.parse(options.body);
    f.calls.push(body);
    access = { canManage: true, canCancel: true, attemptId: body.attemptId };
    return new Response(
      JSON.stringify({
        requiresTwoFactor: true,
        attemptId: body.attemptId,
        expiresAtUtc: '2030-01-01T00:00:00Z'
      })
    );
  };
  const view = await mount(() =>
    useSteamLoginFlow({
      loginUrl: '/login',
      integration: { identity, access, refresh: async () => undefined }
    })
  );
  try {
    view.read().actions.setUsername('steam-a');
    view.read().actions.setPassword('password');
    view.render();
    await view.read().actions.handleAuthenticate();
    view.render();
    const old = view.read();
    const first = old.state.attemptId;
    old.actions.setTwoFactorCode('12345');
    view.render();
    await view.read().actions.handleAuthenticate();
    view.render();
    assert.equal(f.calls[1].attemptId, first);
    old.actions.cancelPendingRequest();
    old.actions.resetAuthForm();
    old.actions.cancelLogin();
    access = { canManage: true, canSignIn: true };
    view.render();
    view.read().actions.setPassword('password');
    view.render();
    await view.read().actions.handleAuthenticate();
    view.render();
    const second = view.read().state.attemptId;
    old.actions.cancelPendingRequest();
    old.actions.resetAuthForm();
    old.actions.cancelLogin();
    assert.equal(view.render().state.attemptId, second);
    assert.notEqual(second, first);
    identity = 'b';
    view.render();
    await settle();
    assert.equal(view.render().state.username, '');
    old.actions.cancelLogin();
    assert.ok(f.calls.filter(Array.isArray).every((call) => call[2] === first));
  } finally {
    view.unmount();
    globalThis.fetch = originalFetch;
  }
});

test('Web API save is main-owner-only in authenticated mode and shared mode remains writable', async () => {
  const f = setup();
  f.keyStatus = { canManage: false, ownershipReason: 'main-owner-required' };
  const view = await mount(() => useSteamApiKey());
  view.read().setApiKey('key');
  view.render();
  await view.read().handleSave('empty', 'failed');
  assert.deepEqual(f.calls, []);
  f.auth = {
    ...f.auth,
    authenticationEnabled: false,
    authMode: 'none',
    accountId: null,
    sessionId: null
  };
  f.keyError = 'errors.integration.statusUnavailable';
  view.render();
  view.render();
  view.read().setApiKey('shared-key');
  view.render();
  await view.read().handleSave('empty', 'failed');
  assert.deepEqual(f.calls, [['save', 'shared-key']]);
  const pending = deferred();
  f.api.testSteamApiKey = () => pending.promise;
  const testing = view.read().handleTest('empty', 'failed');
  f.auth = { ...f.auth, authenticationEnabled: true, accountId: 'b', sessionId: 'b' };
  view.render();
  pending.resolve({ valid: true });
  await testing;
  assert.equal(view.render().testResult, null);
  assert.equal(view.read().apiKey, '');
  view.unmount();
});

test('Steam manual-code switch releases local loading without retiring its admitted attempt', async () => {
  const f = setup();
  const answer = deferred();
  const originalFetch = globalThis.fetch;
  let access = { canSignIn: true };
  let refreshes = 0;
  globalThis.fetch = async (_url, options) => {
    const request = JSON.parse(options.body);
    access = { canCancel: true, attemptId: request.attemptId };
    return answer.promise;
  };
  const view = await mount(() =>
    useSteamLoginFlow({
      loginUrl: '/login',
      integration: {
        identity: 'a',
        access,
        refresh: async () => {
          refreshes++;
        }
      }
    })
  );
  try {
    view.read().actions.setUsername('steam');
    view.read().actions.setPassword('password');
    view.render();
    const pending = view.read().actions.handleAuthenticate();
    view.render();
    const attempt = view.read().state.attemptId;
    assert.equal(view.read().state.loading, true);
    view.read().actions.cancelPendingRequest();
    view.read().actions.setWaitingForMobileConfirmation(false);
    view.read().actions.setNeedsTwoFactor(true);
    view.read().actions.setUseManualCode(true);
    view.render();
    assert.equal(view.read().state.loading, false);
    assert.equal(view.read().state.attemptId, attempt);
    answer.resolve(new Response(JSON.stringify({ success: true })));
    await pending;
    assert.equal(view.render().state.attemptId, attempt);
    assert.equal(f.calls.length, 0);
    assert.ok(refreshes > 0);
  } finally {
    view.unmount();
    globalThis.fetch = originalFetch;
  }
});

test('LANCache logout never clears Steam login or the installation Web API key', async () => {
  const calls = [];
  const handler = bindLifted(
    liftConstArrow(
      'src/components/features/management/steam/AuthenticationManager.tsx',
      'handleLogout'
    ),
    {
      setAuthLoading: () => undefined,
      authService: { logout: async () => calls.push('lancache') },
      refreshAuth: async () => calls.push('refresh'),
      onSuccess: () => undefined,
      onError: () => undefined,
      t: (key) => key,
      getErrorMessage: String
    }
  );
  await handler();
  assert.deepEqual(calls, ['lancache', 'refresh']);
});

test('same-caller Web API refresh failure retains health but refuses mutation until recovery', async () => {
  const f = setup();
  const originalFetch = globalThis.fetch;
  const originalError = console.error;
  console.error = noop;
  globalThis.fetch = async () =>
    new Response(
      JSON.stringify({ version: 'V2', hasApiKey: true, isFullyOperational: true, canManage: true })
    );
  const view = await mount(() => useSteamWebApiStatusState());
  const key = await mount(() => useSteamApiKey());
  const Status = make(
    'src/components/features/management/steam/SteamWebApiStatus.tsx',
    'SteamWebApiStatus',
    {
      ...renderBindings,
      useAuth: () => f.auth,
      useSteamWebApiStatus: () => view.read(),
      useFormattedDateTime: () => 'checked',
      usePicsProgress: () => ({ updateProgress: noop }),
      useNotifications: () => ({
        addNotification: noop,
        updateNotification: noop,
        scheduleAutoDismiss: noop
      }),
      HelpPopover: () => null,
      HelpSection: passthrough,
      HelpDefinition: () => null,
      HelpNote: passthrough,
      SteamWebApiKeyModal: () => null,
      ConfirmationModal: () => null,
      ErrorBlock: ({ title, message }) =>
        React.createElement('div', { role: 'alert' }, title, message)
    }
  );
  try {
    assert.equal(view.read().status.canManage, true);
    globalThis.fetch = async () => new Response('{}', { status: 503 });
    await view.read().refresh();
    view.render();
    assert.equal(view.read().status.version, 'V2');
    assert.equal(view.read().status.hasApiKey, true);
    assert.equal(view.read().status.canManage, true);
    assert.equal(view.read().status.ownershipReason, undefined);
    assert.ok(view.read().error);
    // A failed read shows its box with the reason; the muted permissions line waits for a good read.
    const failedMarkup = renderToStaticMarkup(React.createElement(Status));
    assert.match(failedMarkup, /management\.steamWebApi\.loadError/);
    assert.ok(failedMarkup.includes(view.read().error));
    assert.doesNotMatch(failedMarkup, /errors\.integration\.statusUnavailable/);
    f.keyStatus = view.read().status;
    f.keyError = view.read().error;
    key.read().setApiKey('candidate');
    assert.equal(key.render().canManage, false);
    assert.equal(key.read().ownershipReason, 'errors.integration.statusUnavailable');
    await key.read().handleSave('empty', 'failed');
    await key.read().handleTest('empty', 'failed');
    assert.equal(f.calls.length, 0);
    const retry = deferred();
    globalThis.fetch = () => retry.promise;
    const refreshing = view.read().refresh();
    assert.ok(view.render().error);
    retry.resolve(new Response(JSON.stringify({ canManage: false, ownershipReason: null })));
    await refreshing;
    assert.equal(view.render().error, 'errors.integration.statusUnavailable');
    assert.match(
      renderToStaticMarkup(React.createElement(Status)),
      /errors.integration.statusUnavailable/
    );
    globalThis.fetch = async () =>
      new Response(
        JSON.stringify({
          version: 'V2',
          hasApiKey: true,
          isFullyOperational: true,
          canManage: true
        })
      );
    await view.read().refresh();
    assert.equal(view.render().status.canManage, true);
    f.keyStatus = view.read().status;
    f.keyError = view.read().error;
    assert.equal(key.render().canManage, true);
    await key.read().handleSave('empty', 'failed');
    assert.deepEqual(f.calls, [['save', 'candidate']]);
    f.auth = { ...f.auth, isLoading: true };
    assert.equal(key.render().canManage, false);
    assert.equal(key.read().ownershipReason, 'errors.integration.statusUnavailable');
    assert.match(
      renderToStaticMarkup(React.createElement(Status)),
      /errors.integration.statusUnavailable/
    );
    f.auth = {
      authenticationEnabled: false,
      authMode: 'none',
      accountId: null,
      sessionId: null,
      isLoading: false
    };
    globalThis.fetch = async () => new Response('{}', { status: 503 });
    await view.read().refresh();
    view.render();
    assert.doesNotThrow(() => renderToStaticMarkup(React.createElement(Status)));
  } finally {
    view.unmount();
    key.unmount();
    globalThis.fetch = originalFetch;
    console.error = originalError;
  }
});

const containersHook =
  'src/components/features/management/schedules/scheduled-prefill/useScheduledPrefillContainers.ts';
const actSource = liftConstArrow(containersHook, 'act');
const persistentLoginSource = liftConstArrow(containersHook, 'handlePersistentLogin');

const savedLoginAction = ({
  identity = 'a',
  liveIdentity = identity,
  availability = { available: true, reason: null },
  loading = false,
  recover = async () => undefined
} = {}) => {
  const counters = {
    recovery: 0,
    loads: 0,
    stores: [],
    targets: [],
    attempts: [],
    actions: [],
    errors: [],
    errorActions: []
  };
  const privateAvailabilityIdentityRef = { current: liveIdentity };
  const attempts = { current: new Map() };
  const actionState = {};
  const errorState = {};
  const persistentContainerByServiceRef = {
    current: new Map([['Steam', { sessionId: 'session', isRunning: true }]])
  };
  const setActions = (update) => {
    Object.assign(actionState, update(actionState));
    counters.actions.push(actionState.steam);
  };
  const setErrors = (update) => {
    Object.assign(errorState, update(errorState));
    counters.errors.push(errorState.steam);
  };
  const errorActionState = {};
  const setErrorActions = (update) => {
    Object.assign(errorActionState, update(errorActionState));
    counters.errorActions.push(errorActionState.steam);
  };
  const act = bindLifted(actSource, {
    privateAvailabilityIdentity: identity,
    privateAvailabilityIdentityRef,
    attempts,
    view: { current: { service: 'steam' } },
    persistentContainerByServiceRef,
    getPersistentServiceId: () => 'Steam',
    setActions,
    setErrors,
    setErrorActions,
    recover: async () => {
      counters.recovery += 1;
      await recover();
    },
    loadPersistentContainers: async () => {
      counters.loads += 1;
    },
    getErrorMessage: (error) => error.message
  });
  const login = bindLifted(persistentLoginSource, {
    act,
    getPersistentServiceId: () => 'Steam',
    persistentContainerByServiceRef,
    visibleIntegrationLoginAvailabilityByService: new Map([['steam', availability]]),
    visibleIntegrationLoginErrors: loading ? { steam: 'status failed' } : {},
    activeRef: { current: 'steam' },
    canUseSavedLogin: true,
    loadingIntegrationLoginAvailability: loading,
    getIntegrationReasonKey,
    t: (key) => key,
    baseKey: 'management.schedules.services.scheduledPrefill.config',
    hasActivePersistentLogin: () => false,
    setPersistentLoginStartSessionId: (...args) => counters.stores.push(args),
    setPersistentLoginTarget: (value) => counters.targets.push(value),
    requestPersistentLoginAttempt: (value) => counters.attempts.push(value)
  });
  return {
    actionState,
    attempts,
    counters,
    errorState,
    login,
    privateAvailabilityIdentityRef
  };
};

test('saved-login refusal preserves exact action ownership without login mutation', async () => {
  for (const reason of [
    'owned-by-another-account',
    'reauthentication-required',
    'account-required',
    'no-saved-login'
  ]) {
    const fixture = savedLoginAction({ availability: { available: false, reason } });
    await fixture.login('steam', true);
    assert.deepEqual(fixture.counters.stores, []);
    assert.deepEqual(fixture.counters.targets, []);
    assert.deepEqual(fixture.counters.attempts, []);
    assert.deepEqual(fixture.counters.actions, ['login', undefined]);
    assert.deepEqual(fixture.counters.errors, [undefined, integrationReasonKeys[reason]]);
    // The refusal is a Log in error, so the Services row does not show it beside Start.
    assert.deepEqual(fixture.counters.errorActions, [undefined, 'login']);
    assert.equal(fixture.counters.recovery, 1);
    assert.equal(fixture.counters.loads, 0);
  }
});

test('saved-login loading and retained stale handlers cannot mutate the current owner', async () => {
  const loading = savedLoginAction({ loading: true });
  await loading.login('steam', true);
  assert.deepEqual(loading.counters.stores, []);
  assert.deepEqual(loading.counters.targets, []);
  assert.deepEqual(loading.counters.attempts, []);
  assert.deepEqual(loading.counters.actions, ['login', undefined]);
  assert.deepEqual(loading.counters.errors, [undefined, 'errors.integration.statusUnavailable']);

  const stale = savedLoginAction({ identity: 'a', liveIdentity: 'b' });
  await stale.login('steam', true);
  assert.deepEqual(stale.counters, {
    recovery: 0,
    loads: 0,
    stores: [],
    targets: [],
    attempts: [],
    actions: [],
    errors: [],
    errorActions: []
  });
});

test('identity change during deferred cleanup leaves the replacement action and error untouched', async () => {
  const gate = deferred();
  const fixture = savedLoginAction({ recover: () => gate.promise });
  const pending = fixture.login('steam', true);
  assert.deepEqual(fixture.counters.actions, ['login']);
  assert.deepEqual(fixture.counters.errors, [undefined]);

  fixture.privateAvailabilityIdentityRef.current = 'b';
  fixture.attempts.current.set('steam', { replacement: true });
  fixture.actionState.steam = 'start';
  fixture.errorState.steam = 'new-owner-error';
  const actionWrites = fixture.counters.actions.length;
  const errorWrites = fixture.counters.errors.length;
  gate.resolve();
  await pending;

  assert.equal(fixture.actionState.steam, 'start');
  assert.equal(fixture.errorState.steam, 'new-owner-error');
  assert.equal(fixture.counters.actions.length, actionWrites);
  assert.equal(fixture.counters.errors.length, errorWrites);
  assert.deepEqual(fixture.counters.stores, []);
  assert.deepEqual(fixture.counters.targets, []);
  assert.deepEqual(fixture.counters.attempts, []);
  assert.equal(fixture.counters.loads, 0);
});

test('a Start failure stays cleared after another tab starts and then stops the container', () => {
  const clearStaleStart = liftHookCallback(containersHook, 'useEffect', "=== 'start'");
  let errors = { steam: 'Start failed', epic: 'Stop failed' };
  let errorActions = { steam: 'start', epic: 'stop' };
  const runWith = (steamRunning) =>
    bindLifted(clearStaleStart, {
      SCHEDULED_PREFILL_SERVICE_RUN_ORDER: ['steam', 'epic'],
      errorActions,
      containersByServiceKey: new Map([
        ['steam', { isRunning: steamRunning }],
        ['epic', { isRunning: true }]
      ]),
      setErrors: (update) => {
        errors = update(errors);
      },
      setErrorActions: (update) => {
        errorActions = update(errorActions);
      }
    })();

  runWith(false);
  assert.equal(errors.steam, 'Start failed');
  assert.equal(errorActions.steam, 'start');

  runWith(true);
  runWith(false);
  assert.equal(errors.steam, undefined);
  assert.equal(errorActions.steam, undefined);
  assert.equal(errors.epic, 'Stop failed');
  assert.equal(errorActions.epic, 'stop');
});

test('persistent card renders checking, unavailable and optional available-account states', () => {
  const Card = make(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPersistentCard.tsx',
    'ScheduledPrefillPersistentCard',
    {
      ...renderBindings,
      Card: passthrough,
      Badge: passthrough,
      Tooltip: passthrough,
      StatusDot: () => null,
      useFormattedDateTime: () => 'expires',
      useCountdownTimer: () => 30,
      SCHEDULED_PREFILL_BUTTON_SIZE: 'sm',
      SCHEDULED_PREFILL_PLATFORM_UI: { steam: { icon: () => null } },
      getPersistentServiceId: () => 'Steam',
      isScheduledPrefillAnonymousService: () => false,
      getScheduledPrefillServiceStatus: () => ({
        container: 'running',
        account: 'loginRequired',
        next: 'logIn'
      }),
      getScheduledPrefillStatusFact: (status) => ({ busy: false, tone: null, label: status }),
      isPersistentLoginIntegrationReuse: () => false,
      formatTimeRemaining: (seconds) => `${seconds}s`,
      usePersistentLoginStoreState: () => ({ error: null, sessionUnavailableState: null }),
      getPersistentLoginFailure: (state) => state.error,
      usePersistentLoginCanceling: () => false
    }
  );
  for (const [availability, loading, expected] of [
    [undefined, true, 'savedLoginChecking'],
    [undefined, false, 'errors.integration.statusUnavailable'],
    [{ available: true }, false, 'errors.integration.loginAvailable'],
    [{ available: true, account: 'saved' }, false, 'savedLoginAvailable'],
    [{ available: false, reason: 'no-saved-login' }, false, 'errors.integration.noSavedLogin']
  ]) {
    const markup = renderToStaticMarkup(
      React.createElement(Card, {
        serviceKey: 'steam',
        integrationLoginAvailability: availability,
        integrationLoginAvailabilityLoading: loading,
        container: { isRunning: true, isAuthenticated: false },
        listLoaded: true,
        listFailed: false,
        onStart: noop,
        onStop: noop,
        onLogout: noop,
        onLogin: noop
      })
    );
    assert.ok(markup.includes(expected), expected);
  }
});

for (const [platform, hook] of [
  ['Epic', useEpicMappingAuth],
  ['Xbox', useXboxMappingAuth]
]) {
  test(`${platform} current shared credential visibility follows explicit auth mode and newest REST status`, async () => {
    const f = setup();
    f.status = {
      canManage: true,
      canSignIn: true,
      isAuthenticated: true,
      displayName: 'active-login'
    };
    const view = await mount(() => hook());
    assert.equal(view.read().authStatus.displayName, 'active-login');
    f.auth = {
      authenticationEnabled: false,
      authMode: 'unauthenticated',
      accountId: null,
      sessionId: null,
      isLoading: false
    };
    view.render();
    await settle();
    assert.equal(view.render().authStatus.displayName, 'active-login');
    const previous = deferred();
    f.api[`get${platform}MappingAuthStatus`] = () => previous.promise;
    const old = view.read().refreshStatus();
    f.api[`get${platform}MappingAuthStatus`] = async () => ({
      canManage: false,
      canSignIn: false,
      isAuthenticated: true,
      ownershipReason: 'owned-by-another-account'
    });
    await view.read().refreshStatus();
    previous.resolve({ canManage: true, canSignIn: true, displayName: 'stale-login' });
    await old;
    assert.equal(view.render().state.canAuthenticate, false);
    assert.equal(view.read().authStatus.displayName, undefined);
    f.auth = {
      ...f.auth,
      authenticationEnabled: true,
      authMode: 'authenticated',
      accountId: 'b',
      sessionId: 'b'
    };
    view.render();
    await settle();
    assert.equal(view.render().state.canAuthenticate, false);
    view.unmount();
  });
}

test('all integration modal submissions and provider links refuse disabled actions', async () => {
  for (const platform of ['Steam', 'Epic', 'Xbox']) {
    const path = `src/components/modals/auth/${platform}AuthModal.tsx`;
    let mutations = 0;
    await bindLifted(liftConstArrow(path, 'handleSubmit'), {
      state: { canAuthenticate: false },
      isSubmitting: false,
      loading: false,
      setIsSubmitting: () => mutations++,
      handleAuthenticate: async () => {
        mutations++;
        return true;
      },
      onClose: () => mutations++
    })();
    assert.equal(mutations, 0);
    const source = readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
    assert.match(source, /disabled=\{[^}]*state\.canAuthenticate === false/);
    assert.doesNotMatch(source, /handleSoftClose/);
    assert.match(source, /onClose=\{isKeepPending \? handleExplicitCancel : handleCloseModal\}/);
    assert.match(source, /dismissOnBackdrop=\{!isKeepPending\}/);
    assert.match(source, /getIntegrationReasonKey\(state\.ownershipReason\)/);
  }
  let opens = 0;
  for (const [platform, name] of [
    ['Epic', 'handleOpenAuthUrl'],
    ['Xbox', 'handleOpenVerificationUrl'],
    ['Xbox', 'handleCopyCode']
  ]) {
    await bindLifted(liftConstArrow(`src/components/modals/auth/${platform}AuthModal.tsx`, name), {
      state: { canAuthenticate: false },
      window: { open: () => opens++ },
      copyText: () => opens++
    })();
  }
  assert.equal(opens, 0);
});

test('Epic provider link and code input refuse accepted submissions', () => {
  for (const pending of [
    { loading: true, isSubmitting: false },
    { loading: false, isSubmitting: true }
  ]) {
    let opens = 0;
    bindLifted(
      liftConstArrow('src/components/modals/auth/EpicAuthModal.tsx', 'handleOpenAuthUrl'),
      {
        state: { canAuthenticate: true },
        authorizationUrl: 'https://example.test/epic',
        window: { open: () => opens++ },
        ...pending
      }
    )();
    assert.equal(opens, 0);

    const pendingReact = { ...React, useState: () => [pending.isSubmitting, noop] };
    const Modal = make('src/components/modals/auth/EpicAuthModal.tsx', 'EpicAuthModal', {
      ...renderBindings,
      React: pendingReact,
      FormField: ({ children }) => children({})
    });
    const markup = renderToStaticMarkup(
      React.createElement(Modal, {
        opened: true,
        onClose: noop,
        state: {
          canAuthenticate: true,
          loading: pending.loading,
          needsAuthorizationCode: true,
          authorizationUrl: 'https://example.test/epic',
          authorizationCode: 'accepted-code',
          error: null
        },
        actions: {
          setAuthorizationCode: noop,
          handleAuthenticate: async () => false,
          resetAuthForm: noop,
          cancelPendingRequest: noop
        }
      })
    );
    assert.match(markup, /<button disabled=""[^>]*>[\s\S]*openEpicLogin/);
    assert.match(markup, /<input[^>]*disabled=""/);
  }
});

test('primary recovery keeps every integration modal submission enabled', async () => {
  for (const platform of ['Steam', 'Epic', 'Xbox']) {
    const events = [];
    await bindLifted(
      liftConstArrow(`src/components/modals/auth/${platform}AuthModal.tsx`, 'handleSubmit'),
      {
        state: { canAuthenticate: true },
        isSubmitting: false,
        loading: false,
        setIsSubmitting: (value) => events.push(value),
        handleAuthenticate: async () => true,
        onClose: () => events.push('closed')
      }
    )();
    assert.deepEqual(events, [true, 'closed', false]);
  }
});

test('integration authentication starts mapping only after acceptance', () => {
  for (const path of [
    'src/hooks/useSteamLoginFlow.ts',
    'src/hooks/useSteamAuthentication.ts',
    'src/hooks/useEpicMappingAuth.ts',
    'src/hooks/useXboxMappingAuth.ts',
    'src/components/features/management/steam/SteamLoginManager.tsx',
    'src/components/features/management/epic/EpicDaemonStatus.tsx',
    'src/components/features/management/xbox/XboxDaemonStatus.tsx'
  ]) {
    const source = readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /loginStatusNotifications/);
    assert.doesNotMatch(
      source,
      /type:\s*['"](?:depot_mapping|epic_game_mapping|xbox_game_mapping)['"]/
    );
  }

  const steamSource = readFileSync(
    new URL(
      '../../Api/LancacheManager/Core/Services/SteamKit2/SteamKit2Service.Authentication.cs',
      import.meta.url
    ),
    'utf8'
  );
  assert.doesNotMatch(steamSource, /reporter\.StartAsync\(/);

  const steamControllerSource = readFileSync(
    new URL(
      '../../Api/LancacheManager/Controllers/Prefill/SteamAuthController.cs',
      import.meta.url
    ),
    'utf8'
  );
  assert.match(
    steamControllerSource,
    /if \(result\.Success\)[\s\S]*?if \(request\.AutoStartPicsRebuild\)[\s\S]*?_steamKit2Service\.TryStartRebuild\(/
  );

  const epicSource = readFileSync(
    new URL(
      '../../Api/LancacheManager/Core/Services/EpicMapping/EpicMappingService.Authentication.cs',
      import.meta.url
    ),
    'utf8'
  );
  assert.ok(
    epicSource.indexOf('ExchangeAuthCodeAsync(') < epicSource.indexOf('reporter.StartAsync(')
  );

  const xboxSource = readFileSync(
    new URL(
      '../../Api/LancacheManager/Core/Services/Xbox/XboxCatalogMappingService.Authentication.cs',
      import.meta.url
    ),
    'utf8'
  );
  assert.ok(
    xboxSource.indexOf('HarvestCatalogAsync(') < xboxSource.indexOf('reporter.StartAsync(')
  );
});

test('every ownership reason has matching copy and Steam actions retain one-line tracks', () => {
  for (const locale of ['en', 'zh']) {
    const messages = JSON.parse(
      readFileSync(new URL(`../src/i18n/locales/${locale}.json`, import.meta.url), 'utf8')
    );
    for (const key of Object.values(integrationReasonKeys))
      assert.equal(
        typeof key.split('.').reduce((value, part) => value?.[part], messages),
        'string'
      );
  }
  const css = readFileSync(
    new URL('../src/components/features/management/steam/steamIntegration.css', import.meta.url),
    'utf8'
  );
  assert.match(css, /steam-integration__segments[^}]*width: 13rem/);
  assert.match(css, /steam-integration__pair[^}]*repeat\(2, minmax\(0, 1fr\)\)[^}]*width: 13rem/);
  assert.match(css, /steam-integration__pair > button[^}]*height: 2rem[^}]*white-space: nowrap/);
  assert.match(css, /steam-integration__single[^}]*width: 6\.375rem[^}]*height: 2rem/);
  assert.match(
    css,
    /steam-integration__single[^}]*width: calc\(50% - 0\.125rem\)[^}]*height: 2\.75rem/
  );
  assert.match(
    css,
    /steam-integration__segments[^}]*height: calc\(2\.75rem \+ 8px\)[^}]*}[\s\S]*steam-integration__segments button[^}]*height: 2\.75rem/
  );
  assert.doesNotMatch(css, /white-space: normal/);
  assert.doesNotMatch(css, /color-mix\(/);

  const english = JSON.parse(
    readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
  );
  assert.equal(english.management.steamAuth.accountLogin, 'Sign In');
  assert.equal(english.management.steamAuth.signInAgain, 'Sign In');
  assert.equal(english.management.steamAuth.logout, 'Log Out');
  assert.equal(english.management.steamWebApi.updateApiKey, 'Edit Key');
  assert.equal(english.management.steamWebApi.configureApiKey, 'Add Key');
});

test(
  'rendered integration controls and session rows retain geometry and native disabled behavior',
  { skip: !process.env.INTEGRATION_UI_BROWSER },
  async () => {
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const output = process.env.INTEGRATION_UI_OUTPUT;
    assert.ok(output);
    mkdirSync(output, { recursive: true });
    const noop = () => undefined;
    const h = React.createElement;
    const icon = (props) =>
      h('svg', {
        className: props.className,
        width: props.size ?? 16,
        height: props.size ?? 16,
        'aria-hidden': true
      });
    const passthrough = ({ children }) => h(React.Fragment, null, children);
    const LoadingSpinner = make('src/components/common/LoadingSpinner.tsx', 'LoadingSpinner', {
      React,
      Loader2: icon
    });
    const Button = make('src/components/ui/Button.tsx', 'Button', {
      React,
      LoadingSpinner,
      useOptionalDirectoryPermissionsContext: () => null
    });
    const SegmentedControl = make('src/components/ui/SegmentedControl.tsx', 'SegmentedControl', {
      React,
      Tooltip: passthrough
    });
    const locales = Object.fromEntries(
      ['en', 'zh'].map((locale) => [
        locale,
        JSON.parse(
          readFileSync(new URL(`../src/i18n/locales/${locale}.json`, import.meta.url), 'utf8')
        )
      ])
    );
    const schema = await import(await compileToUrl('../src/services/themeSchema.ts'));
    const themeSource = parseSource('src/services/theme.service.ts');
    const computed = collectNodes(
      themeSource,
      (node) =>
        ts.isMethodDeclaration(node) &&
        node.name.getText(themeSource) === 'generateComputedColorVars'
    )[0];
    const apply = collectNodes(
      themeSource,
      (node) => ts.isMethodDeclaration(node) && node.name.getText(themeSource) === 'applyTheme'
    )[0];
    const generateComputedColorVars = bindLifted(
      `function(colors) ${computed.body.getText(themeSource)}`,
      {
        schemaHexToRgba: schema.hexToRgba,
        readableTextColor: schema.readableTextColor,
        indicatorColor: schema.indicatorColor
      }
    );
    const themeCss = {};
    for (const kind of ['light', 'dark']) {
      const definition = collectNodes(
        themeSource,
        (node) =>
          ts.isObjectLiteralExpression(node) &&
          node.properties.some(
            (property) =>
              ts.isPropertyAssignment(property) &&
              property.name.getText(themeSource) === 'meta' &&
              property.initializer.getText(themeSource).includes(`id: '${kind}-default'`)
          )
      )[0];
      assert.ok(definition);
      const theme = bindLifted(`()=>(${definition.getText(themeSource)})`, {
        complete: schema.parseThemeColors,
        i18n: { t: (key) => key }
      })();
      const applyTheme = bindLifted(`function(theme, options) ${apply.body.getText(themeSource)}`, {
        document: {
          getElementById: () => null,
          createElement: () => ({ remove: noop }),
          documentElement: { setAttribute: noop, style: {} },
          head: {
            appendChild: (element) => {
              themeCss[kind] = element.textContent;
            }
          }
        },
        window: { dispatchEvent: noop },
        APP_EVENTS: { THEME_CHANGE: 'theme-change' },
        Event
      });
      applyTheme.call({ generateComputedColorVars }, theme, { persist: false });
    }
    const assets = new URL('../dist/assets/', import.meta.url);
    const styles = readdirSync(assets)
      .filter((file) => file.endsWith('.css'))
      .sort((a, b) => Number(b.startsWith('index-')) - Number(a.startsWith('index-')))
      .map((file) => readFileSync(new URL(file, assets), 'utf8'))
      .join('\n');
    const shared = {
      React,
      Button,
      SegmentedControl,
      LoadingSpinner,
      getIntegrationReasonKey,
      integrationReasonKeys,
      useEffect: noop,
      useRef: (value) => ({ current: value }),
      useState: (value) => [typeof value === 'function' ? value() : value, noop],
      User: icon,
      UserCheck: icon,
      Key: icon,
      EpicIcon: icon,
      XboxIcon: icon,
      ExternalLink: icon,
      HelpPopover: () => null,
      HelpSection: passthrough,
      HelpDefinition: () => null,
      HelpNote: passthrough,
      Alert: passthrough,
      SteamAuthModal: () => null,
      SteamWebApiKeyModal: () => null,
      ConfirmationModal: () => null,
      usePicsProgress: () => ({ updateProgress: noop }),
      useNotifications: () => ({
        addNotification: noop,
        updateNotification: noop,
        scheduleAutoDismiss: noop
      }),
      useFormattedDateTime: () => '2026-09-13 22:00',
      storage: { getItem: () => null },
      ApiService: {},
      ApiError,
      noAutofill: {},
      useSignalR: () => ({ on: noop, off: noop }),
      useCopyFeedback: () => [false, noop],
      copyText: async () => true,
      cancelAuthModalLogin: noop,
      LoginSteps: () => null,
      LoginAttemptStatus: () => null,
      FormField: ({ label, children }) => h('label', null, label, children({})),
      Modal: ({ opened, children }) =>
        opened ? h('div', { role: 'dialog', className: 'p-4' }, children) : null
    };
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ hasTouch: true });
    const device = await page.context().newCDPSession(page);
    await page.route('**/*', (route) => route.abort());
    const measurements = [];
    const source = parseSource(
      'src/components/features/user/ActiveSessions.tsx',
      ts.ScriptKind.TSX
    );
    const row = collectNodes(
      source,
      (node) =>
        ts.isJsxElement(node) &&
        node.openingElement.attributes.properties.some(
          (attr) =>
            ts.isJsxAttribute(attr) &&
            attr.name.getText(source) === 'className' &&
            attr.initializer
              ?.getText(source)
              .includes('mgmt-row--interactive focus-ring--inset session-row')
        )
    )[0];
    assert.ok(row);
    try {
      for (const viewport of [
        { width: 320, height: 812 },
        { width: 390, height: 844 },
        { width: 768, height: 1024 },
        { width: 1440, height: 900 },
        { width: 900, height: 1600 }
      ]) {
        await page.setViewportSize(viewport);
        for (const locale of ['en', 'zh'])
          for (const theme of ['light', 'dark'])
            for (const zoom of [1, 2]) {
              const t = (key, values = {}) => {
                const text = Object.entries(values).reduce(
                  (text, [name, value]) => text.replaceAll(`{{${name}}}`, String(value)),
                  key.split('.').reduce((value, part) => value?.[part], locales[locale]) ?? key
                );
                return [
                  'management.steamAuth.signInAgain',
                  'management.steamAuth.logout',
                  'management.steamAuth.automatic',
                  'management.steamAuth.manual',
                  'management.steamWebApi.updateApiKey',
                  'management.steamWebApi.remove'
                ].includes(key)
                  ? `${text} ${text}`
                  : text;
              };
              for (const state of [
                'owner',
                'other',
                'reauth',
                'pending',
                'recovery',
                'shared',
                'unavailable'
              ]) {
                const admitted = ['owner', 'reauth', 'shared'].includes(state);
                const access =
                  state === 'unavailable'
                    ? null
                    : {
                        canManage: admitted,
                        canSignIn: admitted,
                        canLogout: admitted,
                        canRecover: state === 'recovery',
                        canCancel: state === 'pending',
                        ownershipReason: {
                          other: 'owned-by-another-account',
                          reauth: 'reauthentication-required',
                          pending: 'login-in-progress',
                          recovery: 'reauthentication-required'
                        }[state]
                      };
                const bindings = {
                  ...shared,
                  useTranslation: () => ({ t }),
                  useAuth: () => ({
                    authenticationEnabled: state !== 'shared',
                    authMode: 'authenticated',
                    accountId: state === 'shared' ? null : 'a',
                    sessionId: 'a',
                    isLoading: false
                  }),
                  useSteamAuth: () => ({
                    access,
                    steamAuthMode:
                      state === 'owner' || state === 'shared' ? 'authenticated' : 'anonymous',
                    username:
                      locale === 'en'
                        ? 'An exceptionally long integration account name'
                        : '一个非常长的集成账户显示名称',
                    refreshSteamAuth: noop,
                    clearAutoLogoutMessage: noop
                  }),
                  useSteamAuthentication: () => ({
                    state: { canAuthenticate: admitted },
                    actions: {}
                  }),
                  useSteamWebApiStatus: () => ({
                    error: null,
                    status: {
                      hasApiKey: true,
                      isFullyOperational: true,
                      canManage: state === 'owner' || state === 'shared',
                      ownershipReason: 'main-owner-required'
                    },
                    loading: false,
                    refresh: noop
                  })
                };
                const Manager = make(
                  'src/components/features/management/steam/SteamLoginManager.tsx',
                  'SteamLoginManager',
                  bindings
                );
                const KeyStatus = make(
                  'src/components/features/management/steam/SteamWebApiStatus.tsx',
                  'SteamWebApiStatus',
                  bindings
                );
                const session = {
                  id: 'session',
                  isCurrentSession: true,
                  lastSeenAt: '2026-09-13T22:00:00Z'
                };
                const sessionRow = bindLifted(
                  `()=>(${row.getText(source)})`,
                  {
                    React,
                    Button,
                    Tooltip: passthrough,
                    StatusDot: () => h('span', { className: 'w-2 h-2' }),
                    ChevronDown: icon,
                    RowActionsMenu: () => null,
                    session,
                    isExpanded: false,
                    rowToggleHandlers: () => ({ role: 'button', tabIndex: 0 }),
                    sessionStatus: 'active',
                    t,
                    name:
                      locale === 'en'
                        ? 'VeryLongAccountNameWithoutBreaksRepeatedForNarrowPortraitLayouts'
                        : '用于狭窄竖屏布局验证的非常长账户名称',
                    account: true,
                    guest: false,
                    deviceLabel: 'Firefox on Windows 11',
                    location:
                      locale === 'en'
                        ? 'A very long city and region description'
                        : '一个非常长的城市和地区描述',
                    flag: '',
                    formatRelativeTime: () => t('activeSessions.labels.lastSeenShort'),
                    canShowRemaining: false,
                    loggingOut: false,
                    handleEditSession: noop,
                    handleLogout: noop,
                    sessionRowsRef: { current: new Map() },
                    toggleSessionExpanded: noop
                  },
                  { jsx: ts.JsxEmit.React }
                )();
                const markup = renderToStaticMarkup(
                  h(
                    'main',
                    { className: 'p-4 space-y-6' },
                    h(Manager, { mockMode: false, authMode: 'authenticated' }),
                    h(KeyStatus),
                    h('section', { className: 'mgmt-list' }, sessionRow)
                  )
                );
                await page.setContent(
                  `<!doctype html><html data-theme="${theme}" class="${theme}"><head><meta charset="utf-8"><style>${styles}</style><style>${themeCss[theme]}</style></head><body>${markup}</body></html>`
                );
                await page.evaluate((value) => {
                  globalThis.document.documentElement.style.zoom = String(value);
                }, zoom);
                await device.send('Emulation.setTouchEmulationEnabled', { enabled: false });
                await device.send('Emulation.setTouchEmulationEnabled', {
                  enabled: true,
                  maxTouchPoints: 1
                });
                const result = await page.evaluate(() => {
                  const rect = (element) => {
                    const r = element.getBoundingClientRect();
                    return {
                      x: r.x,
                      y: r.y,
                      width: r.width,
                      height: r.height,
                      right: r.right,
                      bottom: r.bottom
                    };
                  };
                  const pairs = [
                    ...globalThis.document.querySelectorAll('.steam-integration__pair')
                  ].map((element) => ({
                    track: rect(element),
                    buttons: [...element.querySelectorAll(':scope > button')].map(rect)
                  }));
                  const segments = globalThis.document.querySelector(
                    '.steam-integration__segments'
                  );
                  const single = globalThis.document.querySelector('.steam-integration__single');
                  return {
                    pairs,
                    single: rect(single),
                    segments: {
                      track: rect(segments),
                      buttons: [...segments.querySelectorAll('button')].map(rect)
                    },
                    overflow:
                      globalThis.document.documentElement.scrollWidth >
                      globalThis.document.documentElement.clientWidth,
                    session: rect(globalThis.document.querySelector('.session-row')),
                    disabled: [...globalThis.document.querySelectorAll('button:disabled')].length,
                    coarse: globalThis.matchMedia('(pointer: coarse)').matches
                  };
                });
                for (const pair of [...result.pairs, result.segments])
                  if (pair.buttons.length === 2) {
                    assert.ok(
                      Math.abs(pair.buttons[0].width - pair.buttons[1].width) <= 1,
                      JSON.stringify({ viewport, locale, theme, zoom, state, pair })
                    );
                    assert.ok(Math.abs(pair.buttons[0].height - pair.buttons[1].height) <= 1);
                  }
                for (const button of result.segments.buttons) {
                  assert.ok(
                    button.y >= result.segments.track.y - 1 &&
                      button.bottom <= result.segments.track.bottom + 1,
                    JSON.stringify({ viewport, locale, theme, zoom, state, result })
                  );
                }
                assert.ok(
                  Math.abs(result.pairs[0].track.width - result.segments.track.width) <= 1,
                  JSON.stringify({ viewport, locale, zoom, state, result })
                );
                assert.ok(
                  Math.abs(result.pairs[0].track.right - result.segments.track.right) <= 1,
                  JSON.stringify({ viewport, locale, zoom, state, result })
                );
                assert.ok(
                  Math.abs(result.single.width - result.pairs[1].buttons[1].width) <= 1,
                  JSON.stringify({ viewport, locale, zoom, state, result })
                );
                assert.equal(
                  result.overflow,
                  false,
                  JSON.stringify({ viewport, locale, theme, zoom, state, result })
                );
                measurements.push({ viewport, locale, theme, zoom, state, ...result });
                assert.equal(result.coarse, true);
                if (viewport.width <= 390) {
                  for (const pair of [...result.pairs, result.segments])
                    for (const button of pair.buttons) assert.ok(button.height >= 44 * zoom);
                  assert.ok(result.single.height >= 44 * zoom);
                }
                if (state === 'other' && zoom === 1)
                  await page.screenshot({
                    path: `${output}/integration-${viewport.width}-${viewport.height}-${locale}-${theme}.png`,
                    fullPage: true
                  });
              }
              for (const platform of ['Steam', 'Epic', 'Xbox']) {
                const Component = make(
                  `src/components/modals/auth/${platform}AuthModal.tsx`,
                  `${platform}AuthModal`,
                  { ...shared, useTranslation: () => ({ t }) }
                );
                const state = {
                  canAuthenticate: false,
                  ownershipReason: 'owned-by-another-account',
                  loading: false,
                  needsTwoFactor: false,
                  needsEmailCode: false,
                  waitingForMobileConfirmation: false,
                  useManualCode: false,
                  username: '',
                  password: '',
                  twoFactorCode: '',
                  emailCode: '',
                  needsAuthorizationCode: true,
                  authorizationUrl: 'https://example.test',
                  authorizationCode: 'code',
                  needsDeviceCode: true,
                  deviceUserCode: 'ABC-123',
                  deviceVerificationUri: 'https://example.test',
                  error: null
                };
                const actions = new Proxy({}, { get: () => noop });
                const markup = renderToStaticMarkup(
                  h(Component, { opened: true, state, actions, onClose: noop })
                );
                await page.setContent(
                  `<!doctype html><html class="${theme}"><head><style>${styles}</style><style>${themeCss[theme]}</style></head><body><main></main><div id="portal">${markup}</div></body></html>`
                );
                await page.evaluate((value) => {
                  globalThis.document.documentElement.style.zoom = String(value);
                  globalThis.nativeClicks = 0;
                  globalThis.document
                    .querySelectorAll('button:disabled')
                    .forEach((button) =>
                      button.addEventListener('click', () => globalThis.nativeClicks++)
                    );
                }, zoom);
                const disabled = page.locator('#portal button:disabled');
                assert.ok((await disabled.count()) > 0);
                for (let index = 0; index < (await disabled.count()); index++) {
                  const button = disabled.nth(index);
                  const box = await button.boundingBox();
                  if (box) await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
                  await button.evaluate((element) => element.focus());
                  await page.keyboard.press('Enter');
                  await page.keyboard.press('Space');
                }
                assert.equal(await page.evaluate(() => globalThis.nativeClicks), 0);
              }
            }
      }
    } finally {
      writeFileSync(`${output}/geometry.json`, JSON.stringify(measurements, null, 2));
      await browser.close();
    }
  }
);
