import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { ordersOf } from './transpile-module.mjs';

/**
 * A window's refresh rate is the rate the server holds for it: an admin's window follows the
 * install-wide rate, a guest's or a user-role account's window follows its own session's rate and
 * else its fallback (the guest default, or the install-wide rate). Whatever the admin changes, and in
 * whatever order a window's own saves and announcements answer, the selector and this browser's
 * polling end on that rate, and a window that cannot read its own row says so instead of showing a
 * lock. The shipped provider and the shipped selector run on a hook store; the server's reads answer
 * at once from its stored rates, and every save waits until the test answers it.
 */

const RATES = {
  LIVE: 1000,
  ULTRA: 2000,
  REALTIME: 5000,
  STANDARD: 10000,
  RELAXED: 30000,
  SLOW: 60000
};
const ADMIN = { authMode: 'authenticated', sessionId: 'admin-1', sessionType: 'admin' };
const GUEST = { authMode: 'guest', sessionId: 'guest-1', sessionType: 'guest' };
const ACCOUNT = { authMode: 'authenticated', sessionId: 'account-1', sessionType: 'user' };
const OWN_ROW = { refreshRate: null, refreshRateLocked: null };

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

/** A hub that keeps every handler by event name, so a test can send any event to all of them. */
const eventHub = () => {
  const handlers = new Map();
  return {
    on: (name, handler) => {
      if (!handlers.has(name)) handlers.set(name, new Set());
      handlers.get(name).add(handler);
    },
    off: (name, handler) => handlers.get(name)?.delete(handler),
    deliver: (name, event) => {
      for (const handler of handlers.get(name) ?? []) handler(event);
    },
    names: () => [...handlers.keys()].filter((name) => handlers.get(name).size > 0),
    isConnected: true
  };
};

/**
 * The shipped provider in one window. `server` holds the install-wide rate and the guest default the
 * reads answer with; `own` is what the window's preferences provider hands it (its own row, the
 * error of that row's read, and the re-read). Saves wait in `saves` until `answer`.
 */
const mountWindow = async ({ auth, own, server }) => {
  const { hooks, draw } = hookStore();
  const hub = eventHub();
  const saves = [];
  globalThis.window = { dispatchEvent: () => true };
  globalThis.fetch = (url, options) => {
    const method = options?.method ?? 'GET';
    if (method === 'GET' && url === '/api/system/refresh-rate')
      return Promise.resolve(Response.json({ refreshRate: server.systemRate }));
    if (method === 'GET' && url === '/api/system/default-guest-refresh-rate')
      return Promise.resolve(Response.json({ locked: false, refreshRate: server.guestDefault }));
    return new Promise((resolve) =>
      saves.push({ url, method, body: JSON.parse(options?.body ?? 'null'), resolve })
    );
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
        error: own.error ?? null,
        resyncPreferences: own.resync ?? (() => undefined)
      }),
      RefreshRateContext: { Provider: ContextValue }
    }
  );
  const value = () => draw(() => RefreshRates({ children: null }))?.props.value;
  const settle = async () => {
    for (let step = 0; step < 6; step += 1) {
      value();
      await turn();
    }
    return value();
  };
  const shown = async () => {
    const current = await settle();
    return { shown: current?.refreshRate, pollEveryMs: current?.getRefreshInterval() };
  };
  const answer = async (save, status) => {
    save.resolve(
      new Response(JSON.stringify({}), {
        status,
        headers: { 'Content-Type': 'application/json' }
      })
    );
    await settle();
  };
  return { value, settle, shown, answer, saves, hub };
};

test('"Use default" on a session moves that session\'s own window to its fallback rate', async () => {
  for (const [role, auth, fallback] of [
    ['a guest', GUEST, 'ULTRA'],
    ['a user-role account', ACCOUNT, 'RELAXED']
  ]) {
    const own = { row: { refreshRate: 'SLOW', refreshRateLocked: false } };
    const client = await mountWindow({
      auth,
      own,
      server: { systemRate: 'RELAXED', guestDefault: 'ULTRA' }
    });
    assert.equal((await client.shown()).shown, 'SLOW', `${role}'s own rate was not shown`);

    // What the server announces when an admin saves "Use default" for this session.
    client.hub.deliver('GuestRefreshRateUpdated', { sessionId: auth.sessionId, refreshRate: '' });

    assert.deepEqual(
      await client.shown(),
      { shown: fallback, pollEveryMs: RATES[fallback] },
      `after the admin set ${role}'s rate to the default, its window still shows and polls at ` +
        'the old SLOW rate'
    );
  }
});

test('a user-role or guest window whose own preferences cannot be read shows the failure and recovers on Retry', async () => {
  for (const [role, auth, fallback] of [
    ['a user-role window', ACCOUNT, 'RELAXED'],
    ['a guest window', GUEST, 'ULTRA']
  ]) {
    const own = { row: null, error: 'HTTP 500' };
    const rereads = [];
    own.resync = () => {
      rereads.push(auth.sessionId);
      own.row = OWN_ROW;
      own.error = null;
      return Promise.resolve();
    };
    const client = await mountWindow({
      auth,
      own,
      server: { systemRate: 'RELAXED', guestDefault: 'ULTRA' }
    });
    await client.settle();
    const Tooltip = inert('Tooltip');
    const EnhancedDropdown = inert('EnhancedDropdown');
    const SectionErrorChip = inert('SectionErrorChip');
    const Lock = inert('Lock');
    const { Root: Selector } = await featureTree(
      'src/components/common/RefreshRateSelector.tsx',
      'RefreshRateSelector',
      {
        React,
        useTranslation: () => ({ t: (key) => key }),
        useRefreshRate: () => client.value(),
        useConnectionLost: () => false,
        Tooltip,
        EnhancedDropdown,
        SectionErrorChip,
        Lock
      }
    );
    const drawn = () => {
      const tree = Selector({});
      if (elementsIn(tree, (element) => element.type === SectionErrorChip).length > 0)
        return 'the failure with a Retry';
      if (elementsIn(tree, (element) => element.type === Lock).length > 0)
        return 'locked by the administrator';
      const [picker] = elementsIn(tree, (element) => element.type === EnhancedDropdown);
      return picker ? `a picker on ${picker.props.value}` : 'nothing';
    };

    assert.ok(
      client.value(),
      `${role} whose own preferences read failed draws a blank page instead of the failure`
    );
    assert.equal(
      drawn(),
      'the failure with a Retry',
      `${role} whose own preferences read failed shows its rate as set by the ` +
        'administrator, with no Retry, although its session is not locked'
    );

    const [retry] = elementsIn(Selector({}), (element) => element.type === 'button');
    retry.props.onClick();
    await client.settle();
    assert.deepEqual(
      rereads,
      [auth.sessionId],
      `${role}: Retry did not read the own preferences again`
    );
    assert.equal(
      drawn(),
      `a picker on ${fallback}`,
      `${role}: after a Retry that read the row, no picker came back`
    );
  }
});

test('a user-role window that follows the install-wide rate moves when an admin changes it', async () => {
  const server = { systemRate: 'RELAXED', guestDefault: 'STANDARD' };
  const client = await mountWindow({ auth: ACCOUNT, own: { row: OWN_ROW }, server });
  assert.equal((await client.shown()).shown, 'RELAXED', 'the install-wide rate was not shown');

  // An admin saves SLOW in the header: the server announces it under the name SignalREvents.cs
  // sends, and its reads answer SLOW from now.
  server.systemRate = 'SLOW';
  client.hub.deliver('SystemRefreshRateChanged', { refreshRate: server.systemRate });

  assert.deepEqual(
    await client.shown(),
    { shown: 'SLOW', pollEveryMs: RATES.SLOW },
    'a user-role window with no rate of its own keeps showing and polling at the old ' +
      'install-wide RELAXED after an admin saved SLOW'
  );
});

test("an admin's saved header pick becomes the install-wide rate the Users page draws as the default", async () => {
  const server = { systemRate: 'STANDARD', guestDefault: 'STANDARD' };
  const client = await mountWindow({ auth: ADMIN, own: { row: null }, server });
  assert.equal((await client.settle()).systemRate, 'STANDARD');

  void client.value().setRefreshRate('SLOW');
  const [save] = client.saves.splice(0);
  assert.equal(save.url, '/api/system/refresh-rate');
  server.systemRate = 'SLOW';
  await client.answer(save, 200);

  assert.equal(
    (await client.settle()).systemRate,
    'SLOW',
    'the server holds SLOW, yet the admin window still hands the Users page STANDARD as the ' +
      "install-wide rate a user-role account's default is drawn from"
  );
});

test("an admin window whose own save fails after another admin's change goes back to that change", async () => {
  const server = { systemRate: 'STANDARD', guestDefault: 'STANDARD' };
  const client = await mountWindow({ auth: ADMIN, own: { row: null }, server });
  await client.settle();

  void client.value().setRefreshRate('LIVE');
  const [save] = client.saves.splice(0);
  // Another admin window saves SLOW while this window's LIVE save is still on the wire.
  server.systemRate = 'SLOW';
  client.hub.deliver('SystemRefreshRateChanged', { refreshRate: server.systemRate });
  await client.answer(save, 500);

  assert.deepEqual(
    await client.shown(),
    { shown: 'SLOW', pollEveryMs: RATES.SLOW },
    'the server holds the SLOW another admin saved, yet after its own LIVE save failed the ' +
      'window went back to the STANDARD it held before that change'
  );
});

test("an admin's two quick picks and a later failed pick end on the newest saved pick in every order", async () => {
  const wrong = [];
  const steps = ['LIVE saves', 'SLOW saves', 'ULTRA is picked', 'ULTRA fails'];
  const orders = ordersOf(steps).filter(
    (order) => order.indexOf('ULTRA is picked') < order.indexOf('ULTRA fails')
  );
  for (const order of orders) {
    const client = await mountWindow({
      auth: ADMIN,
      own: { row: null },
      server: { systemRate: 'STANDARD', guestDefault: 'STANDARD' }
    });
    await client.settle();
    void client.value().setRefreshRate('LIVE');
    void client.value().setRefreshRate('SLOW');
    const pending = {};
    [pending.LIVE, pending.SLOW] = client.saves.splice(0);
    for (const step of order) {
      if (step === 'ULTRA is picked') {
        void client.value().setRefreshRate('ULTRA');
        [pending.ULTRA] = client.saves.splice(0);
      } else {
        const [rate, outcome] = step.split(' ');
        await client.answer(pending[rate], outcome === 'saves' ? 200 : 500);
      }
    }
    const ended = await client.shown();
    if (ended.shown !== 'SLOW' || ended.pollEveryMs !== RATES.SLOW)
      wrong.push(`${order.join(', ')}: shows ${ended.shown}, polls every ${ended.pollEveryMs} ms`);
  }

  assert.ok(orders.length > 0, 'no order of the steps was run');
  assert.deepEqual(
    wrong,
    [],
    'the server holds SLOW, the newest pick it saved, yet after the failed ULTRA pick the window ' +
      `shows and polls at an older pick: ${wrong.join('; ')}`
  );
});

test("a window's own announcement of its first pick never redraws that pick while the second saves", async () => {
  const wrong = [];
  const steps = ['LIVE saves', 'LIVE is announced', 'SLOW saves', 'SLOW is announced'];
  const orders = ordersOf(steps).filter(
    (order) => order.indexOf('LIVE is announced') < order.indexOf('SLOW is announced')
  );
  for (const [role, auth] of [
    ['a guest', GUEST],
    ['a user-role account', ACCOUNT]
  ]) {
    for (const order of orders) {
      const own = { row: { refreshRate: 'RELAXED', refreshRateLocked: false } };
      const client = await mountWindow({
        auth,
        own,
        server: { systemRate: 'STANDARD', guestDefault: 'STANDARD' }
      });
      await client.settle();
      void client.value().setRefreshRate('LIVE');
      void client.value().setRefreshRate('SLOW');
      const pending = {};
      [pending.LIVE, pending.SLOW] = client.saves.splice(0);
      let slowSaving = true;
      for (const step of order) {
        const [rate, outcome] = step.split(' ');
        if (outcome === 'saves') {
          await client.answer(pending[rate], 200);
          if (rate === 'SLOW') slowSaving = false;
        } else {
          // The server's UserPreferencesUpdated for this session, stored by the preferences provider.
          own.row = { refreshRate: rate, refreshRateLocked: false };
        }
        const { shown } = await client.shown();
        if (slowSaving && shown !== 'SLOW')
          wrong.push(`${role}, after ${step} in ${order.join(', ')}: shows ${shown}`);
      }
      const ended = await client.shown();
      if (ended.shown !== 'SLOW')
        wrong.push(`${role}, ${order.join(', ')}: ends on ${ended.shown}`);
    }
  }

  assert.ok(orders.length > 0, 'no order of the steps was run');
  assert.deepEqual(
    wrong,
    [],
    `the newest pick is SLOW, yet the window went back to the older LIVE pick: ${wrong.join('; ')}`
  );
});
