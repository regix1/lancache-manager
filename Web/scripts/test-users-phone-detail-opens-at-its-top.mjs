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

// Opens the built app (run `npm run build` first) in Chromium at 390 by 844 with every API answer
// faked, signed in as the primary admin on the Users page with ten guests and four other accounts
// signed in, so the list is taller than the phone screen. For every list row it scrolls the list
// down until that row sits at the screen's foot, taps it, and reads where the detail's name and its
// Back to list button sit; then it goes back and reads where the list came back to. Runs only with
// PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const translate = translator('en');
const ownSessionId = 'c4e6a8c0-2f4b-4d6e-8a0c-4e6a8c0e2a11';
const guestSessionIds = Array.from(
  { length: 10 },
  (_, index) => `d5f7b9d1-3a5c-4e7f-9b1d-5f7b9d1f3b${String(index).padStart(2, '0')}`
);
const adminName = 'phone-admin';
// Other accounts, each signed in once with prefill off, as the list's last rows.
const members = Array.from({ length: 4 }, (_, index) => ({
  name: `phone-member-${index + 1}`,
  accountId: `account-member-${index + 1}`,
  sessionId: `e6a8c0e2-4b6d-4f8a-8c2e-6a8c0e2a4c${String(index).padStart(2, '0')}`,
  role: index % 2 === 0 ? 'admin' : 'user'
}));
const chromeOnWindows =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
  'Chrome/131.0.0.0 Safari/537.36';
const guestConfigPaths = [
  '/api/auth/guest/prefill/config',
  '/api/auth/guest/epic-prefill/config',
  '/api/auth/guest/battlenet-prefill/config',
  '/api/auth/guest/riot-prefill/config',
  '/api/auth/guest/xbox-prefill/config'
];

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
    prefillEnabled: true,
    steamPrefillEnabled: true,
    epicPrefillEnabled: true,
    battlenetPrefillEnabled: true,
    riotPrefillEnabled: true,
    xboxPrefillEnabled: true,
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
        ...guestSessionIds.map((id, index) =>
          session({
            id,
            sessionType: 'guest',
            username: null,
            accountId: null,
            ipAddress: `192.168.1.${80 + index}`,
            expiresAt: at(3600 * 6)
          })
        ),
        ...members.map((member, index) =>
          session({
            id: member.sessionId,
            sessionType: member.role,
            username: member.name,
            accountId: member.accountId,
            ipAddress: `192.168.1.${120 + index}`,
            lastSeenAt: at(-600 * (index + 1)),
            prefillEnabled: false,
            steamPrefillEnabled: false,
            epicPrefillEnabled: false,
            battlenetPrefillEnabled: false,
            riotPrefillEnabled: false,
            xboxPrefillEnabled: false
          })
        )
      ],
      pagination: {
        totalPages: 1,
        totalCount: 1 + guestSessionIds.length + members.length,
        page: 1
      },
      historySessions: []
    },
    ...Object.fromEntries(
      [ownSessionId, ...guestSessionIds, ...members.map((member) => member.sessionId)].map((id) => [
        `/api/user-preferences/session/${id}`,
        preferences
      ])
    ),
    '/api/accounts': [
      {
        id: 'account-admin',
        username: adminName,
        role: 'admin',
        isMainAdmin: true,
        isDisabled: false,
        createdAtUtc: at(-86400 * 120),
        lastLoginAtUtc: at(-600)
      },
      ...members.map((member, index) => ({
        id: member.accountId,
        username: member.name,
        role: member.role,
        isMainAdmin: false,
        isDisabled: false,
        createdAtUtc: at(-86400 * (30 + index)),
        lastLoginAtUtc: at(-600 * (index + 1))
      }))
    ],
    '/api/metrics/security': {
      requiresAuthentication: true,
      source: 'default',
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
    ...Object.fromEntries(
      guestConfigPaths.map((path) => [path, { enabledByDefault: true, durationHours: 2 }])
    )
  };
};

/** Opens the Users page at 390 by 844 and waits for its list. */
const openUsers = async (browser) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript(() => {
    globalThis.localStorage.setItem('lancache_language', 'en');
  });
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

const rowSelector = '.users-split .users-rows li > button';

/**
 * Runs in the page. Scrolls the page as far down as it goes while the list row at `index` still
 * shows wholly above the screen's foot, the way a person scrolls down to a row and taps it.
 */
const scrollToRow = ({ selector, index }) => {
  const target = globalThis.document.scrollingElement;
  const row = globalThis.document.querySelectorAll(selector)[index];
  const bottom = row.getBoundingClientRect().bottom;
  target.scrollTop = Math.max(
    0,
    Math.round(target.scrollTop + bottom - globalThis.innerHeight + 8)
  );
};

/** Runs in the page. How far the page is scrolled and where every list row sits. */
const readList = (selector) => ({
  scrolled: Math.round(globalThis.document.scrollingElement.scrollTop),
  rows: [...globalThis.document.querySelectorAll(selector)]
    .map((row) => Math.round(row.getBoundingClientRect().top))
    .join(',')
});

/**
 * Runs in the page. Where on screen the opened detail's name and its Back to list button sit, and
 * the part of the screen they can show in: below any bar pinned to the screen's top, above its foot.
 */
const detailTop = (backLabel) => {
  const document = globalThis.document;
  const read = (element) => {
    if (element === null || element.getClientRects().length === 0) return null;
    const rect = element.getBoundingClientRect();
    return { top: Math.round(rect.top), bottom: Math.round(rect.bottom) };
  };
  const pinnedBottom = [...document.querySelectorAll('body *')]
    .filter((element) => /^(sticky|fixed)$/.test(globalThis.getComputedStyle(element).position))
    .map((element) => element.getBoundingClientRect())
    .filter(
      (rect) => rect.height > 0 && rect.top <= 0.5 && rect.height < globalThis.innerHeight / 2
    )
    .reduce((lowest, rect) => Math.max(lowest, Math.round(rect.bottom)), 0);
  const back = [...document.querySelectorAll('.users-detail-pane button')].find(
    (button) => (button.textContent ?? '').trim() === backLabel
  );
  return {
    shownFrom: pinnedBottom,
    shownTo: globalThis.innerHeight,
    name: read(document.querySelector('.users-detail-pane .users-pane-head__title')),
    back: read(back ?? null)
  };
};

/** Every part of the opened detail's top that is not wholly on screen, as lines naming `where`. */
const hiddenParts = (where, read) =>
  Object.entries({ name: read.name, 'Back to list': read.back }).flatMap(([part, box]) =>
    box === null
      ? [`${where}: the ${part} is not drawn`]
      : box.top < read.shownFrom || box.bottom > read.shownTo
        ? [
            `${where}: the ${part} spans y=${box.top}..${box.bottom}, ` +
              `but the screen shows y=${read.shownFrom}..${read.shownTo}`
          ]
        : []
  );

test(
  'on a phone, a row tapped in a scrolled list opens its detail with the name and Back to list on screen',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const backLabel = translate('user.panes.list.back');
      const problems = [];
      const scrolls = [];
      const { context, page } = await openUsers(browser);
      try {
        const rowCount = await page.locator(rowSelector).count();
        for (let index = 0; index < rowCount; index += 1) {
          await page.evaluate(scrollToRow, { selector: rowSelector, index });
          await page.evaluate(settle);
          const before = await page.evaluate(readList, rowSelector);
          scrolls.push(before.scrolled);
          const title = (await page.locator(rowSelector).nth(index).innerText()).split('\n')[0];
          const where = `row ${index + 1} "${title}" tapped with the list scrolled ${before.scrolled}px`;
          await page.locator(rowSelector).nth(index).click();
          await page.evaluate(settle);
          problems.push(...hiddenParts(where, await page.evaluate(detailTop, backLabel)));

          // Back to list returns the list to where it was, with every row in place.
          await page.getByRole('button', { name: backLabel, exact: true }).click();
          await page.evaluate(settle);
          const after = await page.evaluate(readList, rowSelector);
          if (after.scrolled !== before.scrolled || after.rows !== before.rows) {
            problems.push(
              `${where}: Back to list returned to ${after.scrolled}px with rows at ${after.rows}, ` +
                `not ${before.scrolled}px with rows at ${before.rows}`
            );
          }
        }
      } finally {
        await context.close();
      }
      assert.ok(
        scrolls.some((scrolled) => scrolled > 0),
        `the list never scrolled, so the case is not set up: ${scrolls.join(', ')}px`
      );
      assert.deepEqual(
        problems,
        [],
        'on a phone, the detail opened from a scrolled Users list hid its name or Back to list above or below the screen'
      );
    })
);
