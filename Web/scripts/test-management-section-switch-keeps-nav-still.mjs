import assert from 'node:assert/strict';
import test from 'node:test';
import { builtAppOrigin, fakeHub, server, settle, withBrowser } from './transpile-module.mjs';

// Opens the built app (run `npm run build` first) in Chromium at 1440 by 900 and at 390 by 844
// with every API answer faked, on the Management page. For every section it scrolls the page to
// its end and picks the next section, then does the same picking the one before, and watches where
// the page header and the section nav sit on screen while the content changes. Runs only with
// PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const answers = {
  '/api/system/config': {
    cachePath: '/cache',
    logsPath: '/logs',
    dataPath: '/data',
    cacheDeleteMode: 'preserve',
    steamAuthMode: 'anonymous',
    timeZone: 'UTC',
    cacheWritable: true,
    logsWritable: true,
    dataSources: [
      {
        name: 'default',
        cachePath: '/cache',
        logsPath: '/logs',
        cacheWritable: true,
        logsWritable: true,
        enabled: true
      }
    ]
  },
  '/api/auth/status': {
    isAuthenticated: true,
    sessionType: 'admin',
    sessionId: 'session',
    authenticationSetupRequired: false,
    guestAccessEnabled: false,
    guestDurationHours: 6,
    authenticationEnabled: false
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
    mode: 'embedded',
    postgresHost: null,
    postgresPort: null,
    postgresDatabase: null,
    postgresUser: null
  },
  '/api/stats/exclusions': { ips: [], rules: [] }
};

/** Opens the Management page at `width` and waits for its section nav. */
const openManagement = async (browser, width) => {
  const context = await browser.newContext({
    viewport: { width, height: width < 640 ? 844 : 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript(() => {
    globalThis.localStorage.setItem('lancache_language', 'en');
    globalThis.localStorage.setItem('management-active-section', 'settings');
  });
  const page = await context.newPage();
  await page.route('**/*', server(answers));
  await fakeHub(page);
  await page.goto(`${builtAppOrigin}/`);
  // The app listens for its tab links once it has mounted, so the link is sent until the page shows.
  await page.waitForFunction(
    () => {
      globalThis.dispatchEvent(
        new globalThis.CustomEvent('navigate-to-tab', { detail: { tab: 'management' } })
      );
      return globalThis.document.querySelector('.management-nav-container') !== null;
    },
    undefined,
    { polling: 250 }
  );
  await page.evaluate(settle);
  return { context, page };
};

/** Runs in the page. The ids of the section tabs a person can pick, in the nav's order. */
const sectionIds = () =>
  [...globalThis.document.querySelectorAll('.management-nav-tab')]
    .filter((tab) => tab.getClientRects().length > 0 && !tab.disabled)
    .map((tab) => tab.id);

/** Runs in the page. Scrolls the page to its end and returns how far down that is. */
const scrollToEnd = () => {
  const target = globalThis.document.scrollingElement;
  target.scrollTop = target.scrollHeight;
  return Math.round(target.scrollTop);
};

/**
 * Runs in the page. Sets up `globalThis.navWatch`: `boxes()` reads where on screen the page header
 * sits and where the section nav starts (its left, top and width, since the phone nav's own text
 * may change its height); `start()` reads them on every frame until `stop()`, each read stamped
 * with its time since start.
 */
const installWatch = () => {
  const box = (element, withHeight) => {
    const rect = element.getBoundingClientRect();
    const sides = withHeight
      ? [rect.left, rect.top, rect.width, rect.height]
      : [rect.left, rect.top, rect.width];
    return sides.map(Math.round).join(',');
  };
  const boxes = () => {
    const read = {};
    const document = globalThis.document;
    const header = document.querySelector('header');
    const nav = document.querySelector('.management-nav-container');
    if (header !== null && header.getClientRects().length > 0) read.header = box(header, true);
    if (nav !== null && nav.getClientRects().length > 0) read['section nav'] = box(nav, false);
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
  globalThis.navWatch = watch;
};

/** Waits until the page's watch has read for `ms` milliseconds since it started. */
const watchedFor = (page, ms) =>
  page.waitForFunction((until) => (globalThis.navWatch.samples.at(-1)?.at ?? 0) >= until, ms, {
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
  'with the Management page scrolled to its end, picking another section keeps the header and section nav where they are on screen',
  { skip: !process.env.PLAYWRIGHT_PATH },
  () =>
    withBrowser(async (browser) => {
      const changes = [];
      const scrolls = [];
      let switches = 0;
      for (const width of [1440, 390]) {
        const { context, page } = await openManagement(browser, width);
        try {
          await page.evaluate(installWatch);
          const ids = await page.evaluate(sectionIds);
          // Each section picked from the one before it in the nav, then from the one after it.
          const legs = [
            ...ids.map((id, index) => [id, ids[(index + 1) % ids.length]]),
            ...ids.map((id, index) => [id, ids[(index - 1 + ids.length) % ids.length]])
          ];
          for (const [from, to] of legs) {
            await page.evaluate((id) => globalThis.document.getElementById(id).click(), from);
            await page.evaluate(settle);
            const scrolled = await page.evaluate(scrollToEnd);
            await page.evaluate(settle);
            scrolls.push(scrolled);
            const before = await page.evaluate(() => globalThis.navWatch.boxes());
            await page.evaluate(() => globalThis.navWatch.start());
            // A click in the page, so the browser does not scroll the tab into view first.
            await page.evaluate((id) => globalThis.document.getElementById(id).click(), to);
            await watchedFor(page, 600);
            await page.evaluate(settle);
            const samples = await page.evaluate(() => globalThis.navWatch.stop());
            switches += 1;
            changes.push(
              ...changesFrom(
                `${from} to ${to} at ${width} wide, ${scrolled}px down`,
                before,
                samples
              )
            );
          }
        } finally {
          await context.close();
        }
      }
      assert.ok(switches > 0, 'no Management section was found to switch between');
      assert.ok(
        scrolls.some((scrolled) => scrolled > 0),
        `the page never scrolled, so the case is not set up: ${scrolls.join(', ')}px`
      );
      assert.deepEqual(
        changes,
        [],
        'with the Management page scrolled to its end, the header or section nav moved on screen during a section switch'
      );
    })
);
