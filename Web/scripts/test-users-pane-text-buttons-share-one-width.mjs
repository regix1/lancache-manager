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
// in as the primary admin on the Users page with sign-in on and one tested sign-in connection. A
// user-role account is signed in and has one ended session, and a disabled account has no session.
// The test reads the width of every text button the detail panes draw in their header (Log out,
// Revoke, Delete, Enable) and in the Sign-in pane body (Change, Remove, Reset to default): one
// width on a wide screen, and on a phone, where the header's buttons fill its row, whole labels on
// a header that keeps its buttons on one row. Runs only with PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const ownSessionId = '4d6f8b0d-2e4a-4c6e-9a8c-1b3d5f7b9d1f';
const memberSessionId = 'b8d0f2b4-6c8e-4a0c-8e2a-4c6e8a0c2e4b';
const endedSessionId = 'e5a7c9e1-3b5d-4f7b-9d1f-5b7d9f1b3d5e';
const adminName = 'width-admin';
const memberName = 'width-member';
const disabledName = 'width-disabled';
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
      // A tested connection, so the Sign-in pane draws its Remove button.
      loginServices: [
        { id: 'login-github', kind: 'github', displayName: 'GitHub', environmentFields: [] }
      ],
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
      account({ id: 'account-disabled', username: disabledName, isDisabled: true })
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

/**
 * Runs in the page. How many rows the shown buttons of the detail pane header take, counting two
 * buttons whose tops are within 1px as one row; 0 when the header shows no button.
 */
const headerRows = () => {
  const tops = [
    ...globalThis.document.querySelectorAll('.users-detail-pane .users-pane-head button')
  ]
    .filter((button) => button.getClientRects().length > 0)
    .map((button) => button.getBoundingClientRect().top)
    .sort((a, b) => a - b);
  return tops.filter((top, index) => index === 0 || top - tops[index - 1] > 1).length;
};

// Each pane, the list row that opens it, and the list filter that shows that row.
const panes = (translate) => [
  { pane: 'own session', title: adminName, filterKey: 'user.panes.list.filters.signedIn' },
  {
    pane: `${memberName} session`,
    title: memberName,
    filterKey: 'user.panes.list.filters.signedIn'
  },
  { pane: 'ended session', title: memberName, filterKey: 'user.panes.list.filters.ended' },
  { pane: 'disabled account', title: disabledName, filterKey: 'activeSessions.accountFilter' },
  { pane: 'Sign-in', title: translate('user.panes.list.signIn'), filterKey: null }
];

test(
  'every text button in the Users detail panes, header and body, has one width on a wide screen',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const empty = [];
      const cut = [];
      const uneven = [];
      const wrapped = [];
      for (const language of ['en', 'zh']) {
        const translate = translator(language);
        const labels = [
          translate('activeSessions.actions.logout'),
          translate('activeSessions.actions.revoke'),
          translate('activeSessions.actions.delete'),
          translate('user.accounts.actions.enable'),
          translate('user.accounts.actions.disable'),
          translate('accessSetup.change'),
          translate('accessSetup.remove'),
          translate('management.grafana.metricsToggle.resetToDefault')
        ];
        for (const width of [1440, 390]) {
          const { context, page } = await openUsers(browser, width, language);
          try {
            const measured = [];
            for (const { pane, title, filterKey } of panes(translate)) {
              await openPane(page, translate, title, filterKey);
              const buttons = await page.evaluate(measureButtons, labels);
              if (buttons.length === 0) empty.push(`${language} at ${width} wide, ${pane}`);
              measured.push(...buttons.map((button) => ({ pane, ...button })));
              const rows = await page.evaluate(headerRows);
              if (rows > 1) {
                wrapped.push(
                  `${language} at ${width} wide, ${pane}: header buttons on ${rows} rows`
                );
              }
            }
            const where = `${language} at ${width} wide`;
            for (const button of measured) {
              if (button.cut) cut.push(`${where}, ${button.pane}: "${button.label}"`);
            }
            // On a phone the header's buttons fill its row, so one width is asked only when wide.
            if (
              width === 1440 &&
              measured.some((button) => Math.abs(button.width - measured[0].width) >= 0.5)
            ) {
              uneven.push(
                `${where}: ` +
                  measured
                    .map((button) => `${button.pane} "${button.label}" ${button.width}px`)
                    .join(', ')
              );
            }
          } finally {
            await context.close();
          }
        }
      }
      assert.deepEqual(empty, [], 'these panes draw none of the text buttons measured');
      assert.deepEqual(cut, [], 'these pane text buttons do not show their whole label');
      assert.deepEqual(
        wrapped,
        [],
        'these pane headers break their buttons over more than one row'
      );
      assert.deepEqual(uneven, [], 'at 1440 wide the Users pane text buttons differ in width');
    })
);
