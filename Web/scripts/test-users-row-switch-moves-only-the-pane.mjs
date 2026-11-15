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
// in as the primary admin on the Users page with sign-in on, and every session's preferences
// answered 400 ms late, as a real server over a network does. One test picks one list row after
// another on a wide screen and reads, on every frame for one second after each click, where the
// list's search box, filters, rows and Create account sit; the other opens a row low in a long
// phone list and goes back. Runs only with PLAYWRIGHT_PATH set to a playwright-core install whose
// Chromium is downloaded.

const ownSessionId = '5b0d7c2e-3f41-4a8e-9c6d-1e2f3a4b5c6d';
const guestSessionId = 'a7c3e9f1-2b4d-4f6a-8c0e-2d4f6a8c0e1b';
const memberSessionId = 'c2e4a6b8-0d1f-4e3a-9b5c-7d9f1b3d5f7a';
const endedSessionId = '3d5f7b9a-1c2e-4d4f-a6b8-0c2e4a6c8e0d';
const neverSignedInId = 'account-never';
const adminName = 'switch-admin';
const memberName = 'switch-member';
const neverSignedInName = 'switch-new';
const chromeOnWindows =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
  'Chrome/131.0.0.0 Safari/537.36';
// Enough guests that the phone list runs past one screen.
const extraGuestIds = Array.from(
  { length: 12 },
  (_, index) => `e0a1b2c3-d4e5-4f60-8a1b-${String(index).padStart(12, '0')}`
);

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
  const guest = (id, address) =>
    session({ id, sessionType: 'guest', username: null, ipAddress: address, expiresAt: at(18000) });
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
        guest(guestSessionId, '192.168.1.102'),
        session({
          id: memberSessionId,
          sessionType: 'user',
          username: memberName,
          accountId: 'account-member',
          ipAddress: '192.168.1.51'
        }),
        ...extraGuestIds.map((id, index) => guest(id, `192.168.2.${index + 10}`))
      ],
      pagination: { totalPages: 1, totalCount: 3 + extraGuestIds.length, page: 1 },
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
    '/api/accounts': [
      account({
        id: 'account-admin',
        username: adminName,
        role: 'admin',
        isMainAdmin: true,
        createdAtUtc: at(-86400 * 120)
      }),
      account({ id: 'account-member', username: memberName }),
      account({ id: neverSignedInId, username: neverSignedInName, lastLoginAtUtc: null })
    ],
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

/** Opens the Users page in English at `width` by 900 (390 by 844 on a phone). */
const openUsers = async (browser, width) => {
  const context = await browser.newContext({
    viewport: { width, height: width < 640 ? 844 : 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript(() => {
    globalThis.localStorage.setItem('lancache_language', 'en');
  });
  const page = await context.newPage();
  await page.route('**/*', server(answers()));
  // Registered last, so it is the route a session's preferences read takes.
  await page.route('**/api/user-preferences/session/*', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 400));
    await route.fulfill({ json: preferences });
  });
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

/** Chooses the list filter labeled `label`. */
const chooseFilter = async (page, label) => {
  await page.locator('.users-split .segmented-control-button', { hasText: label }).first().click();
  await page.evaluate(settle);
};

/**
 * Runs in the page. Clicks the `index`-th list row titled `title`, then on every frame for one
 * second reads the box of the list's search box, filter track, each row and Create account, and
 * returns each one that left the place it had before the click, or vanished, with when it did.
 */
const clickAndWatch = async ({ title, index }) => {
  const document = globalThis.document;
  const watched = () => {
    const pane = document.querySelector('.users-list-pane');
    const named = [
      ['search box', pane.querySelector('.users-tools input')],
      ['filter track', pane.querySelector('.users-tools .segmented-control-button')?.parentElement],
      ['Create account', pane.querySelector('.users-list-foot button')],
      ...[...pane.querySelectorAll('li > button')].map((row, at) => [
        `row ${at + 1} "${row.querySelector('.users-row__body span span')?.textContent ?? ''}"`,
        row
      ])
    ];
    return new Map(
      named
        .filter(([, element]) => element != null)
        .map(([name, element]) => {
          const box = element.getBoundingClientRect();
          return [
            name,
            `${box.x.toFixed(1)},${box.y.toFixed(1)},${box.width.toFixed(1)}x${box.height.toFixed(1)}`
          ];
        })
    );
  };
  const target = [...document.querySelectorAll('.users-list-pane li > button')].filter(
    (row) => row.querySelector('.users-row__body span span')?.textContent === title
  )[index];
  if (target === undefined) return [`no row titled "${title}"`];
  const before = watched();
  const started = performance.now();
  target.click();
  const moved = new Map();
  while (performance.now() - started < 1000) {
    await new Promise((resolve) => globalThis.requestAnimationFrame(resolve));
    const now = watched();
    const when = Math.round(performance.now() - started);
    for (const [name, box] of before) {
      if (moved.has(name)) continue;
      const after = now.get(name);
      if (after !== box) moved.set(name, `${name} ${box} -> ${after ?? 'gone'} at ${when} ms`);
    }
  }
  return [...moved.values()];
};

test(
  'picking another Users row moves nothing outside the detail pane',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const translate = translator('en');
      const guestTitle = translate('activeSessions.filters.guest');
      const accessTitle = translate('activeSessions.access.title');
      const signInTitle = translate('user.panes.list.signIn');
      const steps = [
        { filterKey: 'user.panes.list.filters.signedIn', title: adminName, index: 0 },
        { title: memberName, index: 0 },
        { title: guestTitle, index: 0 },
        { title: guestTitle, index: 1 },
        { title: accessTitle, index: 0 },
        { title: memberName, index: 0 },
        { title: signInTitle, index: 0 },
        { title: adminName, index: 0 },
        { filterKey: 'activeSessions.accountFilter', title: memberName, index: 0 },
        { title: neverSignedInName, index: 0 },
        { title: adminName, index: 0 },
        { filterKey: 'user.panes.list.filters.ended', title: memberName, index: 0 },
        { title: accessTitle, index: 0 },
        { title: memberName, index: 0 }
      ];
      const { context, page } = await openUsers(browser, 1440);
      try {
        const moved = [];
        let previous = 'the first pane';
        for (const { filterKey, title, index } of steps) {
          if (filterKey !== undefined) {
            await chooseFilter(page, translate(filterKey));
            previous = `the pane open when ${translate(filterKey)} was chosen`;
          }
          const step = `from ${previous} to "${title}" #${index + 1}`;
          for (const line of await page.evaluate(clickAndWatch, { title, index })) {
            moved.push(`${step}: ${line}`);
          }
          await page.evaluate(settle);
          previous = `"${title}" #${index + 1}`;
        }
        assert.deepEqual(moved, [], 'these list parts moved while only the detail pane changed');
      } finally {
        await context.close();
      }
    })
);

test(
  'a phone that opens a Users row and goes back finds the list where it left it',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const translate = translator('en');
      const { context, page } = await openUsers(browser, 390);
      try {
        const guestTitle = translate('activeSessions.filters.guest');
        const rows = page.locator('.users-list-pane li > button', {
          has: page.getByText(guestTitle, { exact: true })
        });
        const low = rows.nth(9);
        await low.scrollIntoViewIfNeeded();
        await page.evaluate(settle);
        const before = await low.boundingBox();
        const scrolledBefore = await page.evaluate(() => globalThis.scrollY);
        assert.ok(scrolledBefore > 0, 'the phone list runs past one screen, so it is scrolled');
        await low.click();
        await page.evaluate(settle);
        const back = page.getByRole('button', {
          name: translate('user.panes.list.back'),
          exact: true
        });
        const backBox = await back.boundingBox();
        assert.ok(
          backBox !== null && backBox.y >= 0 && backBox.y + backBox.height <= 844,
          `the opened pane starts on screen with its Back button, not at ${JSON.stringify(backBox)}`
        );
        await back.click();
        await page.evaluate(settle);
        const after = await low.boundingBox();
        assert.deepEqual(
          after,
          before,
          'the row the phone opened is not where it was before the pane covered the list'
        );
      } finally {
        await context.close();
      }
    })
);
