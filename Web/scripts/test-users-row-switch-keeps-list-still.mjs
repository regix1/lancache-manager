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
// in as the primary admin on the Users page with two user-role accounts and four guests signed in.
// The answers a newly picked pane asks for (its session's preferences and the five guest thread
// defaults) can be held back and let go by the test, the way a slow server answers them. The first
// test switches between rows and watches every box outside the detail pane; the second switches
// between guests ten times and watches the pane of the last pick. Runs only with PLAYWRIGHT_PATH set
// to a playwright-core install whose Chromium is downloaded.

const translate = translator('en');
const ownSessionId = '1e3a5c7e-9b1d-4f3a-8c5e-7a9c1e3b5d01';
const firstMemberSessionId = '2f4b6d8f-0c2e-4a4b-9d6f-8b0d2f4c6e02';
const secondMemberSessionId = '3a5c7e9a-1d3f-4b5c-8e7a-9c1e3a5d7f03';
const guestSessionIds = [
  '4b6d8f0b-2e4a-4c6d-9f8b-0d2f4b6e8a04',
  '5c7e9a1c-3f5b-4d7e-8a9c-1e3a5c7f9b05',
  '6d8f0b2d-4a6c-4e8f-9b0d-2f4b6d8a0c06',
  '7e9a1c3e-5b7d-4f9a-8c1e-3a5c7e9b1d07'
];
const adminName = 'still-admin';
const firstMemberName = 'still-member-one';
const secondMemberName = 'still-member-two';
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
const sessionPreferencesPath = '/api/user-preferences/session/';

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
          id: firstMemberSessionId,
          sessionType: 'user',
          username: firstMemberName,
          accountId: 'account-member-one',
          ipAddress: '192.168.1.51'
        }),
        session({
          id: secondMemberSessionId,
          sessionType: 'user',
          username: secondMemberName,
          accountId: 'account-member-two',
          ipAddress: '192.168.1.52'
        }),
        ...guestSessionIds.map((id, index) =>
          session({
            id,
            sessionType: 'guest',
            username: null,
            accountId: null,
            ipAddress: `192.168.1.${70 + index}`,
            expiresAt: at(3600 * 6)
          })
        )
      ],
      pagination: { totalPages: 1, totalCount: 3 + guestSessionIds.length, page: 1 },
      historySessions: []
    },
    ...Object.fromEntries(
      [ownSessionId, firstMemberSessionId, secondMemberSessionId, ...guestSessionIds].map((id) => [
        `${sessionPreferencesPath}${id}`,
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
      account({ id: 'account-member-one', username: firstMemberName }),
      account({ id: 'account-member-two', username: secondMemberName })
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

/**
 * The faked server with a hold: while `holding` is on, a request whose path `holds` accepts waits
 * in `held`, tagged with the switch the test had made when it arrived, until the test lets it go
 * with `answer` or fails it the way a timed-out connection does with `timeOut`. Every API path the
 * page asks for is kept in `asked` with the same tag.
 */
const heldServer = (holds) => {
  const answer = server(answers());
  const state = { holding: false, switchIndex: -1, held: [], asked: [] };
  const route = async (request) => {
    const { pathname } = new URL(request.request().url());
    if (pathname.startsWith('/api/'))
      state.asked.push({ pathname, switchIndex: state.switchIndex });
    if (state.holding && holds(pathname)) {
      state.held.push({ request, pathname, switchIndex: state.switchIndex });
      return;
    }
    await answer(request);
  };
  const letGo = (items, outcome) =>
    Promise.all(
      items.map((item) => {
        state.held.splice(state.held.indexOf(item), 1);
        return outcome === 'answer' ? answer(item.request) : item.request.abort('timedout');
      })
    );
  return { state, route, letGo };
};

/** Opens the Users page at `width` by 900 (390 by 844 on a phone) behind `held.route`. */
const openUsers = async (browser, width, held) => {
  const context = await browser.newContext({
    viewport: { width, height: width < 640 ? 844 : 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript(() => {
    globalThis.localStorage.setItem('lancache_language', 'en');
  });
  const page = await context.newPage();
  await page.route('**/*', held.route);
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

/** Shows the list on a phone, where a picked pane covers it; does nothing on a wide screen. */
const showList = async (page) => {
  const back = page.getByRole('button', { name: translate('user.panes.list.back'), exact: true });
  if (await back.isVisible()) {
    await back.click();
    await page.evaluate(settle);
  }
};

/** Chooses the list filter labeled by `filterKey`. */
const pickFilter = async (page, filterKey) => {
  await page
    .locator('.users-split .segmented-control-button', { hasText: translate(filterKey) })
    .first()
    .click();
  await page.evaluate(settle);
};

/** The list row button of `row`: a named row by its title, a guest by its place among guests. */
const rowButton = (page, row) =>
  page
    .locator('.users-split ul.users-rows--people li > button', {
      has: page.getByText(row.title, { exact: true })
    })
    .nth(row.nth);

const memberRow = (title) => ({ title, nth: 0 });
const guestRow = (nth) => ({ title: translate('activeSessions.filters.guest'), nth });

/**
 * Runs in the page. Sets up `globalThis.listWatch`: `boxes()` reads the box of every shown element
 * outside the detail pane that a switch must leave in place (the tools row, the list, each list
 * row, the foot's Create account button) and whether a loading state shows outside the pane;
 * `start()` reads them on every frame until `stop()`, each read stamped with its time since start.
 */
const installWatch = () => {
  const box = (element) => {
    const rect = element.getBoundingClientRect();
    return [rect.left, rect.top, rect.width, rect.height].map(Math.round).join(',');
  };
  const shown = (element) => element.getClientRects().length > 0;
  const boxes = () => {
    const read = {};
    const add = (key, element) => {
      if (element !== null && shown(element)) read[key] = box(element);
    };
    const document = globalThis.document;
    add('tools row', document.querySelector('.users-split .users-tools'));
    add('list', document.querySelector('.users-split .users-list-scroll'));
    add('Create account', document.querySelector('.users-split .users-list-foot button'));
    document.querySelectorAll('.users-split .users-rows li > button').forEach((row, index) => {
      add(`row ${index + 1} "${(row.textContent ?? '').trim().slice(0, 40)}"`, row);
    });
    const loading = [...document.querySelectorAll('[aria-busy="true"], .skeleton-shimmer')].some(
      (element) => shown(element) && element.closest('.users-detail-pane') === null
    );
    return { read, loading };
  };
  const watch = { boxes, samples: [], running: false };
  watch.start = () => {
    watch.samples = [];
    watch.running = true;
    const began = globalThis.performance.now();
    const step = () => {
      if (!watch.running) return;
      watch.samples.push({ at: Math.round(globalThis.performance.now() - began), ...boxes() });
      globalThis.requestAnimationFrame(step);
    };
    step();
  };
  watch.stop = () => {
    watch.running = false;
    return watch.samples;
  };
  globalThis.listWatch = watch;
};

/** Waits until the page's watch has read for `ms` milliseconds since it started. */
const watchedFor = (page, ms) =>
  page.waitForFunction((until) => (globalThis.listWatch.samples.at(-1)?.at ?? 0) >= until, ms, {
    polling: 'raf'
  });

/**
 * Every box of `before` that a later read lost or moved, and every read that showed a loading
 * state outside the pane, as lines naming `where`, the element, the boxes and the time.
 */
const changesFrom = (where, before, samples) => {
  const changes = [];
  const reported = new Set();
  for (const sample of samples) {
    if (sample.loading && !reported.has('loading')) {
      reported.add('loading');
      changes.push(`${where}: a loading state shows outside the pane at ${sample.at}ms`);
    }
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

// Each switch: the filter that shows both rows, the row picked first, and the row switched to.
const switches = [
  {
    name: 'account row to guest row',
    filterKey: 'user.panes.list.filters.signedIn',
    from: memberRow(firstMemberName),
    to: guestRow(0)
  },
  {
    name: 'guest row to guest row',
    filterKey: 'user.panes.list.filters.signedIn',
    from: guestRow(0),
    to: guestRow(1)
  },
  {
    name: 'account row to account row',
    filterKey: 'activeSessions.accountFilter',
    from: memberRow(firstMemberName),
    to: memberRow(secondMemberName)
  }
];

test(
  'switching between Users rows moves nothing outside the detail pane',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const changes = [];
      let watched = 0;
      for (const width of [1440, 390]) {
        const held = heldServer(
          (pathname) =>
            pathname.startsWith(sessionPreferencesPath) || guestConfigPaths.includes(pathname)
        );
        const { context, page } = await openUsers(browser, width, held);
        try {
          await page.evaluate(installWatch);
          for (const { name, filterKey, from, to } of switches) {
            const where = `${name} at ${width} wide`;
            await showList(page);
            await pickFilter(page, filterKey);
            await rowButton(page, from).click();
            await page.evaluate(settle);
            if (width === 1440) {
              // The new pane's answers come late, the way a slow server sends them.
              const { read: before } = await page.evaluate(() => globalThis.listWatch.boxes());
              watched += Object.keys(before).length;
              held.state.holding = true;
              await page.evaluate(() => globalThis.listWatch.start());
              await rowButton(page, to).click();
              await watchedFor(page, 500);
              held.state.holding = false;
              await held.letGo([...held.state.held], 'answer');
              await watchedFor(page, 1000);
              const samples = await page.evaluate(() => globalThis.listWatch.stop());
              changes.push(...changesFrom(where, before, samples));
            } else {
              // On a phone the pane covers the list; the list comes back where it was.
              await showList(page);
              const { read: before } = await page.evaluate(() => globalThis.listWatch.boxes());
              watched += Object.keys(before).length;
              await rowButton(page, to).click();
              await page.evaluate(settle);
              await page.evaluate(() => globalThis.listWatch.start());
              await page
                .getByRole('button', { name: translate('user.panes.list.back'), exact: true })
                .click();
              await watchedFor(page, 1000);
              const samples = await page.evaluate(() => globalThis.listWatch.stop());
              // The reads before the list is drawn again belong to the pane, which covers it.
              const back = samples.findIndex((sample) => 'tools row' in sample.read);
              changes.push(
                ...(back === -1
                  ? [`${where}: the list never came back`]
                  : changesFrom(where, before, samples.slice(back)))
              );
            }
          }
        } finally {
          await context.close();
        }
      }
      assert.ok(watched > 0, 'no element outside the detail pane was found to watch');
      assert.deepEqual(
        changes,
        [],
        'these elements outside the Users detail pane moved, went away or showed a loading state during a row switch'
      );
    })
);

// The ten quick switches between the four guests; the last pick is the third guest.
const guestSwitches = [1, 2, 3, 0, 1, 2, 3, 0, 1, 2];
const lastSwitch = guestSwitches.length - 1;

// How the held thread defaults come back: those of the earlier switches and those of the last
// pick, each answered or timed out, in either order.
const releaseCases = [
  {
    name: 'earlier answered, then the last pick answered',
    steps: [
      ['earlier', 'answer'],
      ['last', 'answer']
    ]
  },
  {
    name: 'the last pick answered, then earlier answered',
    steps: [
      ['last', 'answer'],
      ['earlier', 'answer']
    ]
  },
  {
    name: 'earlier timed out, then the last pick answered',
    steps: [
      ['earlier', 'timeOut'],
      ['last', 'answer']
    ]
  },
  {
    name: 'the last pick answered, then earlier timed out',
    steps: [
      ['last', 'answer'],
      ['earlier', 'timeOut']
    ]
  }
];

/** Runs in the page. Whether the detail pane shows the block titled `title`. */
const paneShows = (title) =>
  [...globalThis.document.querySelectorAll('.users-detail-pane *')].some(
    (element) =>
      element.children.length === 0 &&
      element.getClientRects().length > 0 &&
      (element.textContent ?? '').trim() === title
  );

test(
  'quick switches between guests ask for the thread defaults no more and never show their load error',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const errorTitle = translate('user.errors.loadThreadConfig');
      const shownErrors = [];
      const asks = [];
      for (const { name, steps } of releaseCases) {
        const held = heldServer((pathname) => guestConfigPaths.includes(pathname));
        const { context, page } = await openUsers(browser, 1440, held);
        try {
          await pickFilter(page, 'user.panes.list.filters.signedIn');
          await rowButton(page, guestRow(0)).click();
          await page.evaluate(settle);
          held.state.holding = true;
          for (const [index, nth] of guestSwitches.entries()) {
            held.state.switchIndex = index;
            const preferencesAsked = held.state.asked.filter((ask) =>
              ask.pathname.startsWith(sessionPreferencesPath)
            ).length;
            await rowButton(page, guestRow(nth)).click();
            // The pane asks for its preferences in the same step as the thread defaults, so once
            // that request is in, this switch's requests are tagged with it. A pane that asks for
            // nothing is given 120 frames.
            for (
              let frames = 0;
              frames < 120 &&
              held.state.asked.filter((ask) => ask.pathname.startsWith(sessionPreferencesPath))
                .length === preferencesAsked;
              frames += 1
            ) {
              await page.evaluate(() => new Promise(globalThis.requestAnimationFrame));
            }
            await page.evaluate(() => new Promise(globalThis.requestAnimationFrame));
          }
          held.state.holding = false;
          for (const [group, outcome] of steps) {
            const items = held.state.held.filter((item) =>
              group === 'last' ? item.switchIndex === lastSwitch : item.switchIndex !== lastSwitch
            );
            await held.letGo(items, outcome);
            await page.evaluate(settle);
            if (await page.evaluate(paneShows, errorTitle)) {
              shownErrors.push(`${name}: shown after ${group} ${outcome}`);
            }
          }
          const perSwitch = guestSwitches.map(
            (_, index) =>
              held.state.asked.filter(
                (ask) => ask.switchIndex === index && guestConfigPaths.includes(ask.pathname)
              ).length
          );
          if (perSwitch.some((count) => count > 0)) {
            asks.push(`${name}: ${perSwitch.join(', ')}`);
          }
        } finally {
          await context.close();
        }
      }
      assert.deepEqual(
        { shownErrors, asks },
        { shownErrors: [], asks: [] },
        `after ten quick guest switches the last pick's pane showed "${errorTitle}" (shownErrors), or a switch asked again for the guest thread defaults that had not changed, counted per switch (asks)`
      );
    })
);
