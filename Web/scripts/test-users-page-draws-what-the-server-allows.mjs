import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { compileToUrl, compileTree, moduleUrl } from './transpile-module.mjs';

/**
 * The Users page draws what the server will do. A guest session with no refresh-rate lock of its own
 * follows the lock Guest access sets for every guest, so its "may change the refresh rate" row reads
 * that lock, the way the guest's own browser and the server read it. A user-role account is offered
 * no Create account, which the server refuses for that role.
 *
 * The shipped Users page runs with every feature component it draws, on one hook store, over a server
 * whose answers are given by hand. Equivalent element walkers and a hub are private to
 * `test-session-preference-rows-save-and-refresh-lock.mjs` and `test-user-role-hides-install-controls.mjs`.
 */

const USERS = 'src/components/features/user/UserTab.tsx';
const PREFERENCES_SECTION = 'user.panes.sections.prefs';
const CREATE_ACCOUNT = 'user.accounts.actions.create';
const USE_DEFAULT = 'actions.useDefault';

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

/** The elements a person can see that `matches`: a closed dialog's contents are left out. */
const shownElementsIn = (node, matches) => {
  const found = [];
  const walk = (value) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value) && value.props.opened !== false) {
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
    isConnected: true
  };
};

/** A session row as the sessions list sends it. */
const sessionRow = (changes) => ({
  accountDeleted: false,
  ipAddress: '192.168.1.20',
  userAgent: null,
  createdAt: '2026-11-15T08:00:00Z',
  lastSeenAt: '2026-11-15T09:00:00Z',
  expiresAt: '2026-11-15T20:00:00Z',
  isExpired: false,
  isRevoked: false,
  isCurrentSession: false,
  isSharedSession: false,
  signedOut: false,
  ...Object.fromEntries(PREFILL_SERVICES.map((service) => [service.sessionEnabledField, false])),
  ...changes
});

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

const shippedFetch = globalThis.fetch;
test.afterEach(() => {
  globalThis.fetch = shippedFetch;
});

/**
 * The shipped Users page for one viewer. `guestsLocked` is the lock Guest access holds for every
 * guest; `stored` maps a session id to its stored preferences row; `pick` is the row the page opened
 * on last time. Every read answers at once; a write is never answered.
 */
const mountUsersPage = async ({ viewer, sessions, stored, guestsLocked, pick }) => {
  const { hooks, draw } = hookStore();
  const hub = handlerHub();
  const guestRate = { refreshRate: 'STANDARD', locked: guestsLocked };
  const never = () => new Promise(() => undefined);
  const answers = {
    getFetchOptions: () => ({}),
    getJsonFetchOptions: (body, init) => ({ ...init, body: JSON.stringify(body) }),
    getGuestConfig: async () => ({ isLocked: false }),
    getGuestSessionDuration: async () => ({ durationHours: 6, source: 'ui' }),
    getGuestThemePreference: async () => ({ themeId: 'dark-default' }),
    getDefaultGuestRefreshRate: async () => ({ ...guestRate }),
    getSessionPreferences: async (id) => ({ ...storedRow(stored[id]) })
  };
  const api = new Proxy(answers, {
    get: (target, key) => (key in target ? target[key] : never)
  });
  // The guest-wide rate and lock answer the same on every path; any other request stays open.
  globalThis.fetch = (url) =>
    String(url).endsWith('/system/default-guest-refresh-rate')
      ? Promise.resolve(Response.json({ ...guestRate }))
      : never();
  const UserSettingRow = inert('UserSettingRow');
  const UserPaneSection = inert('UserPaneSection');
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const tree = await featureTree(USERS, 'UserTab', {
    React: { ...React, ...hooks },
    ...hooks,
    useTranslation: () => ({ t: (key) => key }),
    useSignalR: () => hub,
    useAuth: () => viewer,
    useIsDesktop: () => true,
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useReconnectRefetch: () => undefined,
    useHeldValue: (value) => value,
    useCopyFeedback: () => [false, () => undefined],
    useFormattedDateTime: (value) => String(value ?? ''),
    useRefreshRate: () => ({
      systemRate: 'STANDARD',
      refreshRate: 'STANDARD',
      isControlledByAdmin: false
    }),
    useDefaultGuestPreferences: () => ({ prefs: { allowedTimeFormats: [] }, loading: false }),
    useSessionPreferences: () => ({
      getSessionPreferences: () => null,
      loadSessionPreferences: async () => undefined,
      isLoaded: () => true,
      isLoading: () => false
    }),
    useUserSessions: () => ({
      sessions,
      historySessions: [],
      loading: false,
      loadError: null,
      loaded: true,
      reload: async () => undefined,
      getSessionStatus: () => 'active'
    }),
    useUserAccounts: () => ({
      accounts: [],
      loading: false,
      loadError: null,
      loaded: true,
      reload: async () => undefined,
      saveAccount: async () => undefined,
      deleteAccount: async () => undefined,
      setAccountDisabled: async () => undefined,
      disablingAccountId: null
    }),
    useGuestThreadDefaults: () => ({
      values: prefillServiceRecord(() => null),
      error: null,
      retrying: false,
      retry: async () => undefined
    }),
    storage: {
      getJSON: () => pick,
      setJSON: () => undefined,
      removeItem: () => undefined,
      getItem: () => null,
      setItem: () => undefined
    },
    ApiService: api,
    themeService: {
      loadThemes: async () => ({
        themes: [{ meta: { id: 'dark-default', name: 'Dark' } }],
        loadError: null
      })
    },
    getErrorMessage: (error) => error.message,
    isAbortError: () => false,
    useReaderClock: () => ({}),
    formatTimestamp: (value) => String(value ?? ''),
    authService: { authMode: 'authenticated' },
    assertOk: async () => undefined,
    isAccountHolder: (sessionType) => sessionType === 'admin' || sessionType === 'user',
    copyText: async () => true,
    DEFAULT_PREFERENCES,
    PREFILL_SERVICES,
    prefillServiceRecord,
    UserSettingRow,
    UserPaneSection,
    ToggleSwitch,
    Button
  });
  const view = () => draw(() => tree.draw(tree.Root({})));
  const settle = async () => {
    for (let step = 0; step < 12; step += 1) {
      await turn();
      view();
    }
  };
  /** The switch of the preferences row that says whether the session may change its own rate. */
  const rateLockRow = (drawn) => {
    const [prefs] = elementsIn(
      drawn,
      (element) => element.type === UserPaneSection && element.props.title === PREFERENCES_SECTION
    );
    assert.ok(prefs, 'the session pane drew no preferences section');
    const rows = elementsIn(
      prefs,
      (element) =>
        element.type === UserSettingRow &&
        /rate/i.test(String(element.props.label)) &&
        elementsIn(element, (inner) => inner.type === ToggleSwitch).length > 0
    );
    assert.equal(rows.length, 1, 'the preferences section has no single refresh-rate lock row');
    return {
      value: elementsIn(rows[0], (inner) => inner.type === ToggleSwitch)[0].props.value,
      useDefault: elementsIn(
        rows[0],
        (inner) => inner.type === Button && textOf(inner) === USE_DEFAULT
      )[0]?.props
    };
  };
  const createAccountShown = (drawn) =>
    shownElementsIn(
      drawn,
      (element) => element.type === Button && textOf(element) === CREATE_ACCOUNT
    ).length > 0;
  return { view, settle, rateLockRow, createAccountShown };
};

const ADMIN = {
  accountId: 'account-admin',
  isMainAdmin: true,
  authenticationEnabled: true,
  accountMode: 'apiKeyPassword',
  sessionType: 'admin',
  sessionId: 'session-admin',
  logout: async () => undefined,
  refreshAuth: async () => undefined
};
const ADMIN_SESSION = sessionRow({
  id: 'session-admin',
  sessionType: 'admin',
  accountId: 'account-admin',
  username: 'owner',
  isCurrentSession: true
});
const GUEST_SESSION = sessionRow({ id: 'guest-1', sessionType: 'guest' });

test('a guest session with no lock of its own reads the guest-wide refresh-rate lock', async () => {
  const cases = [
    { guestsLocked: false, own: null, clickUseDefault: false, mayChange: 'on' },
    { guestsLocked: false, own: true, clickUseDefault: true, mayChange: 'on' },
    { guestsLocked: true, own: null, clickUseDefault: false, mayChange: 'off' }
  ];
  for (const { guestsLocked, own, clickUseDefault, mayChange } of cases) {
    const page = await mountUsersPage({
      viewer: ADMIN,
      sessions: [ADMIN_SESSION, GUEST_SESSION],
      stored: { 'session-admin': {}, 'guest-1': { refreshRateLocked: own } },
      guestsLocked,
      pick: { kind: 'session', id: 'guest-1' }
    });
    await page.settle();
    if (clickUseDefault) {
      const before = page.rateLockRow(page.view());
      assert.equal(
        before.value,
        'off',
        "the guest's own lock is drawn as locked before Use default"
      );
      assert.ok(before.useDefault, "the guest's own lock row offers no Use default");
      before.useDefault.onClick();
      await page.settle();
    }
    const where =
      `with Guest access ${guestsLocked ? 'locking' : 'not locking'} every guest's rate` +
      (clickUseDefault ? ' and Use default clicked on the session lock' : ' and no session lock');
    assert.equal(
      page.rateLockRow(page.view()).value,
      mayChange,
      `${where}, the pane says the guest ${mayChange === 'on' ? 'may not' : 'may'} change its refresh rate, which its browser and the server do not`
    );
  }
});

test('Create account is offered to an admin and not to a user-role account', async () => {
  const member = {
    ...ADMIN,
    accountId: 'account-member',
    isMainAdmin: false,
    sessionType: 'user',
    sessionId: 'session-member'
  };
  const memberSession = sessionRow({
    id: 'session-member',
    sessionType: 'user',
    accountId: 'account-member',
    username: 'member',
    isCurrentSession: true
  });
  const shownTo = async (viewer, sessions) => {
    const page = await mountUsersPage({
      viewer,
      sessions,
      stored: Object.fromEntries(sessions.map((session) => [session.id, {}])),
      guestsLocked: true,
      pick: null
    });
    await page.settle();
    return page.createAccountShown(page.view());
  };
  assert.equal(
    await shownTo(ADMIN, [ADMIN_SESSION]),
    true,
    'an admin with sign-in on lost Create account'
  );
  assert.equal(
    await shownTo(member, [memberSession]),
    false,
    'a user-role account is offered Create account, which the server refuses for that role'
  );
});
