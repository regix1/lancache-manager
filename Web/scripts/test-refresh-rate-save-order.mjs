import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';

/**
 * A refresh-rate pick is shown at once and saved in the background, and the saves of two picks, or a
 * save and a settings read, can answer in any order. Whatever the order, once every answer is in the
 * selector and this browser's polling show the rate the server holds. The shipped provider runs on a
 * hook store with the server's answers given by hand.
 */

const rates = { LIVE: 1000, ULTRA: 2000, STANDARD: 10000, RELAXED: 30000, SLOW: 60000 };

/** The shipped provider for one role, with every request held until the test answers it. */
const mountRefreshRates = async (auth, currentPreferences) => {
  const { hooks, draw } = hookStore();
  const ContextValue = inert('ContextValue');
  const requests = [];
  const reconnect = { reload: () => undefined };
  globalThis.fetch = (url, options) =>
    new Promise((resolve) => requests.push({ url, method: options?.method ?? 'GET', resolve }));
  globalThis.window = { dispatchEvent: () => true };
  const { Root: RefreshRates } = await featureTree(
    'src/contexts/RefreshRateContext.tsx',
    'RefreshRateProvider',
    {
      React: { ...React, ...hooks },
      ...hooks,
      i18n: { t: (key) => key },
      APP_EVENTS: { SHOW_TOAST: 'show-toast' },
      REFRESH_RATES: rates,
      ApiService: {
        getJsonFetchOptions: (body, init) => ({ ...init, body: JSON.stringify(body) })
      },
      assertOk: async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
      },
      getErrorMessage: (error) => error.message,
      useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
      useReconnectRefetch: (_connected, reload) => {
        reconnect.reload = reload;
      },
      useAuth: () => auth,
      useSessionPreferences: () => ({
        currentPreferences,
        error: null,
        resyncPreferences: () => undefined
      }),
      RefreshRateContext: { Provider: ContextValue }
    }
  );
  const published = () => draw(() => RefreshRates({ children: null }))?.props.value;
  const answer = async (request, status, body) => {
    request.resolve(
      new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' }
      })
    );
    await turn();
    await turn();
  };
  const shown = () => {
    const value = published();
    return { shown: value.refreshRate, pollEveryMs: value.getRefreshInterval() };
  };
  return { published, requests, answer, shown, reconnect };
};

const ADMIN = { authMode: 'authenticated', sessionId: 'admin-session', sessionType: 'admin' };
const GUEST = { authMode: 'guest', sessionId: 'guest-session', sessionType: 'guest' };
const GUEST_PREFERENCES = { refreshRate: 'RELAXED', refreshRateLocked: false };

const shippedFetch = globalThis.fetch;
const shippedWindow = globalThis.window;
test.afterEach(() => {
  globalThis.fetch = shippedFetch;
  globalThis.window = shippedWindow;
});

test('a later pick that fails, then an earlier pick that saves, leaves the earlier pick on screen', async () => {
  const mounted = await mountRefreshRates(ADMIN, null);
  mounted.published();
  await mounted.answer(mounted.requests.shift(), 200, { refreshRate: 'RELAXED' });

  void mounted.published().setRefreshRate('LIVE');
  void mounted.published().setRefreshRate('SLOW');
  const [saveLive, saveSlow] = mounted.requests.splice(0);
  await mounted.answer(saveSlow, 500, {});
  assert.deepEqual(mounted.shown(), { shown: 'RELAXED', pollEveryMs: rates.RELAXED });
  await mounted.answer(saveLive, 200, {});

  assert.deepEqual(
    mounted.shown(),
    { shown: 'LIVE', pollEveryMs: rates.LIVE },
    'the server saved LIVE, yet the selector and the polling stayed on the older RELAXED'
  );
});

test('a pick whose save fails after an earlier pick saved goes back to the earlier pick', async () => {
  const mounted = await mountRefreshRates(ADMIN, null);
  mounted.published();
  await mounted.answer(mounted.requests.shift(), 200, { refreshRate: 'RELAXED' });

  void mounted.published().setRefreshRate('LIVE');
  void mounted.published().setRefreshRate('SLOW');
  const [saveLive, saveSlow] = mounted.requests.splice(0);
  await mounted.answer(saveLive, 200, {});
  assert.deepEqual(mounted.shown(), { shown: 'SLOW', pollEveryMs: rates.SLOW });
  await mounted.answer(saveSlow, 500, {});

  assert.deepEqual(
    mounted.shown(),
    { shown: 'LIVE', pollEveryMs: rates.LIVE },
    'the SLOW save failed, so the screen goes back to LIVE, the rate the server holds'
  );
});

for (const [role, auth, preferences, readUrl, readBody] of [
  ['an admin', ADMIN, null, '/api/system/refresh-rate', { refreshRate: 'RELAXED' }],
  [
    'a guest',
    GUEST,
    GUEST_PREFERENCES,
    '/api/system/default-guest-refresh-rate',
    { locked: false, refreshRate: 'STANDARD' }
  ]
]) {
  test(`for ${role}, a reconnect read during a save that then fails still puts the saved rate back`, async () => {
    const mounted = await mountRefreshRates(auth, preferences);
    mounted.published();
    const mountRead = mounted.requests.shift();
    assert.equal(mountRead.url, readUrl);
    await mounted.answer(mountRead, 200, readBody);
    assert.equal(mounted.shown().shown, 'RELAXED', 'the saved rate was not shown');

    void mounted.published().setRefreshRate('LIVE');
    const [save] = mounted.requests.splice(0);
    assert.equal(save.method, 'PATCH');
    mounted.reconnect.reload();
    const [reconnectRead] = mounted.requests.splice(0);
    assert.equal(reconnectRead.url, readUrl, 'the reconnect sent no settings read');
    // The read is still out when the save fails, so only the save's failure can put the rate back.
    await mounted.answer(save, 500, {});

    assert.deepEqual(
      mounted.shown(),
      { shown: 'RELAXED', pollEveryMs: rates.RELAXED },
      'a reconnect read during the save kept the failed LIVE pick on screen and in the polling'
    );
  });
}
