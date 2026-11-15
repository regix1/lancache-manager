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
// in as the primary admin on the Users page with sign-in on. The admin and a user-role account are
// each signed in on two devices, a guest is signed in, and the user-role account has one ended
// session. One test reads the width of every Change and Reset button the Sign-in and Guest access
// panes draw, the other reads where each session pane puts Other devices against its preferences.
// Runs only with PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const ownSessionId = '5b0d7c2e-3f41-4a8e-9c6d-1e2f3a4b5c6d';
const ownOtherSessionId = '8e1f2a3b-4c5d-4e6f-8a7b-9c0d1e2f3a4b';
const guestSessionId = 'a7c3e9f1-2b4d-4f6a-8c0e-2d4f6a8c0e1b';
const memberSessionId = 'c2e4a6b8-0d1f-4e3a-9b5c-7d9f1b3d5f7a';
const memberOtherSessionId = 'f1a3c5e7-9b0d-4c2e-8f4a-6b8d0f2a4c6e';
const endedSessionId = '3d5f7b9a-1c2e-4d4f-a6b8-0c2e4a6c8e0d';
const adminName = 'panes-admin';
const memberName = 'panes-member';
const chromeOnWindows =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
  'Chrome/131.0.0.0 Safari/537.36';
const firefoxOnLinux = 'Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0';

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
          id: ownOtherSessionId,
          sessionType: 'admin',
          username: adminName,
          accountId: 'account-admin',
          ipAddress: '192.168.1.21',
          userAgent: firefoxOnLinux
        }),
        session({
          id: guestSessionId,
          sessionType: 'guest',
          username: null,
          ipAddress: '192.168.1.102',
          expiresAt: at(3600 * 5)
        }),
        session({
          id: memberSessionId,
          sessionType: 'user',
          username: memberName,
          accountId: 'account-member',
          ipAddress: '192.168.1.51'
        }),
        session({
          id: memberOtherSessionId,
          sessionType: 'user',
          username: memberName,
          accountId: 'account-member',
          ipAddress: '192.168.1.52',
          userAgent: firefoxOnLinux
        })
      ],
      pagination: { totalPages: 1, totalCount: 5, page: 1 },
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
      [ownSessionId, ownOtherSessionId, guestSessionId, memberSessionId, memberOtherSessionId].map(
        (id) => [`/api/user-preferences/session/${id}`, preferences]
      )
    ),
    '/api/accounts': [
      account({
        id: 'account-admin',
        username: adminName,
        role: 'admin',
        isMainAdmin: true,
        createdAtUtc: at(-86400 * 120)
      }),
      account({ id: 'account-member', username: memberName })
    ],
    // Saved from the app, so the Metrics access row offers Reset to default as a live button.
    '/api/metrics/security': {
      requiresAuthentication: true,
      source: 'ui',
      canToggle: true,
      envVarValue: false
    },
    '/api/auth/guest/config': { durationHours: 6, isLocked: false },
    // Chosen in the app, so the Guest access session length row draws its Reset button.
    '/api/auth/guest/config/duration': {
      durationHours: 4,
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
const openPane = async (page, translate, title, filterKey = null) => {
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
 * Runs in the page. Every shown detail pane button whose label is one of `labels`: its label, its
 * width, and whether its label is cut off or broken over two lines.
 */
const measureButtons = (labels) =>
  [...globalThis.document.querySelectorAll('.users-detail-pane button')]
    .filter(
      (button) => button.getClientRects().length > 0 && labels.includes(button.textContent.trim())
    )
    .map((button) => {
      const range = globalThis.document.createRange();
      range.selectNodeContents(button);
      const lines = new Set(
        [...range.getClientRects()]
          .filter((rect) => rect.width > 0)
          .map((rect) => Math.round(rect.top))
      );
      return {
        label: button.textContent.trim(),
        width: button.getBoundingClientRect().width,
        cut: button.scrollWidth > button.clientWidth + 0.5 || lines.size > 1
      };
    });

test(
  'every Change and Reset button in one Users settings pane is as wide as the widest of them',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const uneven = [];
      const cut = [];
      let pairs = 0;
      for (const language of ['en', 'zh']) {
        const translate = translator(language);
        const labels = [
          translate('accessSetup.change'),
          translate('management.grafana.metricsToggle.resetToDefault'),
          translate('common.reset')
        ];
        for (const width of [1440, 390]) {
          const { context, page } = await openUsers(browser, width, language);
          try {
            for (const [pane, titleKey] of [
              ['Sign-in', 'user.panes.list.signIn'],
              ['Guest access', 'activeSessions.access.title']
            ]) {
              await openPane(page, translate, translate(titleKey));
              const buttons = await page.evaluate(measureButtons, labels);
              const where = `${language} at ${width} wide, ${pane} pane`;
              if (buttons.length > 1) pairs += 1;
              for (const button of buttons) {
                if (button.cut) cut.push(`${where}: "${button.label}" is cut off or wraps`);
              }
              if (buttons.some((button) => Math.abs(button.width - buttons[0].width) >= 0.5)) {
                uneven.push(
                  `${where}: ` +
                    buttons.map((button) => `"${button.label}" ${button.width}px`).join(' vs ')
                );
              }
            }
          } finally {
            await context.close();
          }
        }
      }
      assert.ok(pairs > 0, 'a Users settings pane draws a Change and a Reset button together');
      assert.deepEqual(cut, [], 'these Change and Reset buttons do not show their whole label');
      assert.deepEqual(uneven, [], 'these Change and Reset buttons in one pane differ in width');
    })
);

/**
 * Runs in the page. The section headings of the detail pane, top to bottom in document order.
 */
const paneHeadings = () =>
  [...globalThis.document.querySelectorAll('.users-detail-pane h3')]
    .filter((heading) => heading.getClientRects().length > 0)
    .map((heading) => heading.textContent.replace(/\s+/g, ' ').trim());

test(
  'every Users pane that shows both keeps its device first, then its preferences, then Other devices last',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const translate = translator('en');
      const preferencesHeading = translate('user.panes.sections.prefs');
      const otherDevicesHeading = translate('user.panes.sections.otherDevices');
      const deviceHeadings = [
        translate('user.panes.sections.thisDevice'),
        translate('user.panes.sections.device')
      ];
      const panes = [
        { pane: 'own session', title: adminName, filterKey: 'user.panes.list.filters.signedIn' },
        {
          pane: 'guest session',
          title: translate('activeSessions.filters.guest'),
          filterKey: 'user.panes.list.filters.guests'
        },
        {
          pane: `${memberName} session`,
          title: memberName,
          filterKey: 'user.panes.list.filters.signedIn'
        },
        {
          pane: `${memberName} account`,
          title: memberName,
          filterKey: 'activeSessions.accountFilter'
        },
        { pane: 'ended session', title: memberName, filterKey: 'user.panes.list.filters.ended' }
      ];
      const { context, page } = await openUsers(browser, 1440, 'en');
      try {
        const both = [];
        const wrong = [];
        for (const { pane, title, filterKey } of panes) {
          await openPane(page, translate, title, filterKey);
          const headings = await page.evaluate(paneHeadings);
          const preferencesAt = headings.indexOf(preferencesHeading);
          const otherDevicesAt = headings.indexOf(otherDevicesHeading);
          if (preferencesAt < 0 || otherDevicesAt < 0) continue;
          both.push(pane);
          if (
            !deviceHeadings.includes(headings[0]) ||
            otherDevicesAt < preferencesAt ||
            otherDevicesAt !== headings.length - 1
          ) {
            wrong.push(`${pane}: ${headings.map((heading) => `"${heading}"`).join(', ')}`);
          }
        }
        assert.ok(
          both.length > 0,
          `a Users pane shows both "${preferencesHeading}" and "${otherDevicesHeading}"`
        );
        assert.deepEqual(
          wrong,
          [],
          `these panes do not read device first, "${preferencesHeading}" next and ` +
            `"${otherDevicesHeading}" last, headings top to bottom`
        );
      } finally {
        await context.close();
      }
    })
);
