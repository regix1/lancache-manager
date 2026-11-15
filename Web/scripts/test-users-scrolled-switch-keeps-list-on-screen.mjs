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

// Opens the built app (run `npm run build` first) in Chromium at 1440 by 900 with every API answer
// faked, signed in as the primary admin on the Users page with two guests signed in. It opens a
// guest's pane, which is taller than the screen, scrolls the page to its end, and switches to the
// short Sign-in pane, then scrolls down again and switches back. It watches where the list, its
// rows, the tools row and Create account sit on screen. Runs only with PLAYWRIGHT_PATH set to a
// playwright-core install whose Chromium is downloaded.

const translate = translator('en');
const ownSessionId = '8f0b2d4f-6c8e-4a0b-9d2f-4b6d8f0c2e08';
const guestSessionIds = [
  '9a1c3e5a-7d9f-4b1c-8e3a-5c7e9a1d3f09',
  'ab2d4f6b-8e0a-4c2d-9f4b-6d8f0b2e4a10'
];
const adminName = 'scroll-admin';
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
        )
      ],
      pagination: { totalPages: 1, totalCount: 1 + guestSessionIds.length, page: 1 },
      historySessions: []
    },
    ...Object.fromEntries(
      [ownSessionId, ...guestSessionIds].map((id) => [
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
      }
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

/** Opens the Users page at 1440 by 900 and waits for its list. */
const openUsers = async (browser) => {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
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

/** The list row button titled `title`, the `nth` of those rows, in either list of the Users page. */
const rowButton = (page, title, nth) =>
  page
    .locator('.users-split ul.users-rows li > button', {
      has: page.getByText(title, { exact: true })
    })
    .nth(nth);

/**
 * Runs in the page. Scrolls whatever scrolls the Users page as far down as it goes and returns how
 * far that is, with the detail pane's height.
 */
const scrollToEnd = () => {
  const card = globalThis.document.querySelector('.users-card');
  let scroller = card?.parentElement ?? null;
  while (scroller !== null) {
    const style = globalThis.getComputedStyle(scroller);
    if (/(auto|scroll)/.test(style.overflowY) && scroller.scrollHeight > scroller.clientHeight) {
      break;
    }
    scroller = scroller.parentElement;
  }
  const target = scroller ?? globalThis.document.scrollingElement;
  target.scrollTop = target.scrollHeight;
  return {
    scrolled: Math.round(target.scrollTop),
    pane: Math.round(
      globalThis.document.querySelector('.users-detail-pane').getBoundingClientRect().height
    )
  };
};

/**
 * Runs in the page. Sets up `globalThis.screenWatch`: `boxes()` reads where on screen each shown
 * element outside the detail pane sits (the tools row, the list, each list row, Create account);
 * `start()` reads them on every frame until `stop()`, each read stamped with its time since start.
 */
const installWatch = () => {
  const boxes = () => {
    const read = {};
    const add = (key, element) => {
      if (element === null || element.getClientRects().length === 0) return;
      const rect = element.getBoundingClientRect();
      read[key] = [rect.left, rect.top, rect.width, rect.height].map(Math.round).join(',');
    };
    const document = globalThis.document;
    add('tools row', document.querySelector('.users-split .users-tools'));
    add('list', document.querySelector('.users-split .users-list-scroll'));
    add('Create account', document.querySelector('.users-split .users-list-foot button'));
    document.querySelectorAll('.users-split .users-rows li > button').forEach((row, index) => {
      add(`row ${index + 1} "${(row.textContent ?? '').trim().slice(0, 40)}"`, row);
    });
    return read;
  };
  const watch = { boxes, samples: [], running: false };
  watch.start = () => {
    watch.samples = [];
    watch.running = true;
    const began = globalThis.performance.now();
    const step = () => {
      if (!watch.running) return;
      watch.samples.push({ at: Math.round(globalThis.performance.now() - began), read: boxes() });
      globalThis.requestAnimationFrame(step);
    };
    step();
  };
  watch.stop = () => {
    watch.running = false;
    return watch.samples;
  };
  globalThis.screenWatch = watch;
};

/** Waits until the page's watch has read for `ms` milliseconds since it started. */
const watchedFor = (page, ms) =>
  page.waitForFunction((until) => (globalThis.screenWatch.samples.at(-1)?.at ?? 0) >= until, ms, {
    polling: 'raf'
  });

/** Every on-screen box of `before` that a later read lost or moved, once per element. */
const changesFrom = (where, before, samples) => {
  const changes = [];
  const reported = new Set();
  for (const sample of samples) {
    for (const [key, was] of Object.entries(before)) {
      const now = sample.read[key] ?? 'gone';
      if (now !== was && !reported.has(key)) {
        reported.add(key);
        changes.push(`${where}: ${key} ${was} became ${now} at ${sample.at}ms`);
      }
    }
  }
  return changes;
};

test(
  'with the Users page scrolled down, switching between a tall and a short pane keeps the list where it is on screen',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const guestTitle = translate('activeSessions.filters.guest');
      const signInTitle = translate('user.panes.list.signIn');
      // Each switch: the row picked first and the row switched to, as a title and its place.
      const legs = [
        {
          name: 'tall guest pane to short Sign-in pane',
          from: [guestTitle, 0],
          to: [signInTitle, 0]
        },
        {
          name: 'short Sign-in pane to tall guest pane',
          from: [signInTitle, 0],
          to: [guestTitle, 1]
        }
      ];
      const { context, page } = await openUsers(browser);
      const changes = [];
      const scrolls = [];
      let watched = 0;
      try {
        await page.evaluate(installWatch);
        for (const { name, from, to } of legs) {
          await rowButton(page, ...from).click();
          await page.evaluate(settle);
          const scroll = await page.evaluate(scrollToEnd);
          await page.evaluate(settle);
          scrolls.push(`${name}: scrolled ${scroll.scrolled}px with a ${scroll.pane}px pane`);
          const before = await page.evaluate(() => globalThis.screenWatch.boxes());
          watched += Object.keys(before).length;
          await page.evaluate(() => globalThis.screenWatch.start());
          await rowButton(page, ...to).click();
          await watchedFor(page, 1000);
          const samples = await page.evaluate(() => globalThis.screenWatch.stop());
          changes.push(...changesFrom(`${name}, ${scroll.scrolled}px down`, before, samples));
        }
      } finally {
        await context.close();
      }
      assert.ok(watched > 0, 'no element outside the detail pane was found to watch');
      assert.ok(
        scrolls.some((line) => !line.includes('scrolled 0px')),
        `the page never scrolled, so the case is not set up: ${scrolls.join('; ')}`
      );
      assert.deepEqual(
        changes,
        [],
        'with the page scrolled down, these elements outside the Users detail pane moved on screen or went away during a switch'
      );
    })
);
