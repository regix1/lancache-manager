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
const { getIntegrationReasonKey, integrationReasonKeys } = await import(reasonUrl);
const apiErrorUrl = moduleUrl(
  'export class ApiError extends Error { constructor(body){super("refused");this.body=body;} } export const assertOk=async response=>{if(!response.ok)throw new ApiError({stageKey:"errors.integration.statusUnavailable"});};'
);
const { ApiError } = await import(apiErrorUrl);
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
    'const refresh=async()=>{};export const useSteamWebApiStatus=()=>({status:globalThis.integrationTest.keyStatus,refresh});'
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
  state.api.handleResponse = async (response) => {
    const body = await response.json();
    if (!response.ok) throw new ApiError(body);
    return body;
  };
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
  setup();
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    new Response(
      JSON.stringify({ version: 'V2', hasApiKey: true, isFullyOperational: true, canManage: true })
    );
  const view = await mount(() => useSteamWebApiStatusState());
  try {
    assert.equal(view.read().status.canManage, true);
    globalThis.fetch = async () => new Response('{}', { status: 503 });
    await view.read().refresh();
    view.render();
    assert.equal(view.read().status.version, 'V2');
    assert.equal(view.read().status.hasApiKey, true);
    assert.equal(view.read().status.canManage, false);
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
  } finally {
    view.unmount();
    globalThis.fetch = originalFetch;
  }
});

test('saved-login reuse refusal happens before any edit or persistent login mutation', () => {
  const source = liftConstArrow(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
    'handlePersistentLogin'
  );
  for (const reason of [
    'owned-by-another-account',
    'reauthentication-required',
    'account-required',
    'no-saved-login'
  ]) {
    let edits = 0;
    let error;
    bindLifted(source, {
      privateAvailabilityIdentity: 'a',
      privateAvailabilityIdentityRef: { current: 'a' },
      canUseSavedLogin: true,
      loadingIntegrationLoginAvailability: false,
      visibleIntegrationLoginAvailabilityByService: new Map([
        ['steam', { available: false, reason }]
      ]),
      getIntegrationReasonKey,
      integrationReasonKeys,
      setPersistentError: (value) => {
        error = value;
      },
      t: (key) => key,
      recordEditAction: () => edits++
    })('steam', true);
    assert.equal(edits, 0);
    assert.equal(error, integrationReasonKeys[reason]);
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
    assert.match(source, /const handleSoftClose = onClose/);
    assert.match(source, /onClose=\{isKeepPending \? handleSoftClose : handleCloseModal\}/);
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

test('every ownership reason has matching English and Chinese copy and paired controls retain their track', () => {
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
  assert.match(css, /white-space: normal/);
  assert.doesNotMatch(css, /color-mix\(/);
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
    const make = (path, name, bindings) => {
      const source = parseSource(path, ts.ScriptKind.TSX);
      const body = source.statements
        .filter((node) => !ts.isImportDeclaration(node) && !ts.isExportAssignment(node))
        .map((node) => node.getText(source))
        .join('\n')
        .replace(/\bexport\s+/g, '');
      const code = transpile(body, ts.ModuleKind.CommonJS, { jsx: ts.JsxEmit.React });
      const names = Object.keys(bindings).filter((key) => key !== name);
      return new Function(...names, `${code}\nreturn ${name};`)(
        ...names.map((key) => bindings[key])
      );
    };
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
                    return { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right };
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
                  return {
                    pairs,
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
                assert.ok(
                  Math.abs(result.pairs[0].track.width - result.segments.track.width) <= 1,
                  JSON.stringify({ viewport, locale, zoom, state, result })
                );
                assert.ok(
                  Math.abs(result.pairs[0].track.right - result.segments.track.right) <= 1,
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
