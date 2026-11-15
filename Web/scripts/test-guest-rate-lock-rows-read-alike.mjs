import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { compileToUrl, compileTree, moduleUrl } from './transpile-module.mjs';

/**
 * Guest access sets whether every guest may change its refresh rate, and a guest session's pane sets
 * the same thing for one guest. The two rows name that setting in the same words, and On means the
 * same there: the guest may change its rate. So with Guest access locking every guest's rate, both
 * rows read Off. Rows are found by the words drawn on them, in each language the app ships.
 *
 * The shipped Users page runs with every feature component it draws, on one hook store, over a server
 * whose answers are given by hand. The page mount is like the private one in
 * `test-users-page-draws-what-the-server-allows.mjs`, and the element walkers like those private to
 * `test-session-preference-rows-save-and-refresh-lock.mjs`.
 */

const USERS = 'src/components/features/user/UserTab.tsx';
const LOCALES = ['en', 'zh'];
/** Words that name the refresh rate on a row label, per language. */
const RATE_WORDS = { en: /refresh rate/i, zh: /刷新率/ };

const texts = Object.fromEntries(
  LOCALES.map((locale) => [
    locale,
    JSON.parse(readFileSync(new URL(`../src/i18n/locales/${locale}.json`, import.meta.url), 'utf8'))
  ])
);

/** The text a key shows in `locale`; a key with no plain string there (a plural) shows as itself. */
const translate =
  (locale) =>
  (key, vars = {}) => {
    const text = key.split('.').reduce((node, part) => node?.[part], texts[locale]);
    return typeof text === 'string'
      ? text.replace(/{{(\w+)}}/g, (_, name) => String(vars[name]))
      : key;
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

/** A SignalR hub that keeps every handler. */
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

const ADMIN = {
  accountId: 'account-admin',
  isMainAdmin: true,
  isAdmin: true,
  authenticationEnabled: true,
  authMode: 'authenticated',
  accountMode: 'apiKeyPassword',
  sessionType: 'admin',
  sessionId: 'session-admin',
  logout: async () => undefined,
  refreshAuth: async () => undefined
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
const SESSIONS = [
  sessionRow({
    id: 'session-admin',
    sessionType: 'admin',
    accountId: 'account-admin',
    username: 'owner',
    isCurrentSession: true
  }),
  sessionRow({ id: 'guest-1', sessionType: 'guest' })
];

const shippedFetch = globalThis.fetch;
test.afterEach(() => {
  globalThis.fetch = shippedFetch;
});

/**
 * The shipped Users page in `locale`, opened on `pick`, with Guest access locking every guest's rate
 * and the guest session holding no lock of its own. Reads answer at once; writes never answer.
 * Returns the label and the switch value of each row that names the refresh rate and holds a switch.
 */
const rateSwitchRows = async (locale, pick) => {
  const { hooks, draw } = hookStore();
  const guestRate = { refreshRate: 'STANDARD', locked: true };
  const never = () => new Promise(() => undefined);
  const answers = {
    getFetchOptions: () => ({}),
    getJsonFetchOptions: (body, init) => ({ ...init, body: JSON.stringify(body) }),
    getGuestConfig: async () => ({ isLocked: false }),
    getGuestSessionDuration: async () => ({ durationHours: 6, source: 'ui' }),
    getGuestThemePreference: async () => ({ themeId: 'dark-default' }),
    getDefaultGuestRefreshRate: async () => ({ ...guestRate }),
    getSessionPreferences: async () => ({
      ...DEFAULT_PREFERENCES,
      refreshRateLocked: null,
      steamMaxThreadCount: null,
      epicMaxThreadCount: null,
      xboxMaxThreadCount: null,
      battleNetMaxThreadCount: null,
      riotMaxThreadCount: null
    })
  };
  const api = new Proxy(answers, {
    get: (target, key) => (key in target ? target[key] : never)
  });
  globalThis.fetch = (url) =>
    String(url).endsWith('/system/default-guest-refresh-rate')
      ? Promise.resolve(Response.json({ ...guestRate }))
      : never();
  const UserSettingRow = inert('UserSettingRow');
  const ToggleSwitch = inert('ToggleSwitch');
  const tree = await featureTree(USERS, 'UserTab', {
    React: { ...React, ...hooks },
    ...hooks,
    useTranslation: () => ({ t: translate(locale) }),
    useSignalR: handlerHub,
    useAuth: () => ADMIN,
    useIsDesktop: () => true,
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useReconnectRefetch: () => undefined,
    useHeldValue: (value) => value,
    useCopyFeedback: () => [false, () => undefined],
    useFormattedDateTime: (value) => String(value ?? ''),
    useReaderClock: () => ({}),
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
      sessions: SESSIONS,
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
    formatTimestamp: (value) => String(value ?? ''),
    authService: { authMode: 'authenticated' },
    assertOk: async () => undefined,
    isAccountHolder: (sessionType) => sessionType === 'admin' || sessionType === 'user',
    copyText: async () => true,
    DEFAULT_PREFERENCES,
    PREFILL_SERVICES,
    prefillServiceRecord,
    UserSettingRow,
    ToggleSwitch,
    // The dialogs draw after the pane and hold hooks of their own; neither is open here, and leaving
    // them out keeps each hook in its slot when the Guest access pane appears after its first load.
    SessionDialogs: inert('SessionDialogs'),
    AccountDialogs: inert('AccountDialogs')
  });
  const view = () => draw(() => tree.draw(tree.Root({})));
  for (let step = 0; step < 12; step += 1) {
    await turn();
    view();
  }
  return elementsIn(
    view(),
    (element) =>
      element.type === UserSettingRow &&
      RATE_WORDS[locale].test(String(element.props.label)) &&
      elementsIn(element, (inner) => inner.type === ToggleSwitch).length > 0
  ).map((row) => ({
    label: String(row.props.label),
    value: elementsIn(row, (inner) => inner.type === ToggleSwitch)[0].props.value
  }));
};

test("Guest access and a guest session's refresh-rate switch read alike, Off while every guest is locked", async () => {
  for (const locale of LOCALES) {
    const guestAccess = await rateSwitchRows(locale, { kind: 'guestAccess' });
    const guestSession = await rateSwitchRows(locale, { kind: 'session', id: 'guest-1' });
    assert.equal(
      guestAccess.length,
      1,
      `${locale}: Guest access has no single refresh-rate switch`
    );
    assert.equal(
      guestSession.length,
      1,
      `${locale}: the guest session pane has no single refresh-rate switch`
    );
    assert.deepEqual(
      { label: guestAccess[0].label, value: guestAccess[0].value },
      { label: guestSession[0].label, value: 'off' },
      `${locale}: with every guest locked, Guest access reads "${guestAccess[0].label}" ${guestAccess[0].value} while the guest session reads "${guestSession[0].label}" ${guestSession[0].value}`
    );
    assert.equal(
      guestSession[0].value,
      'off',
      `${locale}: with every guest locked, the guest session says it may change its rate`
    );
  }
});
