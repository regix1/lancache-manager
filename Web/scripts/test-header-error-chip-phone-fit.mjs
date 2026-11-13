import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { builtAppOrigin, fakeHub, server, settle, translator } from './transpile-module.mjs';

// The live connection is up but the refresh-rate settings could not be read, so the header shows
// its red "Failed to load" chip, as the words or, at phone widths, as a shorter mark on a control
// named by them. At 390 and 320 px every text in the header stays inside the button
// that holds it, off the other header buttons and on the screen: the chip used to spill out of its
// button under its neighbors and read "LED TO LO". Runs only with PLAYWRIGHT_PATH set to a
// playwright-core install whose Chromium is downloaded, against the built app.

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
  }
};

// Runs in the page: every header text drawn on screen, and for each the ways it leaves its place -
// past the screen's sides, outside the button that holds it, or over another header button. Also
// whether the failed read shows at all: a drawn header element named by `failed` (its aria-label,
// as a phone-width button that shortens to a glyph carries it) or a drawn text holding the words.
const readHeaderText = (failed) => {
  const document = globalThis.document;
  const header = document.querySelector('header');
  if (!header) return null;
  const drawn = (element) => {
    for (let node = element; node && node !== document.body; node = node.parentElement) {
      const style = globalThis.getComputedStyle(node);
      if (style.display === 'none' || style.visibility === 'hidden') return false;
      if (Number(style.opacity) === 0) return false;
    }
    return true;
  };
  const buttons = [...header.querySelectorAll('button')]
    .filter(drawn)
    .map((button) => ({ button, box: button.getBoundingClientRect() }))
    .filter(({ box }) => box.width > 0 && box.height > 0);
  const overlaps = (rect, box) =>
    rect.right > box.left + 0.5 &&
    rect.left < box.right - 0.5 &&
    rect.bottom > box.top + 0.5 &&
    rect.top < box.bottom - 0.5;
  const inside = (rect, box) =>
    rect.left >= box.left - 0.5 &&
    rect.right <= box.right + 0.5 &&
    rect.top >= box.top - 0.5 &&
    rect.bottom <= box.bottom + 0.5;
  const seen = [];
  const misplaced = [];
  const walker = document.createTreeWalker(header, globalThis.NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const text = node.textContent.trim();
    if (!text || !drawn(node.parentElement)) continue;
    const range = document.createRange();
    range.selectNodeContents(node);
    const rects = [...range.getClientRects()].filter((rect) => rect.width > 0 && rect.height > 0);
    if (rects.length === 0) continue;
    seen.push(text);
    const holder = node.parentElement.closest('button');
    const holderBox = holder?.getBoundingClientRect();
    for (const rect of rects) {
      if (rect.left < -0.5 || rect.right > globalThis.innerWidth + 0.5) {
        misplaced.push(`"${text}" runs past the screen's side`);
      }
      if (holderBox && !inside(rect, holderBox)) {
        misplaced.push(
          `"${text}" spills out of its button (text ${Math.round(rect.left)}-${Math.round(rect.right)}, button ${Math.round(holderBox.left)}-${Math.round(holderBox.right)})`
        );
      }
      for (const { button, box } of buttons) {
        if (button !== holder && !button.contains(holder) && overlaps(rect, box)) {
          misplaced.push(`"${text}" lies under another header button`);
        }
      }
    }
  }
  const named = [...header.querySelectorAll('[aria-label]')].some((element) => {
    const box = element.getBoundingClientRect();
    return (
      element.getAttribute('aria-label').trim() === failed &&
      drawn(element) &&
      box.width > 0 &&
      box.height > 0
    );
  });
  const failedShown = named || seen.some((text) => text.includes(failed));
  return { seen, failedShown, misplaced: [...new Set(misplaced)] };
};

test(
  'at 390 and 320 px the header shows its failed-to-load chip inside its own button',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    assert.ok(
      existsSync(new URL('../dist/index.html', import.meta.url)),
      'run npm run build before this test'
    );
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const browser = await chromium.launch({ headless: true });
    const problems = [];
    try {
      const context = await browser.newContext({
        viewport: { width: 1280, height: 900 },
        timezoneId: 'UTC',
        colorScheme: 'dark'
      });
      await context.addInitScript(() => {
        globalThis.localStorage.setItem('lancache_language', 'en');
      });
      const page = await context.newPage();
      // Every API read not answered above fails, the refresh-rate settings among them.
      await page.route('**/*', server(answers));
      // The live connection opens and stays up, so the header reports the failed read.
      await fakeHub(page);
      try {
        await page.goto(`${builtAppOrigin}/`);
        const failed = translator('en')('common.failedToLoad');
        // The failed read reached the header, as its words or as a control named by them.
        await page.waitForFunction(
          (words) =>
            [...globalThis.document.querySelectorAll('header *')].some(
              (element) =>
                element.getAttribute('aria-label')?.trim() === words ||
                element.textContent.trim() === words
            ),
          failed
        );
        for (const width of [390, 320]) {
          await page.setViewportSize({ width, height: 844 });
          await page.waitForFunction((wanted) => globalThis.innerWidth === wanted, width);
          await page.evaluate(settle);
          const read = await page.evaluate(readHeaderText, failed);
          if (!read) {
            problems.push(`${width}px: the page has no header`);
            continue;
          }
          if (!read.failedShown) {
            problems.push(
              `${width}px: the header shows no "${failed}" chip, neither as words nor as a control with that name`
            );
          }
          for (const problem of read.misplaced) problems.push(`${width}px: ${problem}`);
        }
      } finally {
        await context.close();
      }
    } finally {
      await browser.close();
    }
    assert.deepEqual(problems, [], problems.join('\n'));
  }
);
