import assert from 'node:assert/strict';
import test from 'node:test';
import {
  builtAppOrigin,
  declaredValue,
  fakeHub,
  server,
  settle,
  translator,
  withBrowser
} from './transpile-module.mjs';

// Opens the built app (run `npm run build` first) in Chromium with every API answer faked, as a
// visitor on the sign-in screen and as a guest on the pages a guest reaches, in Password mode with
// no download recorded yet. Each test reads one thing a visitor sees there: the words on a card or
// a button, whether a label holds to one line, and how tall a control or its tap area is. Runs only
// with PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const navigateToTab = declaredValue('src/utils/constants.ts', 'APP_EVENTS').NAVIGATE_TO_TAB;

const answers = (visitor) => ({
  '/api/auth/status': {
    isAuthenticated: visitor === 'guest',
    authenticationEnabled: true,
    accountMode: 'password',
    authenticationSetupRequired: false,
    sessionType: visitor === 'guest' ? 'guest' : null,
    sessionId: visitor === 'guest' ? 'guest-session' : null,
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
  '/api/system/default-guest-refresh-rate': { locked: false, refreshRate: 'STANDARD' },
  // Nothing downloaded yet: the Downloads and Service Analytics cards show their empty states.
  '/api/dashboard/batch': {
    cache: null,
    clients: [],
    services: [],
    dashboard: null,
    downloadTotals: null,
    filteredDownloadTotals: null,
    serviceOptions: [],
    clientOptions: [],
    recentDownloads: { groups: [], rows: [] },
    detection: { hasCachedResults: false },
    sparklines: null,
    hourlyActivity: null,
    cacheSnapshot: null
  }
});

/** Opens the app in `language` as `visitor` ('guest' or 'signed-out') at `width` by 900. */
const openApp = async (browser, { visitor, language, width }) => {
  const context = await browser.newContext({
    viewport: { width, height: 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript((chosen) => {
    globalThis.localStorage.setItem('lancache_language', chosen);
  }, language);
  const page = await context.newPage();
  await page.route('**/*', server(answers(visitor)));
  await fakeHub(page);
  await page.goto(`${builtAppOrigin}/`);
  return { context, page };
};

/** Shows the guest tab named by its id, the way the app's own links between pages do. */
const showTab = async (page, tab) => {
  await page.evaluate(
    ({ name, chosen }) =>
      globalThis.dispatchEvent(new globalThis.CustomEvent(name, { detail: { tab: chosen } })),
    { name: navigateToTab, chosen: tab }
  );
  await page.evaluate(settle);
};

const showWidth = async (page, width) => {
  await page.setViewportSize({ width, height: 900 });
  await page.waitForFunction((wanted) => globalThis.innerWidth === wanted, width);
  await page.evaluate(settle);
};

/**
 * Runs in the page: the tallest strip, through the middle of `element`, where a tap lands on
 * `element` (or on something inside it). A control can draw a small box and still take taps
 * across a larger area around it.
 */
const tapHeight = (element) => {
  const document = globalThis.document;
  element.scrollIntoView({ block: 'center', inline: 'nearest' });
  const box = element.getBoundingClientRect();
  const x = box.left + box.width / 2;
  const middle = box.top + box.height / 2;
  const lands = (y) => {
    const hit = document.elementFromPoint(x, y);
    return hit !== null && (hit === element || element.contains(hit));
  };
  let top = middle;
  while (top > middle - 40 && lands(top - 0.5)) top -= 0.5;
  let bottom = middle;
  while (bottom < middle + 40 && lands(bottom + 0.5)) bottom += 0.5;
  return { drawn: box.height, taps: bottom - top + 0.5 };
};

/** The name, drawn height and tap height of every drawn control `controls` finds. */
const measureTaps = async (controls) => {
  const measured = [];
  for (const control of await controls.filter({ visible: true }).all()) {
    const name = await control.evaluate((element) =>
      (element.getAttribute('aria-label') ?? element.innerText).replace(/\s+/g, ' ').trim()
    );
    measured.push({ name, ...(await control.evaluate(tapHeight)) });
  }
  return measured;
};

const runsIfPlaywright = { skip: !process.env.PLAYWRIGHT_PATH };

test(
  'the empty Downloads card under the Live range does not drop the range name into a sentence',
  runsIfPlaywright,
  async () => {
    await withBrowser(async (browser) => {
      const problems = [];
      for (const language of ['en', 'zh']) {
        const t = translator(language);
        const { context, page } = await openApp(browser, {
          visitor: 'guest',
          language,
          width: 1440
        });
        try {
          const title = t('dashboard.downloadsPanel.emptyStates.noDownloads');
          await page.getByText(title, { exact: true }).first().waitFor();
          await page.evaluate(settle);
          const shown = await page.evaluate((wanted) => {
            const heading = [...globalThis.document.querySelectorAll('body *')].find(
              (element) => element.children.length === 0 && element.textContent.trim() === wanted
            );
            return heading.parentElement.innerText.replace(/\s+/g, ' ').trim();
          }, title);
          const rangeName = t('dashboard.downloadsPanel.timeRanges.live');
          if (shown.toLowerCase().includes(rangeName.toLowerCase())) {
            problems.push(`in ${language} the empty Downloads card reads "${shown}"`);
          }
        } finally {
          await context.close();
        }
      }
      assert.deepEqual(problems, [], problems.join('\n'));
    });
  }
);

test(
  'the Service Analytics empty-state button names the Downloads page it opens and is touch sized on a phone',
  runsIfPlaywright,
  async () => {
    await withBrowser(async (browser) => {
      const problems = [];
      for (const language of ['en', 'zh']) {
        const t = translator(language);
        const { context, page } = await openApp(browser, {
          visitor: 'guest',
          language,
          width: 1440
        });
        try {
          const title = t('dashboard.serviceAnalytics.empty.title');
          await page.getByText(title, { exact: true }).first().waitFor();
          await page.evaluate(settle);
          const emptyState = page
            .getByText(title, { exact: true })
            .first()
            .locator('xpath=ancestor::*[.//button][1]');
          const button = emptyState.getByRole('button').first();
          const label = (await button.innerText()).trim();

          await showWidth(page, 390);
          const tap = await button.evaluate(tapHeight);
          if (tap.drawn < 44 - 0.5) {
            problems.push(`in ${language} at 390 px the "${label}" button is ${tap.drawn}px tall`);
          }
          await showWidth(page, 1440);

          await button.click();
          await page.evaluate(settle);
          const pageName = t('nav.downloads');
          const opened = (
            await page
              .locator('nav button[aria-current="page"]')
              .filter({ visible: true })
              .first()
              .innerText()
          ).trim();
          if (opened !== pageName) {
            problems.push(`in ${language} the "${label}" button opened the "${opened}" page`);
          }
          if (!label.toLowerCase().includes(pageName.toLowerCase())) {
            problems.push(
              `in ${language} the button that opens the ${pageName} page reads "${label}"`
            );
          }
        } finally {
          await context.close();
        }
      }
      assert.deepEqual(problems, [], problems.join('\n'));
    });
  }
);

test(
  'at 390 px the Downloads header Hit Rate label stays on one line',
  runsIfPlaywright,
  async () => {
    await withBrowser(async (browser) => {
      const problems = [];
      for (const language of ['en', 'zh']) {
        const t = translator(language);
        const { context, page } = await openApp(browser, {
          visitor: 'guest',
          language,
          width: 1440
        });
        try {
          await page
            .getByText(t('dashboard.downloadsPanel.emptyStates.noDownloads'))
            .first()
            .waitFor();
          await showTab(page, 'downloads');
          const label = t('downloads.header.hitRateLabel');
          await page.getByText(label, { exact: true }).first().waitFor();
          await showWidth(page, 390);
          const lines = await page
            .getByText(label, { exact: true })
            .first()
            .evaluate((element) => {
              const range = globalThis.document.createRange();
              range.selectNodeContents(element);
              const tops = [...range.getClientRects()]
                .filter((rect) => rect.width > 0)
                .map((rect) => Math.round(rect.top));
              return new Set(tops).size;
            });
          if (lines !== 1) {
            problems.push(`in ${language} at 390 px "${label}" is drawn on ${lines} lines`);
          }
        } finally {
          await context.close();
        }
      }
      assert.deepEqual(problems, [], problems.join('\n'));
    });
  }
);

test('at 390 px every header control takes taps across 44 px', runsIfPlaywright, async () => {
  await withBrowser(async (browser) => {
    const { context, page } = await openApp(browser, {
      visitor: 'guest',
      language: 'en',
      width: 390
    });
    try {
      await page.locator('header button').filter({ visible: true }).first().waitFor();
      await showWidth(page, 390);
      const measured = await measureTaps(page.locator('header button'));
      assert.ok(measured.length > 0, 'the phone header draws no button');
      const small = measured.filter((control) => control.taps < 44 - 0.5);
      assert.deepEqual(
        small,
        [],
        small
          .map(
            (control) =>
              `the header control "${control.name}" is ${control.drawn}px tall and takes taps across only ${control.taps}px`
          )
          .join('\n')
      );
    } finally {
      await context.close();
    }
  });
});

test(
  'at 390 px every segment of a segmented control takes taps across 44 px',
  runsIfPlaywright,
  async () => {
    await withBrowser(async (browser) => {
      const { context, page } = await openApp(browser, {
        visitor: 'guest',
        language: 'en',
        width: 1440
      });
      const problems = [];
      let found = 0;
      try {
        const t = translator('en');
        await page
          .getByText(t('dashboard.downloadsPanel.emptyStates.noDownloads'))
          .first()
          .waitFor();
        for (const tab of ['dashboard', 'downloads', 'clients']) {
          await showTab(page, tab);
          await showWidth(page, 390);
          const measured = await measureTaps(page.locator('[role="radiogroup"] [role="radio"]'));
          found += measured.length;
          for (const segment of measured.filter((control) => control.taps < 44 - 0.5)) {
            problems.push(
              `on ${tab} the "${segment.name}" segment is ${segment.drawn}px tall and takes taps across only ${segment.taps}px`
            );
          }
          await showWidth(page, 1440);
        }
      } finally {
        await context.close();
      }
      assert.ok(found > 0, 'no segmented control is drawn on the guest pages');
      assert.deepEqual(problems, [], problems.join('\n'));
    });
  }
);

test('every entry of the header Time menu is the same height', runsIfPlaywright, async () => {
  await withBrowser(async (browser) => {
    const problems = [];
    for (const language of ['en', 'zh']) {
      const t = translator(language);
      const { context, page } = await openApp(browser, {
        visitor: 'guest',
        language,
        width: 1440
      });
      try {
        const trigger = page
          .locator('header')
          .getByLabel(t('common.timezoneSelector.title'), { exact: true })
          .filter({ visible: true })
          .first();
        await trigger.waitFor();
        await page.evaluate(settle);
        await trigger.click();
        await page.getByRole('option').first().waitFor();
        await page.evaluate(settle);
        const entries = await page.evaluate(() =>
          [...globalThis.document.querySelectorAll('[role="listbox"] [role="option"]')]
            .filter((element) => element.getClientRects().length > 0)
            .map((element) => ({
              text: element.innerText.replace(/\s+/g, ' ').trim(),
              height: element.getBoundingClientRect().height
            }))
        );
        assert.ok(
          entries.length > 1,
          `in ${language} the Time menu shows ${entries.length} entries`
        );
        const heights = entries.map((entry) => entry.height);
        if (Math.max(...heights) - Math.min(...heights) > 1) {
          problems.push(
            `in ${language} the Time menu entries differ in height: ${entries
              .map((entry) => `"${entry.text}" ${entry.height}px`)
              .join(', ')}`
          );
        }
      } finally {
        await context.close();
      }
    }
    assert.deepEqual(problems, [], problems.join('\n'));
  });
});

test(
  'the sign-in screen and the guest sign-in page name the action the same way as their subtitle',
  runsIfPlaywright,
  async () => {
    // The subtitle on both says "Sign in with your username and password."; the title, the button
    // and the tab must not call the same action "Authenticate".
    const otherVerb = { en: /authenticat/i, zh: /认证|验证身份/ };
    await withBrowser(async (browser) => {
      const problems = [];
      for (const language of ['en', 'zh']) {
        const t = translator(language);
        const subtitle = t('accessSetup.login.password');
        const signedOut = await openApp(browser, { visitor: 'signed-out', language, width: 1440 });
        try {
          await signedOut.page.getByText(subtitle).first().waitFor();
          await signedOut.page.evaluate(settle);
          const words = await signedOut.page.evaluate(() => globalThis.document.body.innerText);
          const misnamed = words.split('\n').filter((line) => otherVerb[language].test(line));
          if (misnamed.length > 0) {
            problems.push(`in ${language} the sign-in screen reads ${JSON.stringify(misnamed)}`);
          }
        } finally {
          await signedOut.context.close();
        }

        const guest = await openApp(browser, { visitor: 'guest', language, width: 1440 });
        try {
          await guest.page.locator('nav button').first().waitFor();
          await showTab(guest.page, 'authenticate');
          await guest.page.getByText(subtitle, { exact: true }).first().waitFor();
          await guest.page.evaluate(settle);
          const words = await guest.page.evaluate(() => {
            const nav = globalThis.document.querySelector('nav').innerText;
            const form = globalThis.document.querySelector('form').innerText;
            return `${nav}\n${form}`;
          });
          const misnamed = words.split('\n').filter((line) => otherVerb[language].test(line));
          if (misnamed.length > 0) {
            problems.push(
              `in ${language} the guest navigation and sign-in form read ${JSON.stringify(misnamed)}`
            );
          }
        } finally {
          await guest.context.close();
        }
      }
      assert.deepEqual(problems, [], problems.join('\n'));
    });
  }
);

/** Runs in the page: the heights of the drawn text inputs and buttons inside the sign-in form. */
const formControlHeights = () => {
  const form = globalThis.document.querySelector('form');
  const drawn = (element) => element.getClientRects().length > 0;
  const height = (element) => element.getBoundingClientRect().height;
  return {
    inputs: [...form.querySelectorAll('input')].filter(drawn).map((element) => ({
      name: element.getAttribute('autocomplete') ?? element.type,
      height: height(element)
    })),
    buttons: [...globalThis.document.querySelectorAll('button')]
      .filter((element) => drawn(element) && element.closest('form, [role="dialog"]') !== null)
      .filter((element) => element.closest('nav, header') === null)
      .map((element) => ({ name: element.innerText.trim(), height: height(element) }))
      .filter((button) => button.name !== '')
  };
};

test(
  'on the sign-in forms the inputs are as tall as the buttons under them',
  runsIfPlaywright,
  async () => {
    await withBrowser(async (browser) => {
      const t = translator('en');
      const subtitle = t('accessSetup.login.password');
      const problems = [];
      const compare = (where, read) => {
        assert.ok(read.inputs.length > 0, `${where}: no input is drawn`);
        assert.ok(read.buttons.length > 0, `${where}: no button is drawn`);
        for (const input of read.inputs) {
          for (const button of read.buttons) {
            if (Math.abs(input.height - button.height) > 0.5) {
              problems.push(
                `${where}: the ${input.name} input is ${input.height}px tall and the "${button.name}" button ${button.height}px`
              );
            }
          }
        }
      };
      for (const width of [1440, 390]) {
        const signedOut = await openApp(browser, { visitor: 'signed-out', language: 'en', width });
        try {
          await signedOut.page.getByText(subtitle).first().waitFor();
          await signedOut.page.evaluate(settle);
          compare(`sign-in screen at ${width}`, await signedOut.page.evaluate(formControlHeights));
        } finally {
          await signedOut.context.close();
        }

        const guest = await openApp(browser, { visitor: 'guest', language: 'en', width: 1440 });
        try {
          await guest.page.locator('nav button').first().waitFor();
          await showTab(guest.page, 'authenticate');
          await guest.page.getByText(subtitle, { exact: true }).first().waitFor();
          await showWidth(guest.page, width);
          compare(`guest sign-in page at ${width}`, await guest.page.evaluate(formControlHeights));
        } finally {
          await guest.context.close();
        }
      }
      assert.deepEqual(problems, [], problems.join('\n'));
    });
  }
);

test(
  'in Password mode the guest sign-in card has no blank column beside its form',
  runsIfPlaywright,
  async () => {
    await withBrowser(async (browser) => {
      const t = translator('en');
      const { context, page } = await openApp(browser, {
        visitor: 'guest',
        language: 'en',
        width: 1440
      });
      try {
        await page.locator('nav button').first().waitFor();
        await showTab(page, 'authenticate');
        await page.getByText(t('accessSetup.login.password'), { exact: true }).first().waitFor();
        await page.evaluate(settle);
        const read = await page.evaluate(() => {
          const form = globalThis.document.querySelector('form');
          const card = form.parentElement;
          const cardBox = card.getBoundingClientRect();
          const formBox = form.getBoundingClientRect();
          const style = globalThis.getComputedStyle(card);
          const inner =
            cardBox.width - parseFloat(style.borderLeftWidth) - parseFloat(style.borderRightWidth);
          return { card: inner, form: formBox.width };
        });
        assert.ok(
          read.form >= read.card - 0.5,
          `the card is ${read.card}px wide inside its border but its form fills only ${read.form}px, ` +
            `leaving ${read.card - read.form}px blank beside it`
        );
      } finally {
        await context.close();
      }
    });
  }
);

test(
  'in Password mode the one-column guest sign-in card keeps its phone header and form spacing on a desktop',
  runsIfPlaywright,
  async () => {
    await withBrowser(async (browser) => {
      const t = translator('en');
      const { context, page } = await openApp(browser, {
        visitor: 'guest',
        language: 'en',
        width: 1440
      });
      try {
        await page.locator('nav button').first().waitFor();
        await showTab(page, 'authenticate');
        await page.getByText(t('accessSetup.login.password'), { exact: true }).first().waitFor();
        await showWidth(page, 390);
        const spacing = () => {
          const form = globalThis.document.querySelector('form');
          const header = form.previousElementSibling;
          const headerStyle = globalThis.getComputedStyle(header);
          return {
            headerPadding: headerStyle.padding,
            headerRule: headerStyle.borderBottomWidth,
            formPadding: globalThis.getComputedStyle(form).padding
          };
        };
        const phone = await page.evaluate(spacing);
        for (const width of [768, 1440]) {
          await showWidth(page, width);
          assert.deepEqual(
            await page.evaluate(spacing),
            phone,
            `at ${width} px the one-column card's header and form spacing differ from 390 px`
          );
        }
      } finally {
        await context.close();
      }
    });
  }
);
