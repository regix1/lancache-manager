import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';

/**
 * The header's refresh rate is the installation's rate, saved on the server. When an admin picks a
 * new rate and that save fails, the selector and this browser's polling go back to the rate that is
 * still saved, so the screen neither shows nor polls at a rate the installation does not have.
 */

const rates = { LIVE: 1000, STANDARD: 10000, RELAXED: 30000 };

test('a failed refresh-rate save puts the saved rate back on the selector and the polling', async () => {
  const { hooks, draw } = hookStore();
  const ContextValue = inert('ContextValue');
  const requests = [];
  const shippedFetch = globalThis.fetch;
  const shippedWindow = globalThis.window;
  globalThis.fetch = (url, options) =>
    new Promise((resolve) => requests.push({ url, method: options?.method ?? 'GET', resolve }));
  globalThis.window = { dispatchEvent: () => true };
  const answer = (request, status, body) =>
    request.resolve(
      new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' }
      })
    );
  try {
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
        useReconnectRefetch: () => undefined,
        useAuth: () => ({
          authMode: 'authenticated',
          sessionId: 'admin-session',
          sessionType: 'admin'
        }),
        useSessionPreferences: () => ({
          currentPreferences: null,
          error: null,
          resyncPreferences: () => undefined
        }),
        RefreshRateContext: { Provider: ContextValue }
      }
    );
    const published = () => draw(() => RefreshRates({ children: null }))?.props.value;

    published();
    const [read] = requests.splice(0);
    assert.equal(read.url, '/api/system/refresh-rate', 'the provider did not read the saved rate');
    answer(read, 200, { refreshRate: 'RELAXED' });
    await turn();
    assert.equal(published().refreshRate, 'RELAXED', 'the saved rate was not shown');

    void published().setRefreshRate('LIVE');
    const [save] = requests.splice(0);
    assert.equal(save.method, 'PATCH', 'the pick sent no save');
    answer(save, 500, {});
    await turn();
    await turn();

    const after = published();
    assert.deepEqual(
      { shown: after.refreshRate, pollEveryMs: after.getRefreshInterval() },
      { shown: 'RELAXED', pollEveryMs: rates.RELAXED },
      `the save failed, yet the selector shows ${after.refreshRate} and this browser polls every ${after.getRefreshInterval()} ms, not the saved RELAXED`
    );
  } finally {
    globalThis.fetch = shippedFetch;
    globalThis.window = shippedWindow;
  }
});
