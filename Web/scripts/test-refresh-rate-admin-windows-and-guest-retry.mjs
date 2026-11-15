import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { ordersOf } from './transpile-module.mjs';

/**
 * Two cases where a window's refresh-rate control showed something other than what the server holds.
 * An admin's second window must follow a new install-wide rate that another admin window saved, in
 * its header and in this browser's polling, not only in the default its Users page draws. A guest
 * whose own preferences read failed and whose Retry then read a locked row must go from the failure
 * straight to the lock, never drawing a picker it may not use. The shipped provider and the shipped
 * selector run on a hook store; the server's reads answer at once from its stored rates, and every
 * save waits until the test answers it.
 */

const RATES = {
  LIVE: 1000,
  ULTRA: 2000,
  REALTIME: 5000,
  STANDARD: 10000,
  RELAXED: 30000,
  SLOW: 60000
};
const GUEST = { authMode: 'guest', sessionId: 'guest-1', sessionType: 'guest' };

const shippedFetch = globalThis.fetch;
const shippedWindow = globalThis.window;
test.afterEach(() => {
  globalThis.fetch = shippedFetch;
  globalThis.window = shippedWindow;
});

/** Every element in a drawn tree that `matches`, children and element props included. */
const elementsIn = (node, matches) => {
  const found = [];
  const walk = (value) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value)) {
      if (matches(value)) found.push(value);
      Object.values(value.props).forEach(walk);
    }
  };
  walk(node);
  return found;
};

/**
 * The server every window of one browser test talks to: the reads answer from `rates` at once, and
 * every save, from whichever window, waits in `saves` until the test answers it.
 */
const serveRates = (rates) => {
  const saves = [];
  globalThis.window = { dispatchEvent: () => true };
  globalThis.fetch = (url, options) => {
    const method = options?.method ?? 'GET';
    if (method === 'GET' && url === '/api/system/refresh-rate')
      return Promise.resolve(Response.json({ refreshRate: rates.systemRate }));
    if (method === 'GET' && url === '/api/system/default-guest-refresh-rate')
      return Promise.resolve(
        Response.json({ locked: rates.guestsLocked, refreshRate: rates.guestDefault })
      );
    return new Promise((resolve) => saves.push({ url, resolve }));
  };
  const answer = (save, status) =>
    save.resolve(
      new Response(JSON.stringify({}), {
        status,
        headers: { 'Content-Type': 'application/json' }
      })
    );
  return { saves, answer };
};

/**
 * One window: the shipped provider on its own hook store and its own hub. `own` is what the window's
 * preferences provider hands it (its own row, the error of that row's read, and the re-read).
 */
const openWindow = async ({ auth, own }) => {
  const { hooks, draw } = hookStore();
  const handlers = new Map();
  const hub = {
    on: (name, handler) => {
      if (!handlers.has(name)) handlers.set(name, new Set());
      handlers.get(name).add(handler);
    },
    off: (name, handler) => handlers.get(name)?.delete(handler),
    isConnected: true
  };
  const ContextValue = inert('ContextValue');
  const { Root: RefreshRates } = await featureTree(
    'src/contexts/RefreshRateContext.tsx',
    'RefreshRateProvider',
    {
      React: { ...React, ...hooks },
      ...hooks,
      i18n: { t: (key) => key },
      APP_EVENTS: { SHOW_TOAST: 'show-toast' },
      REFRESH_RATES: RATES,
      ApiService: {
        getJsonFetchOptions: (body, init) => ({ ...init, body: JSON.stringify(body) })
      },
      assertOk: async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
      },
      getErrorMessage: (error) => error.message,
      useSignalR: () => hub,
      useReconnectRefetch: () => undefined,
      useAuth: () => auth,
      useSessionPreferences: () => ({
        currentPreferences: own.row,
        error: own.error,
        resyncPreferences: own.resync
      }),
      RefreshRateContext: { Provider: ContextValue }
    }
  );
  /** Draws one frame and returns what the provider hands its children in it. */
  const frame = () => draw(() => RefreshRates({ children: null }))?.props.value;
  const settle = async () => {
    for (let step = 0; step < 6; step += 1) {
      frame();
      await turn();
    }
    return frame();
  };
  const hear = (name, event) => {
    for (const handler of handlers.get(name) ?? []) handler(event);
  };
  return { frame, settle, hear };
};

test("an admin's second window shows and polls the install-wide rate another admin window saved", async () => {
  const wrong = [];
  const orders = ordersOf(['the save answers', 'the server announces SLOW']);
  for (const order of orders) {
    const rates = { systemRate: 'STANDARD', guestDefault: 'STANDARD', guestsLocked: true };
    const server = serveRates(rates);
    const windows = {};
    for (const name of ['first', 'second']) {
      windows[name] = await openWindow({
        auth: { authMode: 'authenticated', sessionId: `admin-${name}`, sessionType: 'admin' },
        own: { row: null, error: null, resync: () => Promise.resolve() }
      });
      await windows[name].settle();
    }

    // The first window picks SLOW in its header; the server stores it, announces it to every
    // window, and answers the first window's save.
    void windows.first.frame().setRefreshRate('SLOW');
    const [save] = server.saves.splice(0);
    rates.systemRate = 'SLOW';
    for (const step of order) {
      if (step === 'the save answers') server.answer(save, 200);
      else
        for (const client of Object.values(windows))
          client.hear('SystemRefreshRateChanged', { refreshRate: 'SLOW' });
      for (const client of Object.values(windows)) await client.settle();
    }

    for (const [name, client] of Object.entries(windows)) {
      const shown = await client.settle();
      const seen = {
        header: shown.refreshRate,
        pollEveryMs: shown.getRefreshInterval(),
        usersPageDefault: shown.systemRate
      };
      const held = { header: 'SLOW', pollEveryMs: RATES.SLOW, usersPageDefault: 'SLOW' };
      if (JSON.stringify(seen) !== JSON.stringify(held))
        wrong.push(`${order.join(', then ')}: the ${name} window has ${JSON.stringify(seen)}`);
    }

    // The second window then picks LIVE and that save fails: it goes back to the rate the server
    // holds, not to the rate it showed before the first window's save.
    void windows.second.frame().setRefreshRate('LIVE');
    const [failed] = server.saves.splice(0);
    server.answer(failed, 500);
    const after = await windows.second.settle();
    if (after.refreshRate !== 'SLOW' || after.getRefreshInterval() !== RATES.SLOW)
      wrong.push(
        `${order.join(', then ')}: after its own LIVE pick failed, the second window shows ` +
          `${after.refreshRate} and polls every ${after.getRefreshInterval()} ms`
      );
  }

  assert.ok(orders.length > 0, 'no order of the steps was run');
  assert.deepEqual(
    wrong,
    [],
    'the server holds the install-wide SLOW another admin window saved, yet an admin window ' +
      `still shows or polls at the old STANDARD rate: ${wrong.join('; ')}`
  );
});

test("a guest's Retry after a failed preferences read goes from the failure straight to the lock", async () => {
  const wrong = [];
  for (const [lockedBy, lockedRow] of [
    ['the lock every guest has', { refreshRate: null, refreshRateLocked: null }],
    ["the session's own lock", { refreshRate: 'RELAXED', refreshRateLocked: true }]
  ]) {
    serveRates({ systemRate: 'STANDARD', guestDefault: 'ULTRA', guestsLocked: true });
    const own = { row: null, error: 'HTTP 500' };
    own.resync = () => {
      own.row = lockedRow;
      own.error = null;
      return Promise.resolve();
    };
    const client = await openWindow({ auth: GUEST, own });
    await client.settle();

    let current = client.frame();
    const SectionErrorChip = inert('SectionErrorChip');
    const EnhancedDropdown = inert('EnhancedDropdown');
    const Lock = inert('Lock');
    const { Root: Selector } = await featureTree(
      'src/components/common/RefreshRateSelector.tsx',
      'RefreshRateSelector',
      {
        React,
        useTranslation: () => ({ t: (key) => key }),
        useRefreshRate: () => current,
        useConnectionLost: () => false,
        Tooltip: inert('Tooltip'),
        EnhancedDropdown,
        SectionErrorChip,
        Lock
      }
    );
    /** What the header's refresh-rate control draws in one frame of the provider. */
    const drawnInNextFrame = () => {
      current = client.frame();
      if (current === undefined) return 'nothing';
      const tree = Selector({});
      if (elementsIn(tree, (element) => element.type === SectionErrorChip).length > 0)
        return 'the failure with a Retry';
      if (elementsIn(tree, (element) => element.type === Lock).length > 0)
        return 'locked by the administrator';
      const [picker] = elementsIn(tree, (element) => element.type === EnhancedDropdown);
      return picker ? 'a picker the guest can use' : 'nothing';
    };

    assert.equal(drawnInNextFrame(), 'the failure with a Retry', 'the failed read was not shown');
    const [retry] = elementsIn(Selector({}), (element) => element.type === 'button');
    retry.props.onClick();

    const frames = [];
    for (let step = 0; step < 6; step += 1) {
      frames.push(drawnInNextFrame());
      await turn();
    }
    if (frames.some((drawn) => drawn !== 'locked by the administrator'))
      wrong.push(`${lockedBy}: the frames after Retry drew ${frames.join(', then ')}`);
  }

  assert.deepEqual(
    wrong,
    [],
    "after a guest's Retry read a locked row, the header drew the refresh-rate picker unlocked " +
      `before the lock: ${wrong.join('; ')}`
  );
});
