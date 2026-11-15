import assert from 'node:assert/strict';
import test from 'node:test';
import {
  builtAppOrigin,
  fakeHub,
  server,
  settle,
  translator,
  withBrowser
} from './transpile-module.mjs';

// Opens the built app (run `npm run build` first) in Chromium with every API answer faked, signed
// in as the primary admin on the Users page with sign-in on, and opens the pane of an account that
// has never signed in. That account then signs in from another browser: the server answers the next
// session and account reads with its new session and the new session's preferences, and sends the
// live events of that sign-in. The test reads whether the open pane shows the new session's
// preferences with no reload and no second pick. Runs only with PLAYWRIGHT_PATH set to a
// playwright-core install whose Chromium is downloaded.

const translate = translator('en');
const ownSessionId = '6c8e0a2c-4e6a-4b8c-9d0e-2f4a6c8e0a2b';
const newSessionId = '9a1c3e5a-7b9d-4f1b-8c3d-5e7a9c1e3a5c';
const adminName = 'sign-in-admin';
const newName = 'sign-in-new';
const chromeOnWindows =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
  'Chrome/131.0.0.0 Safari/537.36';

const preferences = {
  selectedTheme: 'dark-default',
  sharpCorners: false,
  disableFocusOutlines: true,
  disableTooltips: false,
  picsAlwaysVisible: false,
  disableStickyNotifications: false,
  useLocalTimezone: false,
  useUtcTimezone: false,
  use24HourFormat: true,
  showDatasourceLabels: true
};

const at = (seconds) => new Date(Date.now() + seconds * 1000).toISOString();

const session = (fields) => ({
  accountDeleted: false,
  ipAddress: '192.168.1.20',
  userAgent: chromeOnWindows,
  createdAt: at(-86400),
  lastSeenAt: at(-10),
  expiresAt: at(86400 * 20),
  isExpired: false,
  isRevoked: false,
  revokedAt: null,
  isCurrentSession: false,
  isSharedSession: false,
  signedOut: false,
  prefillEnabled: false,
  steamPrefillEnabled: false,
  epicPrefillEnabled: false,
  battlenetPrefillEnabled: false,
  riotPrefillEnabled: false,
  xboxPrefillEnabled: false,
  ...fields
});

const ownSession = session({
  id: ownSessionId,
  sessionType: 'admin',
  username: adminName,
  accountId: 'account-admin',
  isCurrentSession: true
});

const adminAccount = {
  id: 'account-admin',
  username: adminName,
  role: 'admin',
  isMainAdmin: true,
  isDisabled: false,
  createdAtUtc: at(-86400 * 120),
  lastLoginAtUtc: at(-3600)
};

// Never signed in: the server leaves a null field out of its JSON, so the key is absent.
const newAccount = {
  id: 'account-new',
  username: newName,
  role: 'user',
  isMainAdmin: false,
  isDisabled: false,
  createdAtUtc: at(-86400 * 3)
};

/** What the server answers before the new account signs in. */
const answersBefore = () => ({
  '/api/auth/status': {
    isAuthenticated: true,
    authenticationEnabled: true,
    accountMode: 'password',
    authenticationSetupRequired: false,
    sessionType: 'admin',
    sessionId: ownSessionId,
    accountId: 'account-admin',
    isMainAdmin: true,
    expiresAt: at(86400 * 20),
    guestAccessEnabled: true,
    guestDurationHours: 6,
    loginServices: [],
    incompleteLoginServices: [],
    ownerLoginServices: [],
    hasData: true,
    hasBeenInitialized: true,
    hasDataLoaded: true
  },
  '/api/system/setup': {
    isCompleted: true,
    hasProcessedLogs: true,
    needsPostgresCredentials: false,
    accountExists: true,
    mainAdminRecoveryAvailable: false,
    currentSetupStep: null,
    dataSourceChoice: null,
    completedPlatforms: null,
    mode: 'embedded'
  },
  '/api/system/config': {
    cachePath: '/cache',
    logsPath: '/logs',
    dataPath: '/data',
    cacheDeleteMode: 'preserve',
    steamAuthMode: 'anonymous',
    timeZone: 'UTC',
    cacheWritable: true,
    logsWritable: true,
    dataSources: []
  },
  '/api/user-preferences': {
    ...preferences,
    selectedTheme: null,
    refreshRate: null,
    refreshRateLocked: null,
    allowedTimeFormats: null
  },
  '/api/sessions': {
    sessions: [ownSession],
    pagination: { totalPages: 1, totalCount: 1, page: 1 },
    historySessions: []
  },
  [`/api/user-preferences/session/${ownSessionId}`]: preferences,
  '/api/accounts': [adminAccount, newAccount],
  '/api/auth/guest/config': { durationHours: 6, isLocked: false },
  '/api/auth/guest/config/duration': {
    durationHours: 6,
    source: 'default',
    canEdit: true,
    envVarValue: 6
  },
  '/api/themes/preferences/guest': { themeId: 'dark-default' },
  '/api/themes': [],
  '/api/system/default-guest-refresh-rate': { refreshRate: 'STANDARD', locked: false },
  '/api/system/default-guest-preferences': {
    useLocalTimezone: true,
    useUtcTimezone: false,
    use24HourFormat: true,
    sharpCorners: false,
    disableTooltips: false,
    showDatasourceLabels: true,
    allowedTimeFormats: ['server-24h', 'server-12h', 'local-24h', 'local-12h', 'utc']
  },
  '/api/auth/guest/prefill/config': { enabledByDefault: false, durationHours: 2 },
  '/api/auth/guest/epic-prefill/config': { enabledByDefault: false, durationHours: 2 },
  '/api/auth/guest/battlenet-prefill/config': { enabledByDefault: false, durationHours: 2 },
  '/api/auth/guest/riot-prefill/config': { enabledByDefault: false, durationHours: 2 },
  '/api/auth/guest/xbox-prefill/config': { enabledByDefault: false, durationHours: 2 }
});

/**
 * Puts what the server stores when the new account signs in into `answers`: its session in the
 * session list, its sign-in time on its account, and the new session's preferences row.
 */
const signInNewAccount = (answers) => {
  answers['/api/sessions'] = {
    sessions: [
      ownSession,
      session({
        id: newSessionId,
        sessionType: 'user',
        username: newName,
        accountId: 'account-new',
        ipAddress: '192.168.1.77',
        createdAt: at(-5),
        lastSeenAt: at(-1)
      })
    ],
    pagination: { totalPages: 1, totalCount: 2, page: 1 },
    historySessions: []
  };
  answers['/api/accounts'] = [adminAccount, { ...newAccount, lastLoginAtUtc: at(-5) }];
  answers[`/api/user-preferences/session/${newSessionId}`] = preferences;
};

// The live events the server sends for the sign-in: the session it made (AuthController.cs:411)
// and the preferences the new browser stores (UserPreferencesController.cs:102).
const signInEvents = {
  sessionCreated: ['UserSessionCreated', { sessionId: newSessionId, sessionType: 'user' }],
  preferencesStored: ['UserPreferencesUpdated', { sessionId: newSessionId, preferences }]
};

/** Runs in the page. The section headings of the detail pane, top to bottom. */
const paneHeadings = () =>
  [...globalThis.document.querySelectorAll('.users-detail-pane h3')]
    .filter((heading) => heading.getClientRects().length > 0)
    .map((heading) => heading.textContent.replace(/\s+/g, ' ').trim());

test(
  "the open pane of an account that never signed in shows its new session's preferences once it signs in",
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const preferencesHeading = translate('user.panes.sections.prefs');
      const missing = [];
      // The two events reach the page in either order.
      for (const order of [
        ['sessionCreated', 'preferencesStored'],
        ['preferencesStored', 'sessionCreated']
      ]) {
        const context = await browser.newContext({
          viewport: { width: 1440, height: 900 },
          timezoneId: 'UTC',
          colorScheme: 'dark'
        });
        try {
          await context.addInitScript(() => {
            globalThis.localStorage.setItem('lancache_language', 'en');
          });
          const page = await context.newPage();
          const answers = answersBefore();
          await page.route('**/*', server(answers));
          const hub = await fakeHub(page);
          await page.goto(`${builtAppOrigin}/`);
          await page.waitForFunction(
            () => {
              globalThis.dispatchEvent(
                new globalThis.CustomEvent('navigate-to-tab', { detail: { tab: 'users' } })
              );
              return (
                globalThis.document.querySelector('.users-split ul.users-rows--people > li') !==
                null
              );
            },
            undefined,
            { polling: 250 }
          );
          await hub.handshakesReach(1);
          await page.evaluate(settle);

          await page
            .locator('.users-split .segmented-control-button', {
              hasText: translate('activeSessions.accountFilter')
            })
            .first()
            .click();
          await page.evaluate(settle);
          await page
            .locator('.users-split li > button', {
              has: page.getByText(newName, { exact: true })
            })
            .first()
            .click();
          await page.evaluate(settle);
          const before = await page.evaluate(paneHeadings);
          assert.ok(
            !before.includes(preferencesHeading),
            `the pane of ${newName}, which never signed in, shows no "${preferencesHeading}" ` +
              `before it signs in`
          );

          signInNewAccount(answers);
          const reread = page.waitForResponse(
            (response) => new URL(response.url()).pathname === '/api/sessions'
          );
          for (const step of order) hub.send(...signInEvents[step]);
          await reread;
          await page.evaluate(settle);
          // The pane may still be reading the new session's preferences; give it until they show.
          await page
            .waitForFunction(
              (heading) =>
                [...globalThis.document.querySelectorAll('.users-detail-pane h3')].some(
                  (element) => element.textContent.replace(/\s+/g, ' ').trim() === heading
                ),
              preferencesHeading,
              { polling: 'raf', timeout: 5000 }
            )
            .catch(() => null);
          const after = await page.evaluate(paneHeadings);
          if (!after.includes(preferencesHeading)) {
            missing.push(
              `${order.join(' then ')}: the pane shows the headings [` +
                `${after.map((heading) => `"${heading}"`).join(', ')}]`
            );
          }
        } finally {
          await context.close();
        }
      }
      assert.deepEqual(
        missing,
        [],
        `after ${newName} signs in elsewhere, its open pane shows no "${preferencesHeading}" ` +
          `until a reload or a second pick`
      );
    })
);
