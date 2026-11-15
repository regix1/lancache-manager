import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { compileToUrl, compileTree, moduleUrl, ordersOf } from './transpile-module.mjs';

/**
 * The rows of a session's "Preferences for this session" pane save together with one Save button,
 * the way its prefill access rows do: a change sends nothing until Save, a push from another window
 * neither drops the unsaved change nor gets written over by it, and only Save shows busy. An account
 * session's pane offers the guest's rows, its refresh rate and its lock included, and that account's
 * own window follows its own rate rather than the install-wide one. A guest the admin let change its
 * rate stays free to change it after it saves a rate of its own.
 *
 * The shipped pane and the shipped preference and refresh-rate providers run on a hook store, with
 * the server's answers given by hand. Equivalent element walkers are private to
 * `test-saving-controls-show-the-click.mjs` and `test-user-role-hides-install-controls.mjs`.
 */

const PANE = 'src/components/features/user/SessionPreferences.tsx';
const PREFERENCES_SECTION = 'user.panes.sections.prefs';
const PREFILL_SECTION = 'activeSessions.prefill.title';
const SHARP_CORNERS = 'user.guest.preferences.sharpCorners.label';
const TOOLTIPS = 'activeSessions.preferencesModal.tooltips';
const RATES = {
  LIVE: 0,
  ULTRA: 1000,
  REALTIME: 5000,
  STANDARD: 10000,
  RELAXED: 30000,
  SLOW: 60000
};

const { DEFAULT_PREFERENCES } = await import(await compileToUrl('../src/types/userPreferences.ts'));
const { PREFILL_SERVICES, prefillServiceRecord } = await import(
  await compileTree(
    '../src/components/features/prefill/hooks/prefillServiceConfig.ts',
    Object.fromEntries(
      ['SteamIcon', 'EpicIcon', 'BlizzardIcon', 'RiotIcon', 'XboxIcon'].map((name) => [
        `@components/ui/${name}`,
        moduleUrl(`export const ${name} = () => null;`)
      ])
    )
  )
);

/** A stored preferences row as the server sends it, with every admin-only column empty. */
const storedRow = (changes = {}) => ({
  ...DEFAULT_PREFERENCES,
  steamMaxThreadCount: null,
  epicMaxThreadCount: null,
  xboxMaxThreadCount: null,
  battleNetMaxThreadCount: null,
  riotMaxThreadCount: null,
  ...changes
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

/** The text written anywhere inside a node. */
const textOf = (node) =>
  typeof node === 'string'
    ? node
    : Array.isArray(node)
      ? node.map(textOf).join('')
      : React.isValidElement(node)
        ? textOf(node.props.children)
        : '';

/** A SignalR hub that keeps every handler and hands an event to all of them. */
const handlerHub = () => {
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
    isConnected: true
  };
};

const shippedFetch = globalThis.fetch;
const shippedWindow = globalThis.window;
test.afterEach(() => {
  globalThis.fetch = shippedFetch;
  globalThis.window = shippedWindow;
});

/**
 * One session's preferences pane over a server that holds one row. Reads answer at once; every
 * write stays open until the test releases it, and a released write is what the server stores.
 */
const mountPane = async (session, row) => {
  const { hooks, draw } = hookStore();
  const hub = handlerHub();
  const server = { stored: storedRow(row) };
  const writes = [];
  const held = (name, args, store) =>
    new Promise((resolve, reject) => {
      writes.push({
        name,
        args,
        open: true,
        release: () => {
          store();
          resolve(undefined);
        },
        fail: () => reject(new Error('refused'))
      });
    });
  const api = new Proxy(
    {
      getFetchOptions: () => ({}),
      getSessionPreferences: async () => ({ ...server.stored }),
      saveSessionPreferences: (id, body) =>
        held('saveSessionPreferences', [id, body], () => {
          server.stored = { ...server.stored, ...body };
        }),
      setSessionRefreshRate: (id, rate) =>
        held('setSessionRefreshRate', [id, rate], () => {
          server.stored = { ...server.stored, refreshRate: rate === '' ? null : rate };
        })
    },
    {
      get: (target, key) =>
        key in target ? target[key] : (...args) => held(String(key), args, () => undefined)
    }
  );
  // The guest's thread defaults are read with fetch; they never answer, so the rows show no default.
  globalThis.fetch = () => new Promise(() => undefined);
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const UserPaneSection = inert('UserPaneSection');
  const UserSettingRow = inert('UserSettingRow');
  const LoadingSpinner = inert('LoadingSpinner');
  const { Root: Pane } = await featureTree(PANE, 'SessionPreferences', {
    React: { ...React, ...hooks },
    ...hooks,
    useTranslation: () => ({ t: (key) => key }),
    useSignalR: () => hub,
    useAuth: () => ({ sessionType: 'admin' }),
    useRefreshRate: () => ({ systemRate: 'STANDARD' }),
    useReconnectRefetch: () => undefined,
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useDefaultGuestPreferences: () => ({ prefs: { allowedTimeFormats: [] } }),
    getErrorMessage: (error) => error.message,
    assertOk: async () => undefined,
    isAccountHolder: (sessionType) => sessionType === 'admin' || sessionType === 'user',
    ApiService: api,
    // The pane is another session's, so the viewer's own theme store is never reached.
    themeService: {},
    DEFAULT_PREFERENCES,
    PREFILL_SERVICES,
    prefillServiceRecord,
    ToggleSwitch,
    Button,
    UserPaneSection,
    UserSettingRow,
    LoadingSpinner
  });
  const props = {
    session,
    availableThemes: [{ id: 'dark-default', name: 'Dark' }],
    defaultGuestTheme: 'dark-default',
    defaultGuestRefreshRate: 'STANDARD',
    guestDefaultsLoaded: true,
    guestThreadDefaults: {
      values: prefillServiceRecord(() => null),
      error: null,
      retrying: false,
      retry: async () => undefined
    }
  };
  const view = () => draw(() => Pane(props));
  const settle = async () => {
    for (let step = 0; step < 10; step += 1) {
      await turn();
      view();
    }
  };
  const section = (tree, title) =>
    elementsIn(
      tree,
      (element) => element.type === UserPaneSection && element.props.title === title
    )[0];
  const rowLabels = (tree) =>
    elementsIn(
      section(tree, PREFERENCES_SECTION),
      (element) => element.type === UserSettingRow
    ).map((element) => element.props.label);
  const rowSwitch = (tree, label) => {
    const [row] = elementsIn(
      tree,
      (element) => element.type === UserSettingRow && element.props.label === label
    );
    assert.ok(row, `the pane draws no ${label} row`);
    return elementsIn(row, (element) => element.type === ToggleSwitch)[0].props;
  };
  /** The pane's one Save, in the save bar under its sections. */
  const saveButton = (tree) =>
    elementsIn(tree, (element) => element.type === Button && textOf(element) === 'common.save')[0]
      ?.props;
  /** Whether any control of the preferences section other than its Save button shows busy. */
  const rowsBusy = (tree) => {
    const prefs = section(tree, PREFERENCES_SECTION);
    return (
      elementsIn(prefs, (element) => element.type === LoadingSpinner).length > 0 ||
      elementsIn(
        prefs,
        (element) =>
          element.props.loading === true &&
          !(element.type === Button && textOf(element) === 'common.save')
      ).length > 0
    );
  };
  /** Releases every open write, oldest first, until none is left. */
  const releaseWrites = async () => {
    for (let round = 0; round < 10; round += 1) {
      const next = writes.find((write) => write.open);
      if (!next) return;
      next.open = false;
      next.release();
      await settle();
    }
  };
  /** Another window stores a change of this session and the server announces the stored row. */
  const pushFromAnotherWindow = (changes) => {
    server.stored = { ...server.stored, ...changes };
    hub.deliver('UserPreferencesUpdated', {
      sessionId: session.id,
      preferences: { ...server.stored }
    });
  };
  return {
    server,
    writes,
    view,
    settle,
    section,
    rowLabels,
    rowSwitch,
    saveButton,
    rowsBusy,
    releaseWrites,
    pushFromAnotherWindow
  };
};

const GUEST_SESSION = {
  id: 'guest-1',
  sessionType: 'guest',
  isCurrentSession: false,
  ...Object.fromEntries(PREFILL_SERVICES.map((service) => [service.sessionEnabledField, false]))
};
const ACCOUNT_SESSION = { id: 'account-1', sessionType: 'user', isCurrentSession: false };

test("a session's preference rows wait for one Save, keep a pushed change, and only Save shows busy", async () => {
  for (const order of ordersOf(['edit', 'push'])) {
    const pane = await mountPane(GUEST_SESSION, {});
    await pane.settle();
    const cleanSave = pane.saveButton(pane.view());
    const steps = {
      // The admin turns Sharp corners on in the pane.
      edit: () => pane.rowSwitch(pane.view(), SHARP_CORNERS).onChange('on'),
      // The guest turns tooltips off in its own window.
      push: () => pane.pushFromAnotherWindow({ disableTooltips: true })
    };
    for (const step of order) {
      steps[step]();
      await pane.settle();
    }
    const where = `(${order.join(' then the ')})`;
    assert.deepEqual(
      pane.writes.map((write) => write.name),
      [],
      `changing the Sharp corners row saved it at once, before any Save was pressed ${where}`
    );

    const unsaved = pane.view();
    assert.deepEqual(
      {
        sharpCorners: pane.rowSwitch(unsaved, SHARP_CORNERS).value,
        tooltips: pane.rowSwitch(unsaved, TOOLTIPS).value
      },
      { sharpCorners: 'on', tooltips: 'off' },
      `a push from another window dropped the unsaved change, or the pushed change was not drawn ${where}`
    );
    assert.equal(
      cleanSave?.disabled,
      true,
      'before any change the pane must offer a Save with nothing to save'
    );
    assert.ok(pane.saveButton(unsaved), 'the pane has no Save button');
    assert.notEqual(
      pane.saveButton(unsaved).disabled,
      true,
      'an unsaved change left Save disabled'
    );

    pane.saveButton(unsaved).onClick();
    await pane.settle();
    const saving = pane.view();
    assert.deepEqual(
      { saveBusy: pane.saveButton(saving)?.loading === true, rowsBusy: pane.rowsBusy(saving) },
      { saveBusy: true, rowsBusy: false },
      `while the save runs only the Save button may show busy ${where}`
    );

    await pane.releaseWrites();
    const saved = pane.view();
    assert.deepEqual(
      {
        sharpCorners: pane.server.stored.sharpCorners,
        disableTooltips: pane.server.stored.disableTooltips
      },
      { sharpCorners: true, disableTooltips: true },
      `the Save lost the edit or wrote the older tooltips value over the pushed one ${where}`
    );
    assert.deepEqual(
      {
        saveDisabled: pane.saveButton(saved)?.disabled,
        sharpCorners: pane.rowSwitch(saved, SHARP_CORNERS).value,
        tooltips: pane.rowSwitch(saved, TOOLTIPS).value
      },
      { saveDisabled: true, sharpCorners: 'on', tooltips: 'off' },
      `after the save landed the pane must show the stored rows with nothing left to save ${where}`
    );
  }
});

/**
 * The shipped refresh-rate provider for one signed-in role, fed the session preferences given.
 * Settings reads answer at once; a pick's save stays open until the test answers it.
 */
const mountRefreshRates = async (auth, currentPreferences, hub = handlerHub()) => {
  const { hooks, draw } = hookStore();
  const ContextValue = inert('ContextValue');
  const requests = [];
  const preferences = { current: currentPreferences };
  globalThis.window = { dispatchEvent: () => true };
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
        currentPreferences: preferences.current,
        error: null,
        resyncPreferences: () => undefined
      }),
      RefreshRateContext: { Provider: ContextValue }
    }
  );
  const published = () =>
    elementsIn(
      draw(() => RefreshRates({ children: null })),
      (element) => element.type === ContextValue
    )[0]?.props.value;
  return { published, requests, preferences, ContextValue };
};

/** A fetch that answers the settings reads at once and holds every other request for the test. */
const settingsServer = (requests, extra = () => null) => {
  globalThis.fetch = (url, options) => {
    const method = options?.method ?? 'GET';
    const answered = extra(url, method, options);
    if (answered) return Promise.resolve(answered);
    if (method === 'GET' && url === '/api/system/default-guest-refresh-rate')
      return Promise.resolve(Response.json({ locked: true, refreshRate: 'STANDARD' }));
    if (method === 'GET' && url === '/api/system/refresh-rate')
      return Promise.resolve(Response.json({ refreshRate: 'STANDARD' }));
    return new Promise((resolve) => requests.push({ url, method, body: options?.body, resolve }));
  };
};

test("an account session's pane offers the guest's rows, and the account's window follows its own rate", async () => {
  const guest = await mountPane(GUEST_SESSION, {});
  await guest.settle();
  const guestLabels = guest.rowLabels(guest.view());
  const account = await mountPane(ACCOUNT_SESSION, {});
  await account.settle();
  const accountView = account.view();
  // An account names its lock row in its own words; that row is the guest's lock row.
  const accountLabels = account
    .rowLabels(accountView)
    .map((label) =>
      label === 'user.panes.prefs.accountMayChangeRate' ? 'user.panes.prefs.mayChangeRate' : label
    );
  assert.ok(guestLabels.length > 0, 'the guest pane drew no preference rows');
  assert.deepEqual(
    guestLabels.filter((label) => !accountLabels.includes(label)),
    [],
    "an account session's pane lacks rows a guest's pane offers"
  );
  assert.equal(
    account.section(accountView, PREFILL_SECTION),
    undefined,
    "an account session's pane offers prefill access rows, though an account has every service"
  );

  // The admin set this user-role account's own rate and let it change that rate.
  const requests = [];
  settingsServer(requests);
  const rates = await mountRefreshRates(
    { authMode: 'authenticated', sessionId: 'account-1', sessionType: 'user' },
    { ...DEFAULT_PREFERENCES, refreshRate: 'SLOW', refreshRateLocked: false }
  );
  rates.published();
  for (let step = 0; step < 6; step += 1) await turn();
  const shown = rates.published();
  assert.deepEqual(
    { rate: shown?.refreshRate, locked: shown?.isControlledByAdmin },
    { rate: 'SLOW', locked: false },
    "a user-role account's window shows the install-wide rate locked instead of its own rate"
  );

  void shown.setRefreshRate('ULTRA');
  for (let step = 0; step < 6; step += 1) await turn();
  assert.deepEqual(
    requests
      .filter((request) => request.url === '/api/system/refresh-rate')
      .map((request) => request.method),
    [],
    "a user-role account's pick was sent to the install-wide system rate"
  );
  assert.equal(rates.published()?.refreshRate, 'ULTRA', 'the account pick is not drawn');
});

/**
 * The guest's own window: the shipped session preferences provider, whose current row the shipped
 * refresh-rate provider reads, both on one hub.
 */
const mountGuestWindow = async (stored) => {
  const hub = handlerHub();
  const { hooks, draw } = hookStore();
  const PreferencesValue = inert('PreferencesValue');
  const requests = [];
  const server = { stored };
  settingsServer(requests, (url, method) =>
    method === 'GET' && url === '/api/user-preferences' ? Response.json({ ...server.stored }) : null
  );
  const { Root: Preferences } = await featureTree(
    'src/contexts/SessionPreferencesContext.tsx',
    'SessionPreferencesProvider',
    {
      React: { ...React, ...hooks },
      ...hooks,
      useSignalR: () => hub,
      useAuth: () => ({
        isAdmin: false,
        hasSession: true,
        sessionId: 'guest-1',
        isLoading: false,
        authMode: 'guest',
        sessionType: 'guest'
      }),
      ApiService: { getFetchOptions: () => ({}) },
      assertOk: async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
      },
      getErrorMessage: (error) => error.message,
      applyAllowedTimeFormatsChange: () => ({ previous: {} }),
      applyDefaultGuestPreferencesChange: () => ({ previous: {} }),
      getCachedDefaultGuestPreferences: () => ({}),
      useReconnectRefetch: () => undefined,
      SessionPreferencesContext: { Provider: PreferencesValue },
      APP_EVENTS: { PREFERENCE_CHANGED: 'preference-changed' }
    }
  );
  const rates = await mountRefreshRates(
    { authMode: 'guest', sessionId: 'guest-1', sessionType: 'guest' },
    null,
    hub
  );
  // mountRefreshRates set the window; the preferences provider dispatches events on it too.
  globalThis.window = { dispatchEvent: () => true };
  const shown = () => {
    rates.preferences.current = elementsIn(
      draw(() => Preferences({ children: null })),
      (element) => element.type === PreferencesValue
    )[0]?.props.value.currentPreferences;
    return rates.published();
  };
  const settle = async () => {
    for (let step = 0; step < 8; step += 1) {
      await turn();
      shown();
    }
  };
  return { hub, server, requests, shown, settle };
};

test('a guest the admin let change its rate is not locked again after saving a rate of its own', async () => {
  for (const order of ordersOf(['reply', 'broadcast'])) {
    const guest = await mountGuestWindow(
      storedRow({
        refreshRate: 'RELAXED',
        refreshRateLocked: false,
        allowedTimeFormats: ['server-24h', 'utc']
      })
    );
    await guest.settle();
    assert.deepEqual(
      { rate: guest.shown()?.refreshRate, locked: guest.shown()?.isControlledByAdmin },
      { rate: 'RELAXED', locked: false },
      'before the pick the guest has its own unlocked rate'
    );

    void guest.shown().setRefreshRate('SLOW');
    await guest.settle();
    const [save] = guest.requests.filter((request) => request.method === 'PATCH');
    assert.ok(save, 'the guest pick sent no save');
    // The server stores the rate and announces the row before it answers the request. A guest's own
    // announcement carries the stored lock and formats and leaves out the thread caps.
    guest.server.stored = { ...guest.server.stored, refreshRate: 'SLOW' };
    const announced = storedRow({
      refreshRate: guest.server.stored.refreshRate,
      refreshRateLocked: guest.server.stored.refreshRateLocked,
      allowedTimeFormats: guest.server.stored.allowedTimeFormats
    });
    const steps = {
      reply: () => save.resolve(new Response(null, { status: 200 })),
      broadcast: () =>
        guest.hub.deliver('UserPreferencesUpdated', {
          sessionId: 'guest-1',
          preferences: announced
        })
    };
    for (const step of order) {
      steps[step]();
      await guest.settle();
    }
    assert.deepEqual(
      { rate: guest.shown()?.refreshRate, locked: guest.shown()?.isControlledByAdmin },
      { rate: 'SLOW', locked: false },
      `with the ${order.join(' then the ')}, the guest's own save locked its refresh rate again`
    );
  }
});
