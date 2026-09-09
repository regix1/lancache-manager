import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback, moduleUrl } from './transpile-module.mjs';

const storeUrls = new Map();

const loadStore = async (nonce) => {
  const reactUrl = moduleUrl(
    `// ${nonce}\nexport const useSyncExternalStore = (_subscribe, snapshot) => snapshot();`
  );
  const apiUrl = moduleUrl(`// ${nonce}\nexport default {};`);
  const timeoutUrl = moduleUrl(`// ${nonce}\nexport const loginAttemptTimeoutMs = () => 60000;`);
  const guardsUrl = moduleUrl(
    `// ${nonce}\nexport const isRecord = (value) => value !== null && typeof value === 'object';`
  );
  const storeUrl = await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/persistentLoginStore.ts',
    {
      react: reactUrl,
      '@services/api.service': apiUrl,
      '@hooks/loginAttemptTimeout': timeoutUrl,
      './typeGuards': guardsUrl
    }
  );
  storeUrls.set(nonce, storeUrl);
  return await import(storeUrl);
};

const loadHost = async (storeUrl, nonce) => {
  const reactUrl = moduleUrl(
    `// ${nonce}\nexport const useCallback = (callback) => callback; export const useEffect = () => {}; export const useRef = (value) => ({ current: value });`
  );
  const i18nUrl = moduleUrl(
    `// ${nonce}\nexport const useTranslation = () => ({ t: (key) => key });`
  );
  const hostUrl = await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/login/usePersistentLoginHost.ts',
    {
      react: reactUrl,
      'react-i18next': i18nUrl,
      '../persistentLoginStore': storeUrl
    }
  );
  return await import(hostUrl);
};

const loadApi = async (nonce) => {
  const i18nUrl = moduleUrl(`// ${nonce}\nexport default { t: (key) => key };`);
  const antiforgeryUrl = moduleUrl(`// ${nonce}\nexport const antiforgeryHeaders = () => ({});`);
  const constantsUrl = moduleUrl(`// ${nonce}\nexport const API_BASE = '/api';`);
  const errorUrl = moduleUrl(`// ${nonce}\nexport const isAbortError = () => false;`);
  const timezoneUrl = moduleUrl(`// ${nonce}\nexport const getEffectiveTimezone = () => 'UTC';`);
  const interactionUrl = moduleUrl(
    `// ${nonce}\nexport const hasRecentUserInteraction = () => false;`
  );
  const apiErrorUrl = moduleUrl(
    `// ${nonce}\nexport class ApiError extends Error {}; export const assertOk = async () => {}; export const buildApiError = async () => new Error('request failed');`
  );
  const apiUrl = await compileToUrl('../src/services/api.service.ts', {
    '@/i18n': i18nUrl,
    '../utils/antiforgery': antiforgeryUrl,
    '../utils/constants': constantsUrl,
    '../utils/error': errorUrl,
    '../utils/timezone': timezoneUrl,
    '../utils/userInteractionTracker': interactionUrl,
    './apiError': apiErrorUrl
  });
  return (await import(apiUrl)).default;
};

test('a pending reuse hides manual prompting, then rejection permits a manual retry', async () => {
  const store = await loadStore('pending-rejection');
  const host = await loadHost(storeUrls.get('pending-rejection'), 'pending-host');

  store.setPersistentLoginStartSessionId('Steam', 'reuse-session', 'edit-1', 'action-1', true);
  store.updatePersistentLoginState('Steam', (current) => ({ ...current, loading: true }));

  assert.equal(store.isPersistentLoginIntegrationReuse('Steam'), true);
  assert.equal(store.hasActivePersistentLogin('Steam'), true);
  assert.equal(
    host.usePersistentLoginHost({
      service: 'Steam',
      state: { authenticated: false, dismissed: false, hasChallenge: false, loading: true },
      startLogin: () => undefined,
      resumeModal: () => undefined,
      isRunning: true,
      isAuthenticated: false
    }),
    false,
    'reuse waits on the card instead of opening the shared credential form'
  );

  store.clearPersistentLoginIntegrationReuse('Steam');
  store.updatePersistentLoginState('Steam', (current) => ({
    ...current,
    loading: false,
    error: 'integration login rejected'
  }));

  assert.equal(store.isPersistentLoginIntegrationReuse('Steam'), false);
  assert.equal(store.hasActivePersistentLogin('Steam'), false);

  store.setPersistentLoginStartSessionId('Steam', 'manual-session', 'edit-2', 'action-2', false);
  const manualRequest = store.consumePersistentLoginStartRequest('Steam');
  assert.deepEqual(manualRequest, {
    sessionId: 'manual-session',
    editSessionId: 'edit-2',
    editActionId: 'action-2',
    reuseIntegration: false
  });
});

test('a reset invalidates stale reuse before a new manual attempt owns the service', async () => {
  const store = await loadStore('stale-completion');

  store.setPersistentLoginStartSessionId('Epic', 'reuse-session', undefined, undefined, true);
  store.updatePersistentLoginState('Epic', (current) => ({ ...current, loading: true }));
  const reuseEpoch = store.getPersistentLoginEpoch('Epic');

  store.resetPersistentLoginState('Epic');
  store.setPersistentLoginStartSessionId('Epic', 'manual-session', undefined, undefined, false);

  assert.ok(store.getPersistentLoginEpoch('Epic') > reuseEpoch);
  assert.equal(store.isPersistentLoginIntegrationReuse('Epic'), false);
  assert.deepEqual(store.consumePersistentLoginStartRequest('Epic'), {
    sessionId: 'manual-session',
    editSessionId: undefined,
    editActionId: undefined,
    reuseIntegration: false
  });
});

test('an account change clears a pending saved-login reuse without changing shared containers', async () => {
  const store = await loadStore('identity-transition');
  const sharedContainers = new Map([
    ['Steam', { isRunning: true, isAuthenticated: false, sessionId: 'shared-session' }]
  ]);
  let persistentLoginTarget = 'steam';
  store.setPersistentLoginStartSessionId('Steam', 'shared-session', undefined, undefined, true);
  store.updatePersistentLoginState('Steam', (current) => ({ ...current, loading: true }));

  const onIdentityChange = bindLifted(
    liftHookCallback(
      'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
      'useEffect',
      'privateReuseServiceKeys'
    ),
    {
      privateAvailabilityIdentityAppliedRef: { current: 'authenticated:account-a:session-a' },
      privateAvailabilityIdentity: 'authenticated:account-b:session-b',
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epic', 'xbox'],
      isPersistentLoginIntegrationReuse: store.isPersistentLoginIntegrationReuse,
      isScheduledPrefillAccountService: (serviceKey) =>
        serviceKey === 'steam' || serviceKey === 'epic' || serviceKey === 'xbox',
      getPersistentServiceId: (serviceKey) =>
        ({ steam: 'Steam', epic: 'Epic', xbox: 'Xbox' })[serviceKey],
      resetPersistentLoginState: store.resetPersistentLoginState,
      setPersistentLoginTarget: (update) => {
        persistentLoginTarget = update(persistentLoginTarget);
      }
    }
  );

  onIdentityChange();

  assert.equal(store.isPersistentLoginIntegrationReuse('Steam'), false);
  assert.equal(store.hasActivePersistentLogin('Steam'), false);
  assert.equal(persistentLoginTarget, null);
  assert.deepEqual(sharedContainers.get('Steam'), {
    isRunning: true,
    isAuthenticated: false,
    sessionId: 'shared-session'
  });
});

test('an availability response from a prior account cannot populate the new account cache', async () => {
  let resolveAvailability;
  const availability = new Promise((resolve) => {
    resolveAvailability = resolve;
  });
  const availabilityIdentityRef = { current: 'authenticated:account-a:session-a' };
  const availabilityWrites = [];
  const availabilityIdentityWrites = [];
  const loadingWrites = [];
  const loadAvailability = bindLifted(
    liftHookCallback(
      'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
      'useCallback',
      'getPersistentIntegrationLoginAvailability'
    ),
    {
      privateAvailabilityIdentityRef: availabilityIdentityRef,
      integrationLoginRequestRef: { current: null },
      canUseSavedLogin: true,
      requiresIndividualAccount: false,
      setIntegrationLoginAvailabilityByService: (value) => availabilityWrites.push(value),
      setIntegrationLoginAvailabilityIdentity: (value) => availabilityIdentityWrites.push(value),
      setLoadingIntegrationLoginAvailability: (value) => loadingWrites.push(value),
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam'],
      ApiService: {
        getPersistentIntegrationLoginAvailability: () => availability
      },
      getPersistentServiceId: () => 'Steam',
      isAbortError: () => false
    }
  );

  const request = loadAvailability();
  availabilityIdentityRef.current = 'authenticated:account-b:session-b';
  resolveAvailability({ available: true, account: 'account-a', reason: null });
  await request;

  assert.deepEqual(availabilityWrites, []);
  assert.deepEqual(availabilityIdentityWrites, []);
  assert.deepEqual(loadingWrites, [true]);
});

test('an authenticated shared caller without an individual account gets account-required availability', async () => {
  const availabilityWrites = [];
  const availabilityIdentityWrites = [];
  const loadingWrites = [];
  let availabilityCalls = 0;
  const loadAvailability = bindLifted(
    liftHookCallback(
      'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
      'useCallback',
      'getPersistentIntegrationLoginAvailability'
    ),
    {
      privateAvailabilityIdentityRef: { current: 'authenticated::shared-session' },
      integrationLoginRequestRef: { current: null },
      canUseSavedLogin: false,
      requiresIndividualAccount: true,
      setIntegrationLoginAvailabilityByService: (value) => availabilityWrites.push(value),
      setIntegrationLoginAvailabilityIdentity: (value) => availabilityIdentityWrites.push(value),
      setLoadingIntegrationLoginAvailability: (value) => loadingWrites.push(value),
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epic', 'xbox'],
      ApiService: {
        getPersistentIntegrationLoginAvailability: () => {
          availabilityCalls += 1;
          return Promise.resolve({ available: true, account: 'must-not-read', reason: null });
        }
      },
      getPersistentServiceId: (serviceKey) => serviceKey,
      isAbortError: () => false
    }
  );

  await loadAvailability();

  assert.equal(availabilityCalls, 0);
  assert.equal(availabilityIdentityWrites[0], 'authenticated::shared-session');
  assert.deepEqual(loadingWrites, [false]);
  assert.deepEqual(
    [...availabilityWrites[0]],
    [
      ['steam', { available: false, account: null, reason: 'account-required' }],
      ['epic', { available: false, account: null, reason: 'account-required' }],
      ['xbox', { available: false, account: null, reason: 'account-required' }]
    ]
  );
});

function availabilitySession() {
  const pending = [];
  const state = { values: new Map(), loading: false, identity: null };
  const bindings = {
    privateAvailabilityIdentityRef: { current: 'authenticated:a:session-a' },
    integrationLoginRequestRef: { current: null },
    canUseSavedLogin: true,
    requiresIndividualAccount: false,
    setIntegrationLoginAvailabilityByService: (values) => {
      state.values = values;
    },
    setIntegrationLoginAvailabilityIdentity: (identity) => {
      state.identity = identity;
    },
    setLoadingIntegrationLoginAvailability: (loading) => {
      state.loading = loading;
    },
    SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epic'],
    ApiService: {
      getPersistentIntegrationLoginAvailability: (service, signal) =>
        new Promise((resolve, reject) => {
          pending.push({ service, signal, resolve, reject });
        })
    },
    getPersistentServiceId: (service) => service,
    isAbortError: (error) => error.name === 'AbortError'
  };
  const source = liftHookCallback(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
    'useCallback',
    'getPersistentIntegrationLoginAvailability'
  );
  return {
    pending,
    state,
    bindings,
    load: (signal) => bindLifted(source, bindings)(signal),
    finish: (offset, available) => {
      for (const request of pending.slice(offset, offset + 2)) {
        request.resolve({
          available,
          account: available ? 'saved-account' : null,
          reason: available ? null : 'no-saved-login'
        });
      }
    }
  };
}

test('a newer same-owner availability result survives an older response that ignores abort', async () => {
  const f = availabilitySession();
  const old = f.load();
  const current = f.load();
  assert.equal(f.pending[0].signal.aborted, true);
  f.finish(2, true);
  await current;
  const accepted = f.state.values;
  f.finish(0, false);
  await old;
  assert.equal(f.state.values, accepted);
  assert.equal(f.state.values.get('steam').available, true);
  assert.equal(f.state.loading, false);
});

test('fresh and refreshed availability follows authoritative truth in both directions', async () => {
  const f = availabilitySession();
  for (const [index, available] of [true, false, true].entries()) {
    const previous = f.state.values;
    const request = f.load();
    assert.equal(f.state.values, previous, 'checking retains same-owner content');
    assert.equal(f.state.loading, true);
    f.finish(index * 2, available);
    await request;
    assert.equal(f.state.values.get('steam').available, available);
    assert.equal(f.state.loading, false);
  }
});

test('close, reopen and reconnect supersede pending availability without old finally clearing checking', async () => {
  const f = availabilitySession();
  const opening = new AbortController();
  const old = f.load(opening.signal);
  opening.abort();
  const reopened = f.load(new AbortController().signal);
  f.finish(0, false);
  await old;
  assert.equal(f.state.loading, true);
  assert.equal(f.state.values.size, 0);
  const reconnected = f.load();
  f.pending[2].reject(new DOMException('cancelled', 'AbortError'));
  f.pending[3].resolve({ available: false });
  await reopened;
  assert.equal(f.state.loading, true);
  f.finish(4, true);
  await reconnected;
  assert.equal(f.state.values.get('steam').available, true);
  assert.equal(f.state.loading, false);
});

test('account changes and ownerless transitions reject all late private writes', async () => {
  const f = availabilitySession();
  const old = f.load();
  f.bindings.privateAvailabilityIdentityRef.current = 'authenticated:b:session-b';
  const current = f.load();
  f.finish(2, false);
  await current;
  f.finish(0, true);
  await old;
  assert.equal(f.state.identity, 'authenticated:b:session-b');
  assert.equal(f.state.values.get('steam').available, false);
  const pending = f.load();
  f.bindings.privateAvailabilityIdentityRef.current = 'authenticated::shared';
  f.bindings.canUseSavedLogin = false;
  f.bindings.requiresIndividualAccount = true;
  await f.load();
  assert.equal(f.pending.length, 6, 'ownerless transition sends no request');
  assert.equal(f.pending[4].signal.aborted, true);
  f.finish(4, true);
  await pending;
  assert.equal(f.state.values.get('steam').reason, 'account-required');
  assert.equal(f.state.loading, false);
});

test('one service failure preserves Steam success and a retry replaces the unknown hint', async () => {
  const f = availabilitySession();
  const request = f.load();
  f.pending[0].resolve({ available: true, account: 'saved-account', reason: null });
  f.pending[1].reject(new Error('unreachable'));
  await request;
  assert.equal(f.state.values.get('steam').available, true);
  assert.equal(f.state.values.get('epic').reason, 'unknown');
  const retry = f.load();
  f.finish(2, true);
  await retry;
  assert.equal(f.state.values.get('epic').available, true);
  assert.equal(f.state.values.get('epic').reason, null);
});

test('an aborted caller cannot issue requests or disturb a newer availability request', async () => {
  const f = availabilitySession();
  const current = f.load();
  const closed = new AbortController();
  closed.abort();
  await f.load(closed.signal);
  assert.equal(f.pending.length, 2);
  assert.equal(f.pending[0].signal.aborted, false);
  assert.equal(f.state.loading, true);
  f.finish(0, true);
  await current;
});

test('the shared API sends only availability fields and the reuse mode with session ownership', async () => {
  const api = await loadApi('transport');
  const requests = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (input, init) => {
    requests.push({ input: String(input), init });
    return new Response(
      JSON.stringify({ available: true, account: 'masked-account', reason: null }),
      {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      }
    );
  };

  try {
    const availability = await api.getPersistentIntegrationLoginAvailability('Xbox');
    await api.startPersistentLogin('Xbox', 'session-1', 'edit-1', 'action-1', true);

    assert.deepEqual(availability, { available: true, account: 'masked-account', reason: null });
    assert.match(requests[0].input, /integration-login\?service=Xbox$/);
    assert.equal(requests[0].init.body, undefined);
    assert.deepEqual(JSON.parse(requests[1].init.body), {
      service: 'Xbox',
      sessionId: 'session-1',
      editSessionId: 'edit-1',
      editActionId: 'action-1',
      reuseIntegration: true
    });
    assert.equal('accountId' in JSON.parse(requests[1].init.body), false);
    assert.equal('ownerId' in JSON.parse(requests[1].init.body), false);
  } finally {
    globalThis.fetch = originalFetch;
  }
});
