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
// in as the primary admin on the Users page with one guest signed in, a user-role account with one
// session signed in and one ended, and three more accounts with no session: one in use, one disabled
// and one that never signed in. Each test reads one thing an admin sees there: whether a button
// keeps its width, whether a label or an id holds to one line, whether the dialogs share one footer,
// whether a grayed name stays readable, and what an account's sign-in line, a session pane's
// headings and labels, and a session's dialogs say. Runs only with PLAYWRIGHT_PATH set to a playwright-core install whose Chromium
// is downloaded.

const translate = translator('en');
const ownSessionId = '7822aea4-66f6-4a1a-891c-3473a6fd64a5';
const guestSessionId = 'd4a92e63-1c5f-4e7b-b3d4-a2f3e1c6e544';
const adminName = 'uitest-admin';
const userName = 'uitest-user';
const disabledName = 'uitest-disabled';
const newName = 'uitest-new';
const memberName = 'uitest-member';
const memberSessionId = 'c3d81f52-77e0-4b2a-a6c3-91e2f0b5d433';
const endedSessionId = '39f47db8-6bae-4dc0-8829-f7e8d6b1d099';
const chromeOnWindows =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ' +
  'Chrome/131.0.0.0 Safari/537.36';

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
      selectedTheme: null,
      sharpCorners: false,
      disableFocusOutlines: true,
      disableTooltips: false,
      picsAlwaysVisible: false,
      disableStickyNotifications: false,
      useLocalTimezone: false,
      useUtcTimezone: false,
      use24HourFormat: true,
      showDatasourceLabels: true,
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
        })
      ],
      pagination: { totalPages: 1, totalCount: 3, page: 1 },
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
    // What the server sends for a session's own preferences; it leaves null fields out.
    ...Object.fromEntries(
      [ownSessionId, guestSessionId, memberSessionId, endedSessionId].map((id) => [
        `/api/user-preferences/session/${id}`,
        {
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
        }
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
        lastLoginAtUtc: at(-3600)
      },
      {
        id: 'account-user',
        username: userName,
        role: 'user',
        isMainAdmin: false,
        isDisabled: false,
        createdAtUtc: at(-86400 * 30),
        lastLoginAtUtc: at(-86400)
      },
      {
        id: 'account-disabled',
        username: disabledName,
        role: 'user',
        isMainAdmin: false,
        isDisabled: true,
        createdAtUtc: at(-86400 * 20),
        lastLoginAtUtc: at(-86400 * 2)
      },
      {
        id: 'account-member',
        username: memberName,
        role: 'user',
        isMainAdmin: false,
        isDisabled: false,
        createdAtUtc: at(-86400 * 40),
        lastLoginAtUtc: at(-600)
      },
      // Never signed in: the server leaves a null field out of its JSON, so the key is absent.
      {
        id: 'account-new',
        username: newName,
        role: 'user',
        isMainAdmin: false,
        isDisabled: false,
        createdAtUtc: at(-86400 * 3)
      }
    ],
    '/api/auth/guest/config': { durationHours: 6, isLocked: false },
    // No length chosen in the app or set in the config file, so the row shows no Reset button.
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

/** Opens the Users page at `width` by 900 (390 by 844 on a phone) and waits for its list. */
const openUsersPage = async (browser, width) => {
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

/** Shows the list on a phone, where a picked row covers it with the detail pane. */
const showList = async (page) => {
  const back = page.getByRole('button', { name: translate('user.panes.list.back'), exact: true });
  if (await back.isVisible()) {
    await back.click();
    await page.evaluate(settle);
  }
};

const pickFilter = async (page, filterKey) => {
  await showList(page);
  await page
    .locator('.users-split .segmented-control-button', { hasText: translate(filterKey) })
    .first()
    .click();
  await page.evaluate(settle);
};

/** Picks the list row whose title is `title` and waits for its pane. */
const pickRow = async (page, title, filterKey = 'user.panes.list.filters.signedIn') => {
  await pickFilter(page, filterKey);
  await page
    .locator('.users-split li > button', {
      has: page.getByText(title, { exact: true })
    })
    .first()
    .click();
  await page.evaluate(settle);
};

const pickGuestAccess = (page) => pickRow(page, translate('activeSessions.access.title'));

/** The one shown dialog, measured: its title, its footer line and its footer buttons. */
const measureDialog = (cancelLabel) => {
  const document = globalThis.document;
  const dialogs = [...document.querySelectorAll('[role="dialog"]')].filter(
    (element) => element.getClientRects().length > 0
  );
  if (dialogs.length !== 1) return { found: false, count: dialogs.length };
  const [dialog] = dialogs;
  const cancel = [...dialog.querySelectorAll('button')].find(
    (button) => button.textContent.trim() === cancelLabel
  );
  if (!cancel) return { found: false, count: 1 };
  const footer = cancel.parentElement;
  const style = globalThis.getComputedStyle(footer);
  const box = (element) => {
    const rect = element.getBoundingClientRect();
    return { label: element.textContent.trim(), width: rect.width, height: rect.height };
  };
  return {
    found: true,
    title: dialog.querySelector('.modal-title')?.textContent.trim() ?? '',
    // The first paragraph of the body, outside the box naming the dialog's subject.
    question:
      [...dialog.querySelectorAll('p')]
        .find((paragraph) => !paragraph.closest('.mgmt-panel'))
        ?.textContent.trim() ?? '',
    text: dialog.innerText.replace(/\s+/g, ' ').trim(),
    hasInputs: dialog.querySelector('input') !== null,
    divider: style.borderTopStyle === 'none' ? 0 : parseFloat(style.borderTopWidth),
    buttons: [...footer.querySelectorAll(':scope > button')].map(box),
    inputs: [...dialog.querySelectorAll('input')]
      .filter((input) => input.getClientRects().length > 0)
      .map((input) => ({ label: input.type, height: input.getBoundingClientRect().height }))
  };
};

const closeDialog = async (page) => {
  await page
    .locator('[role="dialog"]')
    .getByRole('button', { name: translate('common.cancel'), exact: true })
    .click();
  await page.waitForFunction(
    () =>
      [...globalThis.document.querySelectorAll('[role="dialog"]')].every(
        (element) => element.getClientRects().length === 0
      ),
    undefined,
    { polling: 'raf' }
  );
  await page.evaluate(settle);
};

const paneMenu = (page) =>
  page
    .locator('.users-detail-pane')
    .getByRole('button', { name: translate('common.moreActions'), exact: true })
    .first();

/** Measures the dialog a click opened and closes it with Cancel; null when none opened. */
const dialogAfterClick = async (page, pane, trigger) => {
  await page.evaluate(settle);
  const dialog = await page.evaluate(measureDialog, translate('common.cancel'));
  if (!dialog.found) return null;
  await closeDialog(page);
  return { pane, trigger, ...dialog };
};

const labelOf = async (locator) => (await locator.innerText()).replace(/\s+/g, ' ').trim();

/**
 * Opens every item of the picked pane's menu in turn, and with `headerButtons` every button in the
 * pane's header too, and measures each dialog one opens, closing it with Cancel. An item that opens
 * no dialog is left out.
 */
const menuDialogs = async (page, pane, headerButtons = false) => {
  const measured = [];
  if (headerButtons) {
    const buttons = page.locator('.users-detail-pane .users-pane-btn');
    const count = await buttons.count();
    for (let index = 0; index < count; index += 1) {
      const trigger = await labelOf(buttons.nth(index));
      await buttons.nth(index).click();
      const dialog = await dialogAfterClick(page, pane, trigger);
      if (dialog) measured.push(dialog);
    }
  }
  // An ended session's pane may have no menu.
  if ((await paneMenu(page).count()) === 0) return measured;
  await paneMenu(page).click();
  await page.evaluate(settle);
  const count = await page.locator('.am-dropdown button').count();
  await page.keyboard.press('Escape');
  await page.evaluate(settle);
  for (let index = 0; index < count; index += 1) {
    await paneMenu(page).click();
    await page.evaluate(settle);
    const item = page.locator('.am-dropdown button').nth(index);
    const trigger = await labelOf(item);
    await item.click();
    const dialog = await dialogAfterClick(page, pane, trigger);
    if (dialog) measured.push(dialog);
  }
  return measured;
};

/**
 * Every confirm dialog the Guest access, guest session and account panes open from their menus,
 * and from the guest session's header buttons. The account pane's header button disables the
 * account with no dialog, so it is not pressed.
 */
const confirmDialogs = async (page) => {
  await pickGuestAccess(page);
  const dialogs = await menuDialogs(page, 'Guest access');
  await pickRow(page, translate('activeSessions.filters.guest'), 'user.panes.list.filters.guests');
  dialogs.push(...(await menuDialogs(page, 'guest session', true)));
  await pickRow(page, userName, 'activeSessions.accountFilter');
  dialogs.push(...(await menuDialogs(page, `${userName} account`)));
  return dialogs.filter((dialog) => !dialog.hasInputs);
};

const openCreateAccount = async (page) => {
  await showList(page);
  await page
    .locator('.users-split')
    .getByRole('button', { name: translate('user.accounts.actions.create'), exact: true })
    .click();
  await page.evaluate(settle);
  return page.evaluate(measureDialog, translate('common.cancel'));
};

const openEditAccount = async (page) => {
  await pickRow(page, userName, 'activeSessions.accountFilter');
  await paneMenu(page).click();
  await page.evaluate(settle);
  await page
    .locator('.am-dropdown button', { hasText: translate('user.panes.menu.editAccount') })
    .click();
  await page.evaluate(settle);
  return page.evaluate(measureDialog, translate('common.cancel'));
};

/** The list row whose title is `title`. */
const listRow = (page, title) =>
  page.locator('.users-split li > button', { has: page.getByText(title, { exact: true }) }).first();

/**
 * Runs in the page. The WCAG contrast of the list row title `name` against the row's background,
 * every see-through fill under it blended down to the first solid one, and the colors of the title
 * and of the row's status line.
 */
const nameContrast = (name) => {
  const document = globalThis.document;
  const title = [...document.querySelectorAll('.users-split li > button *')].find(
    (element) => element.children.length === 0 && element.textContent.trim() === name
  );
  if (!title) return { found: false };
  const parse = (value) => {
    const parts = value
      .match(/rgba?\(([^)]+)\)/)[1]
      .split(/[\s,/]+/)
      .filter(Boolean)
      .map(Number);
    return { r: parts[0], g: parts[1], b: parts[2], a: parts.length > 3 ? parts[3] : 1 };
  };
  const fills = [];
  for (let element = title; element; element = element.parentElement) {
    const fill = parse(globalThis.getComputedStyle(element).backgroundColor);
    if (fill.a > 0) fills.push(fill);
    if (fill.a === 1) break;
  }
  const over = (top, under) => ({
    r: top.r * top.a + under.r * (1 - top.a),
    g: top.g * top.a + under.g * (1 - top.a),
    b: top.b * top.a + under.b * (1 - top.a),
    a: 1
  });
  const background = fills
    .reverse()
    .reduce((under, fill) => over(fill, under), { r: 255, g: 255, b: 255, a: 1 });
  const textColor = globalThis.getComputedStyle(title).color;
  const text = over(parse(textColor), background);
  const luminance = ({ r, g, b }) => {
    const channel = (value) => {
      const scaled = value / 255;
      return scaled <= 0.03928 ? scaled / 12.92 : ((scaled + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
  };
  const [light, dark] = [luminance(text), luminance(background)].sort((a, b) => b - a);
  const row = title.closest('button');
  const lineElement = [...row.querySelectorAll('*')]
    .filter((element) => element.children.length === 0 && element.textContent.trim().length > 0)
    .find((element) => element !== title && !title.parentElement.contains(element));
  return {
    found: true,
    ratio: Math.round(((light + 0.05) / (dark + 0.05)) * 100) / 100,
    nameColor: textColor,
    lineColor: lineElement ? globalThis.getComputedStyle(lineElement).color : null
  };
};

/** How many lines the text `text` takes inside the element matched by `root`, or 0 if absent. */
const linesOf = ({ root, text }) => {
  const document = globalThis.document;
  const scope = document.querySelector(root);
  if (!scope) return 0;
  const walker = document.createTreeWalker(scope, globalThis.NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const start = node.textContent.indexOf(text);
    if (start < 0 || node.parentElement.getClientRects().length === 0) continue;
    const range = document.createRange();
    range.setStart(node, start);
    range.setEnd(node, start + text.length);
    const tops = new Set(
      [...range.getClientRects()]
        .filter((rect) => rect.width > 0)
        .map((rect) => Math.round(rect.top))
    );
    return tops.size;
  }
  return 0;
};

test(
  'the session pane Copy button keeps its width when it turns into "Copied"',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      for (const width of [1440, 390]) {
        const { context, page } = await openUsersPage(browser, width);
        try {
          await pickRow(page, adminName);
          const copy = page
            .locator('.users-detail-pane')
            .getByRole('button', { name: translate('user.panes.facts.copyAria'), exact: true });
          const before = await copy.evaluate((button) => button.getBoundingClientRect().width);
          await copy.click();
          await page.waitForFunction(
            ({ label, copied }) =>
              [...globalThis.document.querySelectorAll('.users-detail-pane button')].some(
                (button) =>
                  button.getAttribute('aria-label') === label && button.innerText.trim() === copied
              ),
            {
              label: translate('user.panes.facts.copyAria'),
              copied: translate('user.panes.facts.copied')
            },
            { polling: 'raf' }
          );
          const after = await copy.evaluate((button) => button.getBoundingClientRect().width);
          assert.ok(
            Math.abs(after - before) < 0.5,
            `at ${width} wide the Copy button is ${before}px wide before the click and ` +
              `${after}px wide once it reads "${translate('user.panes.facts.copied')}"`
          );
        } finally {
          await context.close();
        }
      }
    })
);

test(
  'on a phone the session id in the session pane stays on one line',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const { context, page } = await openUsersPage(browser, 390);
      try {
        await pickRow(page, adminName);
        const lines = await page.evaluate(linesOf, {
          root: '.users-detail-pane',
          text: ownSessionId
        });
        assert.ok(lines > 0, 'the session id shows in the session pane at 390 wide');
        assert.equal(
          lines,
          1,
          `at 390 wide the session id ${ownSessionId} breaks over ${lines} lines in the session pane`
        );
      } finally {
        await context.close();
      }
    })
);

test(
  'on a phone the Guest access "Session length" label stays on one line beside its dropdown',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const { context, page } = await openUsersPage(browser, 390);
      try {
        await pickGuestAccess(page);
        const label = translate('user.panes.prefs.sessionLength');
        const lines = await page.evaluate(linesOf, { root: '.users-detail-pane', text: label });
        assert.ok(lines > 0, `the "${label}" label shows in the Guest access pane at 390 wide`);
        assert.equal(
          lines,
          1,
          `at 390 wide the "${label}" label wraps over ${lines} lines in the Guest access pane`
        );
      } finally {
        await context.close();
      }
    })
);

test(
  'the Create account and Edit account inputs are as tall as the dialog buttons',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      for (const width of [1440, 390]) {
        const { context, page } = await openUsersPage(browser, width);
        try {
          for (const [name, open] of [
            ['Create account', openCreateAccount],
            ['Edit account', openEditAccount]
          ]) {
            const dialog = await open(page);
            assert.ok(dialog.found, `the ${name} dialog opens at ${width} wide`);
            assert.ok(dialog.inputs.length > 0, `the ${name} dialog shows its inputs`);
            const [cancel] = dialog.buttons;
            for (const input of dialog.inputs) {
              assert.ok(
                Math.abs(input.height - cancel.height) < 0.5,
                `at ${width} wide the ${name} ${input.label} input is ${input.height}px tall ` +
                  `but the "${cancel.label}" button below it is ${cancel.height}px tall`
              );
            }
            await closeDialog(page);
          }
        } finally {
          await context.close();
        }
      }
    })
);

test(
  'every Users page confirm dialog gives its two footer buttons one width on a wide screen',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const { context, page } = await openUsersPage(browser, 1440);
      try {
        const dialogs = await confirmDialogs(page);
        assert.ok(dialogs.length > 0, 'the pane menus open at least one confirm dialog');
        const uneven = dialogs.filter(
          (dialog) =>
            dialog.buttons.length < 2 ||
            dialog.buttons.some((button) => Math.abs(button.width - dialog.buttons[0].width) >= 0.5)
        );
        assert.deepEqual(
          uneven.map(
            (dialog) =>
              `${dialog.pane}: "${dialog.title}" footer ` +
              dialog.buttons.map((button) => `"${button.label}" ${button.width}px`).join(' vs ')
          ),
          [],
          'at 1440 wide these confirm dialogs show footer buttons of different widths'
        );
      } finally {
        await context.close();
      }
    })
);

test(
  'the Create account and Edit account dialogs use the footer of the other Users dialogs',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      for (const width of [1440, 390]) {
        const { context, page } = await openUsersPage(browser, width);
        try {
          const confirms = await confirmDialogs(page);
          assert.ok(confirms.length > 0, `the pane menus open a confirm dialog at ${width} wide`);
          const divider = confirms[0].divider;
          assert.ok(divider > 0, `the confirm dialogs draw a line above their footer at ${width}`);
          const narrowest = Math.min(
            ...confirms.flatMap((dialog) => dialog.buttons.map((button) => button.width))
          );
          for (const [name, open] of [
            ['Create account', openCreateAccount],
            ['Edit account', openEditAccount]
          ]) {
            const dialog = await open(page);
            assert.ok(dialog.found, `the ${name} dialog opens at ${width} wide`);
            assert.equal(
              dialog.divider,
              divider,
              `at ${width} wide the ${name} dialog draws a ${dialog.divider}px line above its ` +
                `footer but the confirm dialogs draw a ${divider}px line`
            );
            for (const button of dialog.buttons) {
              assert.ok(
                button.width >= narrowest - 0.5,
                `at ${width} wide the ${name} "${button.label}" button is ${button.width}px ` +
                  `wide but no confirm dialog footer button is narrower than ${narrowest}px`
              );
            }
            await closeDialog(page);
          }
        } finally {
          await context.close();
        }
      }
    })
);

test(
  "a disabled account's grayed name stays readable on its row when hovered and when selected",
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      for (const width of [1440, 390]) {
        const { context, page } = await openUsersPage(browser, width);
        try {
          await pickFilter(page, 'activeSessions.accountFilter');
          await listRow(page, disabledName).hover();
          await page.evaluate(settle);
          const hovered = await page.evaluate(nameContrast, disabledName);
          assert.ok(hovered.found, `the ${disabledName} row shows at ${width} wide`);

          await listRow(page, disabledName).click();
          await page.evaluate(settle);
          await showList(page);
          await page.mouse.move(0, 0);
          await page.evaluate(settle);
          const selected = await page.evaluate(nameContrast, disabledName);
          assert.ok(selected.found, `the selected ${disabledName} row shows at ${width} wide`);

          for (const [state, measured] of [
            ['hovered', hovered],
            ['selected', selected]
          ]) {
            assert.ok(
              measured.ratio >= 4.5,
              `at ${width} wide the disabled account's name on its ${state} row has a ` +
                `contrast of ${measured.ratio}:1 (${measured.nameColor}), under 4.5:1`
            );
          }
          assert.notEqual(
            hovered.nameColor,
            hovered.lineColor,
            `at ${width} wide the disabled account's name on its hovered row is drawn in the ` +
              `same color as the status line under it`
          );
        } finally {
          await context.close();
        }
      }
    })
);

test(
  'an account that never signed in says so in its row and in its pane',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const { context, page } = await openUsersPage(browser, 1440);
      try {
        const never = translate('user.panes.list.neverSignedIn');
        await pickFilter(page, 'activeSessions.accountFilter');
        const row = (await listRow(page, newName).innerText()).replace(/\s+/g, ' ').trim();
        assert.ok(
          row.includes(never),
          `the row of ${newName}, which never signed in, reads "${row}" with no "${never}"`
        );
        await listRow(page, newName).click();
        await page.evaluate(settle);
        const pane = (await page.locator('.users-detail-pane').innerText())
          .replace(/\s+/g, ' ')
          .trim();
        assert.ok(
          pane.includes(never),
          `the pane of ${newName}, which never signed in, reads "${pane.slice(0, 160)}" ` +
            `with no "${never}"`
        );
      } finally {
        await context.close();
      }
    })
);

/** Every text the detail pane shows on its own element, trimmed. */
const paneTexts = (page) =>
  page.evaluate(() =>
    [...globalThis.document.querySelectorAll('.users-detail-pane *')]
      .filter(
        (element) =>
          element.getClientRects().length > 0 &&
          [...element.childNodes].some(
            (node) => node.nodeType === 3 && node.textContent.trim().length > 0
          )
      )
      .map((element) => element.textContent.replace(/\s+/g, ' ').trim())
  );

/** The session panes an admin opens: own, a guest's, a user-role account's, and an ended one. */
const sessionPanes = [
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
  { pane: 'ended session', title: memberName, filterKey: 'user.panes.list.filters.ended' }
];

test(
  "a user-role account's session pane does not label its refresh-rate row for a guest",
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const { context, page } = await openUsersPage(browser, 1440);
      try {
        const guestLabel = translate('user.panes.prefs.mayChangeRate');
        const rateLabel = translate('user.panes.prefs.refreshRate');
        await pickRow(
          page,
          translate('activeSessions.filters.guest'),
          'user.panes.list.filters.guests'
        );
        const guest = await paneTexts(page);
        assert.ok(
          guest.includes(guestLabel),
          `the guest session pane shows its "${guestLabel}" row`
        );
        await pickRow(page, memberName);
        const member = await paneTexts(page);
        assert.ok(
          member.includes(rateLabel),
          `the ${memberName} session pane shows its "${rateLabel}" row`
        );
        assert.ok(
          !member.includes(guestLabel),
          `the session pane of ${memberName}, a user-role account, labels a row "${guestLabel}"`
        );
      } finally {
        await context.close();
      }
    })
);

test(
  'only the viewer\'s own session pane heads its device "This device"',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const { context, page } = await openUsersPage(browser, 1440);
      try {
        const heading = translate('user.panes.sections.thisDevice');
        const shown = [];
        for (const { pane, title, filterKey } of sessionPanes) {
          await pickRow(page, title, filterKey);
          if ((await paneTexts(page)).includes(heading)) shown.push(pane);
        }
        assert.deepEqual(
          shown,
          ['own session'],
          `the "${heading}" heading shows on these session panes`
        );
      } finally {
        await context.close();
      }
    })
);

test(
  'the Revoke and Delete dialogs of a session name it a session, and an ended one is not logged out again',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const { context, page } = await openUsersPage(browser, 1440);
      try {
        const loggedOut = 'logged out immediately';
        const otherNoun = /\b(device|devices|user|users)\b/i;
        const found = [];
        const wrong = [];
        for (const { pane, title, filterKey } of sessionPanes.slice(1)) {
          await pickRow(page, title, filterKey);
          const sessionId =
            pane === 'ended session'
              ? endedSessionId
              : pane === 'guest session'
                ? guestSessionId
                : memberSessionId;
          // A session's own dialogs name its id; the account dialogs of the same menu do not.
          const dialogs = (await menuDialogs(page, pane, true)).filter((dialog) =>
            dialog.text.includes(sessionId)
          );
          found.push(...dialogs);
          for (const dialog of dialogs) {
            const where = `${dialog.pane}, "${dialog.trigger}" opens "${dialog.title}"`;
            if (!/\bsession\b/i.test(dialog.title)) {
              wrong.push(`${where}: no "session" in the title`);
            }
            const confirm = dialog.buttons.at(-1).label;
            for (const [part, text] of [
              ['button or menu item', dialog.trigger],
              ['title', dialog.title],
              ['question', dialog.question],
              ['confirm button', confirm]
            ]) {
              if (otherNoun.test(text)) wrong.push(`${where}: ${part} "${text}"`);
            }
            if (pane === 'ended session' && dialog.text.includes(loggedOut)) {
              wrong.push(`${where}: an ended session's dialog says "${loggedOut}"`);
            }
          }
        }
        assert.ok(
          found.some((dialog) => dialog.pane === 'ended session'),
          'the ended session pane opens a dialog'
        );
        assert.ok(found.length > 1, 'the session panes open their Revoke and Delete dialogs');
        assert.deepEqual(wrong, [], 'these session dialogs name the session by another noun');
      } finally {
        await context.close();
      }
    })
);
