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
// in as the primary admin on the Users page with sign-in on, metrics security saved from the app
// (so the Sign-in pane draws the Metrics access row with a live Reset to default), a user-role
// account with one live and one ended session, and a second admin session. The first test reads
// the size of every switch and the touch area of every button in the settings panes; the second
// reads every fact line of the panes and list rows in Chinese. Runs only with PLAYWRIGHT_PATH set
// to a playwright-core install whose Chromium is downloaded.

const ownSessionId = '7a1c3e5b-9d2f-4b6a-8c0e-2f4a6c8e0a1b';
const memberSessionId = 'c3e5a7c9-1b3d-4f5a-9b1d-3f5b7d9f1b2c';
const endedSessionId = 'f6b8d0f2-4c6e-4a8b-8d0f-6b8d0f2b4c3d';
const adminName = 'joins-admin';
const memberName = 'joins-member';
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

const answers = () => {
  const now = Date.now();
  const at = (seconds) => new Date(now + seconds * 1000).toISOString();
  const session = (fields) => ({
    accountDeleted: false,
    ipAddress: '192.168.1.20',
    userAgent: chromeOnWindows,
    browserLanguage: 'zh-CN',
    timezone: 'Asia/Shanghai',
    screenResolution: '1920x1080',
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
  const account = (fields) => ({
    role: 'user',
    isMainAdmin: false,
    isDisabled: false,
    createdAtUtc: at(-86400 * 30),
    lastLoginAtUtc: at(-600),
    ...fields
  });
  return {
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
      sessions: [
        session({
          id: ownSessionId,
          sessionType: 'admin',
          username: adminName,
          accountId: 'account-admin',
          isCurrentSession: true
        }),
        session({
          id: memberSessionId,
          sessionType: 'user',
          username: memberName,
          accountId: 'account-member',
          ipAddress: '192.168.1.51'
        })
      ],
      pagination: { totalPages: 1, totalCount: 2, page: 1 },
      historySessions: [
        session({
          id: endedSessionId,
          sessionType: 'user',
          username: memberName,
          accountId: 'account-member',
          ipAddress: '192.168.1.60',
          lastSeenAt: at(-86400),
          isRevoked: true,
          revokedAt: at(-3600)
        })
      ]
    },
    ...Object.fromEntries(
      [ownSessionId, memberSessionId].map((id) => [
        `/api/user-preferences/session/${id}`,
        preferences
      ])
    ),
    '/api/accounts': [
      account({
        id: 'account-admin',
        username: adminName,
        role: 'admin',
        isMainAdmin: true,
        createdAtUtc: at(-86400 * 120)
      }),
      account({ id: 'account-member', username: memberName }),
      account({ id: 'account-disabled', username: 'joins-disabled', isDisabled: true })
    ],
    // Saved from the app, so the Metrics access row offers Reset to default as a live button.
    '/api/metrics/security': {
      requiresAuthentication: true,
      source: 'ui',
      canToggle: true,
      envVarValue: false
    },
    '/api/auth/guest/config': { durationHours: 6, isLocked: false },
    '/api/auth/guest/config/duration': {
      durationHours: 6,
      source: 'ui',
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
  };
};

/** Opens the Users page in `language` at `width` by 900 (390 by 844 on a phone). */
const openUsers = async (browser, width, language) => {
  const context = await browser.newContext({
    viewport: { width, height: width < 640 ? 844 : 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript((chosen) => {
    globalThis.localStorage.setItem('lancache_language', chosen);
  }, language);
  const page = await context.newPage();
  await page.route('**/*', server(answers()));
  await fakeHub(page);
  await page.goto(`${builtAppOrigin}/`);
  // The app listens for its tab links once it has mounted, so the link is sent until the page shows.
  await page.waitForFunction(
    () => {
      globalThis.dispatchEvent(
        new globalThis.CustomEvent('navigate-to-tab', { detail: { tab: 'users' } })
      );
      return globalThis.document.querySelector('.users-split ul.users-rows--people > li') !== null;
    },
    undefined,
    { polling: 250 }
  );
  await page.evaluate(settle);
  return { context, page };
};

/**
 * Picks the list row whose title is `title`, after choosing the list filter labeled `filterKey`
 * when one is given; on a phone it first goes back from the pane that covers the list.
 */
const openPane = async (page, translate, title, filterKey) => {
  const back = page.getByRole('button', { name: translate('user.panes.list.back'), exact: true });
  if (await back.isVisible()) {
    await back.click();
    await page.evaluate(settle);
  }
  if (filterKey !== null) {
    await page
      .locator('.users-split .segmented-control-button', { hasText: translate(filterKey) })
      .first()
      .click();
    await page.evaluate(settle);
  }
  await page
    .locator('.users-split li > button', { has: page.getByText(title, { exact: true }) })
    .first()
    .click();
  await page.evaluate(settle);
};

/**
 * Runs in the page. Every shown switch and button in the detail pane body: whether it is a
 * switch, its label, its height, and the smaller side of the area a tap lands on, which a
 * positioned `::before` or `::after` overlay may grow past the button's own box.
 */
const measureControls = () =>
  [...globalThis.document.querySelectorAll('.users-detail-pane .users-pane-body button')]
    .filter((button) => button.getClientRects().length > 0)
    .map((button) => {
      const box = button.getBoundingClientRect();
      const overlays = ['::before', '::after']
        .map((part) => globalThis.getComputedStyle(button, part))
        .filter((style) => style.content !== 'none' && style.position === 'absolute')
        .map((style) => Math.min(parseFloat(style.width), parseFloat(style.height)))
        .filter((side) => Number.isFinite(side));
      return {
        isSwitch: button.matches('button.rounded-full.w-fit'),
        label: (button.textContent ?? '').trim() || (button.getAttribute('aria-label') ?? ''),
        height: box.height,
        touch: Math.max(Math.min(box.width, box.height), ...overlays)
      };
    });

// Each settings pane, the list row that opens it, and the list filter that shows that row.
const settingsPanes = (translate) => [
  { pane: 'Sign-in', title: translate('user.panes.list.signIn'), filterKey: null },
  { pane: 'Guest access', title: translate('activeSessions.access.title'), filterKey: null },
  { pane: 'own session', title: adminName, filterKey: 'user.panes.list.filters.signedIn' }
];

test(
  'every switch in the Users settings panes is 40px tall when wide and every pane control has a 44px touch area on a phone',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      let switches = 0;
      const short = [];
      const small = [];
      for (const language of ['en', 'zh']) {
        const translate = translator(language);
        for (const width of [1440, 390]) {
          const { context, page } = await openUsers(browser, width, language);
          try {
            for (const { pane, title, filterKey } of settingsPanes(translate)) {
              await openPane(page, translate, title, filterKey);
              const where = `${language} at ${width} wide, ${pane}`;
              for (const control of await page.evaluate(measureControls)) {
                if (control.isSwitch) switches += 1;
                if (width === 1440 && control.isSwitch && Math.abs(control.height - 40) >= 0.5) {
                  short.push(`${where}: switch "${control.label}" ${control.height}px tall`);
                }
                if (width === 390 && control.touch < 44) {
                  small.push(`${where}: "${control.label}" touch area ${control.touch}px`);
                }
              }
            }
          } finally {
            await context.close();
          }
        }
      }
      assert.ok(switches > 0, 'the Users settings panes drew no switch');
      assert.deepEqual(
        { short, small },
        { short: [], small: [] },
        'at 1440 wide these setting-row switches are not 40px tall (short), and at 390 wide these pane controls have no 44px touch area (small)'
      );
    })
);

/**
 * Runs in the page. The text of every fact line the detail pane and the list rows show: the pane
 * header's lines, the pane body's fact lines, and each list row's line.
 */
const factLines = () =>
  [
    ...globalThis.document.querySelectorAll(
      '.users-detail-pane .users-pane-head__line, .users-detail-pane .users-fact, .users-split .users-row__line'
    )
  ]
    .filter((line) => line.getClientRects().length > 0)
    .map((line) => (line.textContent ?? '').trim());

// Each pane whose facts are read, the list row that opens it, and the filter that shows the row.
const factPanes = [
  { pane: 'own session', title: adminName, filterKey: 'user.panes.list.filters.signedIn' },
  {
    pane: `${memberName} session`,
    title: memberName,
    filterKey: 'user.panes.list.filters.signedIn'
  },
  { pane: 'ended session', title: memberName, filterKey: 'user.panes.list.filters.ended' },
  { pane: `${memberName} account`, title: memberName, filterKey: 'activeSessions.accountFilter' }
];

test(
  'the Chinese Users panes and list rows join their facts with a Chinese comma',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const translate = translator('zh');
      const { context, page } = await openUsers(browser, 1440, 'zh');
      const read = [];
      const english = [];
      try {
        for (const { pane, title, filterKey } of factPanes) {
          await openPane(page, translate, title, filterKey);
          for (const line of await page.evaluate(factLines)) {
            read.push(line);
            if (line.includes(',')) english.push(`${pane}: "${line}"`);
          }
        }
      } finally {
        await context.close();
      }
      assert.ok(read.length > 0, 'the Chinese Users page showed no fact line');
      assert.deepEqual(
        [...new Set(english)],
        [],
        'these Chinese Users fact lines join their facts with an English comma'
      );
    })
);
