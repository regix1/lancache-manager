import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { compileToUrl, compileTree, moduleUrl } from './transpile-module.mjs';

/**
 * A session's preference rows wait for their Save button: a Save whose writes fail keeps every edit
 * and says so once. A user-role account viewing its own session is not offered the admin-only rows,
 * and an edit whose row disappears (the admin locked the rate meanwhile) is neither counted nor sent.
 * A user-role account's window follows its own session's rate and lock, also when its session type
 * arrives late, when the install-wide rate cannot be read, and when an admin moves its rate.
 *
 * The shipped pane and the shipped refresh-rate provider run on a hook store, with the server's
 * answers given by hand.
 */

const PANE = 'src/components/features/user/SessionPreferences.tsx';
const PREFERENCES_SECTION = 'user.panes.sections.prefs';
const SAVE_FAILED = 'activeSessions.errors.savePreferences';
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
 * One session's preferences pane, seen by a viewer of the given role, over a server that holds one
 * row. Reads and writes answer at once; with `failWrites` every write is refused.
 */
const mountPane = async ({ session, row, viewer, failWrites = false }) => {
  const { hooks, draw } = hookStore();
  const hub = eventHub();
  const server = { stored: { ...DEFAULT_PREFERENCES, ...row } };
  const writes = [];
  const notified = [];
  const write = (name, args, store) => {
    writes.push({ name, args });
    if (failWrites) return Promise.reject(new Error('refused'));
    store();
    return Promise.resolve(undefined);
  };
  const api = {
    getFetchOptions: () => ({}),
    getSessionPreferences: async () => ({ ...server.stored }),
    saveSessionPreferences: (id, body) =>
      write('saveSessionPreferences', [id, body], () => {
        server.stored = { ...server.stored, ...body };
      }),
    setSessionRefreshRate: (id, rate) =>
      write('setSessionRefreshRate', [id, rate], () => {
        server.stored = { ...server.stored, refreshRate: rate === '' ? null : rate };
      })
  };
  globalThis.fetch = () => new Promise(() => undefined);
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const EnhancedDropdown = inert('EnhancedDropdown');
  const MultiSelectDropdown = inert('MultiSelectDropdown');
  const UserPaneSection = inert('UserPaneSection');
  const UserSettingRow = inert('UserSettingRow');
  const { Root: Pane } = await featureTree(PANE, 'SessionPreferences', {
    React: { ...React, ...hooks },
    ...hooks,
    useTranslation: () => ({ t: (key) => key }),
    useSignalR: () => hub,
    useReconnectRefetch: () => undefined,
    useErrorHandler: () => ({ notifyError: (title) => notified.push(title) }),
    useDefaultGuestPreferences: () => ({ prefs: { allowedTimeFormats: [] } }),
    useAuth: () => ({ sessionType: viewer }),
    useRefreshRate: () => ({ systemRate: 'STANDARD' }),
    getErrorMessage: (error) => error.message,
    assertOk: async () => undefined,
    isAccountHolder: (sessionType) => sessionType === 'admin' || sessionType === 'user',
    ApiService: api,
    themeService: {},
    DEFAULT_PREFERENCES,
    PREFILL_SERVICES,
    prefillServiceRecord,
    ToggleSwitch,
    Button,
    EnhancedDropdown,
    MultiSelectDropdown,
    UserPaneSection,
    UserSettingRow
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
  const section = (tree) =>
    elementsIn(
      tree,
      (element) => element.type === UserPaneSection && element.props.title === PREFERENCES_SECTION
    )[0];
  const rowLabels = (tree) =>
    elementsIn(section(tree), (element) => element.type === UserSettingRow).map(
      (element) => element.props.label
    );
  const rowControl = (tree, label, type) => {
    const [row] = elementsIn(
      tree,
      (element) => element.type === UserSettingRow && element.props.label === label
    );
    assert.ok(row, `the pane draws no ${label} row`);
    return elementsIn(row, (element) => element.type === type)[0].props;
  };
  const saveButton = (tree) =>
    elementsIn(
      section(tree),
      (element) => element.type === Button && textOf(element) === 'common.save'
    )[0]?.props;
  const push = (changes) => {
    server.stored = { ...server.stored, ...changes };
    hub.deliver('UserPreferencesUpdated', {
      sessionId: session.id,
      preferences: { ...server.stored }
    });
  };
  return {
    writes,
    notified,
    view,
    settle,
    rowLabels,
    rowControl,
    saveButton,
    push,
    ToggleSwitch,
    EnhancedDropdown
  };
};

const GUEST_SESSION = {
  id: 'guest-1',
  sessionType: 'guest',
  isCurrentSession: false,
  ...Object.fromEntries(PREFILL_SERVICES.map((service) => [service.sessionEnabledField, false]))
};
const ACCOUNT_SESSION = { id: 'account-1', sessionType: 'user', isCurrentSession: false };

test('a Save whose writes fail keeps every edit drawn and reports the failure once', async () => {
  const pane = await mountPane({
    session: GUEST_SESSION,
    row: {},
    viewer: 'admin',
    failWrites: true
  });
  await pane.settle();
  const before = pane.view();
  pane
    .rowControl(before, 'user.guest.preferences.sharpCorners.label', pane.ToggleSwitch)
    .onChange('on');
  await pane.settle();
  pane
    .rowControl(pane.view(), 'activeSessions.preferencesModal.tooltips', pane.ToggleSwitch)
    .onChange('off');
  await pane.settle();

  pane.saveButton(pane.view()).onClick();
  await pane.settle();
  const after = pane.view();
  assert.equal(pane.writes.length > 0, true, 'Save sent nothing');
  assert.deepEqual(
    pane.notified.filter((title) => title === SAVE_FAILED),
    [SAVE_FAILED],
    'a Save with two failed rows must report the failure once'
  );
  assert.deepEqual(
    {
      sharpCorners: pane.rowControl(
        after,
        'user.guest.preferences.sharpCorners.label',
        pane.ToggleSwitch
      ).value,
      tooltips: pane.rowControl(
        after,
        'activeSessions.preferencesModal.tooltips',
        pane.ToggleSwitch
      ).value,
      saveOffered: pane.saveButton(after)?.disabled !== true
    },
    { sharpCorners: 'on', tooltips: 'off', saveOffered: true },
    'a failed Save dropped the edits or stopped offering Save'
  );
});

test('a user-role account viewing its own session is offered neither the lock row nor the Time formats row', async () => {
  const pane = await mountPane({ session: ACCOUNT_SESSION, row: {}, viewer: 'user' });
  await pane.settle();
  const labels = pane.rowLabels(pane.view());
  assert.ok(
    labels.includes('user.panes.prefs.refreshRate'),
    'the unlocked account lost its Refresh rate row'
  );
  assert.deepEqual(
    labels.filter(
      (label) =>
        label === 'user.panes.prefs.accountMayChangeRate' ||
        label === 'user.panes.prefs.timeFormats'
    ),
    [],
    'a user-role account is offered a row the server keeps admin-only'
  );

  const admin = await mountPane({ session: ACCOUNT_SESSION, row: {}, viewer: 'admin' });
  await admin.settle();
  const adminView = admin.view();
  assert.equal(
    admin.rowControl(adminView, 'user.panes.prefs.accountMayChangeRate', admin.ToggleSwitch).value,
    'on',
    'an account session with no lock of its own is drawn as locked to the admin'
  );
});

test('an unsaved rate edit whose row an admin lock hides is neither counted nor sent', async () => {
  const pane = await mountPane({ session: ACCOUNT_SESSION, row: {}, viewer: 'user' });
  await pane.settle();
  pane
    .rowControl(pane.view(), 'user.panes.prefs.refreshRate', pane.EnhancedDropdown)
    .onChange('SLOW');
  await pane.settle();
  assert.notEqual(pane.saveButton(pane.view())?.disabled, true, 'the rate edit left Save disabled');

  pane.push({ refreshRateLocked: true });
  await pane.settle();
  const locked = pane.view();
  assert.equal(
    pane.rowLabels(locked).includes('user.panes.prefs.refreshRate'),
    false,
    'the locked rate is still offered'
  );
  assert.equal(pane.saveButton(locked)?.disabled, true, 'Save still offers the hidden rate edit');

  pane.saveButton(locked).onClick();
  await pane.settle();
  assert.deepEqual(pane.writes, [], 'Save sent the hidden rate edit');
});

/** The shipped refresh-rate provider for the auth state `auth.current`, fed `preferences.current`. */
const mountRefreshRates = async (auth, preferences, systemRead, hub = eventHub()) => {
  const { hooks, draw } = hookStore();
  const ContextValue = inert('ContextValue');
  globalThis.window = { dispatchEvent: () => true };
  globalThis.fetch = (url, options) => {
    const method = options?.method ?? 'GET';
    if (method === 'GET' && url === '/api/system/refresh-rate') return systemRead();
    if (method === 'GET' && url === '/api/system/default-guest-refresh-rate')
      return Promise.resolve(Response.json({ locked: true, refreshRate: 'STANDARD' }));
    return new Promise(() => undefined);
  };
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
      useAuth: () => auth.current,
      useSessionPreferences: () => ({
        currentPreferences: preferences.current,
        error: null,
        resyncPreferences: () => undefined
      }),
      RefreshRateContext: { Provider: ContextValue }
    }
  );
  const published = async () => {
    for (let step = 0; step < 6; step += 1) {
      draw(() => RefreshRates({ children: null }));
      await turn();
    }
    const value = elementsIn(
      draw(() => RefreshRates({ children: null })),
      (element) => element.type === ContextValue
    )[0]?.props.value;
    return { rate: value?.refreshRate, locked: value?.isControlledByAdmin };
  };
  return { published, hub };
};

const systemAnswers = () => Promise.resolve(Response.json({ refreshRate: 'RELAXED' }));
const systemFails = () => Promise.resolve(new Response(null, { status: 500 }));

test('an account window whose own row says unlocked stays unlocked when the install-wide rate cannot be read', async () => {
  const rates = await mountRefreshRates(
    { current: { authMode: 'authenticated', sessionId: 'account-1', sessionType: 'user' } },
    { current: { ...DEFAULT_PREFERENCES, refreshRate: 'SLOW', refreshRateLocked: false } },
    systemFails
  );
  assert.deepEqual(await rates.published(), { rate: 'SLOW', locked: false });
});

test('a user-role session type that arrives after sign-in still settles the window from its own row', async () => {
  const auth = {
    current: { authMode: 'authenticated', sessionId: 'account-1', sessionType: null }
  };
  const preferences = { current: null };
  const rates = await mountRefreshRates(auth, preferences, systemAnswers);
  assert.equal(
    (await rates.published()).locked,
    false,
    'an account whose type is not known yet was locked'
  );

  auth.current = { ...auth.current, sessionType: 'user' };
  assert.equal(
    (await rates.published()).locked,
    true,
    'a user-role account whose row is not read yet was left unlocked'
  );

  preferences.current = { ...DEFAULT_PREFERENCES, refreshRate: null, refreshRateLocked: null };
  assert.deepEqual(
    await rates.published(),
    { rate: 'RELAXED', locked: false },
    'a user-role account with no rate or lock of its own must follow the install-wide rate, unlocked'
  );

  preferences.current = { ...DEFAULT_PREFERENCES, refreshRate: 'SLOW', refreshRateLocked: true };
  assert.deepEqual(
    await rates.published(),
    { rate: 'SLOW', locked: true },
    'the admin lock of the session is not applied'
  );
});

test("an admin's new rate for a user-role session reaches that account's window", async () => {
  const rates = await mountRefreshRates(
    { current: { authMode: 'authenticated', sessionId: 'account-1', sessionType: 'user' } },
    { current: { ...DEFAULT_PREFERENCES, refreshRate: 'SLOW', refreshRateLocked: true } },
    systemAnswers
  );
  assert.equal((await rates.published()).rate, 'SLOW');
  rates.hub.deliver('GuestRefreshRateUpdated', { sessionId: 'account-1', refreshRate: 'ULTRA' });
  assert.equal(
    (await rates.published()).rate,
    'ULTRA',
    "the account window ignored the admin's rate for its session"
  );
});
