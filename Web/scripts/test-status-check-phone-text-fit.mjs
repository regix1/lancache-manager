import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { openStatusCheck, readStatusCheckText } from './status-check-page.mjs';
import { settle, translator } from './transpile-module.mjs';

// At 390px and 320px wide, in English and in Chinese, with the failing service open, no text on the
// Status Check page is cut off (by a box that hides its overflow, an ellipsis or a line clamp
// included, or by the screen's sides) and no Latin word is split across two lines. The "From the
// server" heading stays on one line beside the buttons it heads. Runs only with PLAYWRIGHT_PATH set
// to a playwright-core install whose Chromium is downloaded, against the built app.

// Runs in the page: the heading's line count and whether a button of its own heading row shares
// its line.
const readHeading = (label) => {
  const document = globalThis.document;
  const root = document.querySelector('.management-content');
  const walker = document.createTreeWalker(root, globalThis.NodeFilter.SHOW_TEXT);
  let node = walker.nextNode();
  while (node && node.textContent.trim() !== label) node = walker.nextNode();
  if (!node) return { found: false };
  const range = document.createRange();
  range.selectNodeContents(node);
  const rects = [...range.getClientRects()].filter((rect) => rect.width > 0);
  const lines = new Set(rects.map((rect) => Math.round(rect.top))).size;
  const top = Math.min(...rects.map((rect) => rect.top));
  const bottom = Math.max(...rects.map((rect) => rect.bottom));
  let row = node.parentElement;
  const hasButton = (element) =>
    [...element.querySelectorAll('button')].some((button) => button.getClientRects().length > 0);
  while (row && row !== root && !hasButton(row)) row = row.parentElement;
  const beside = [...(row?.querySelectorAll('button') ?? [])].some((button) => {
    const box = button.getBoundingClientRect();
    return box.height > 0 && box.top < bottom && box.bottom > top;
  });
  return { found: true, lines, beside };
};

test(
  'at 320 and 390 px no Status Check text is cut off or splits a word, and the server heading stays on one line',
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
      for (const language of ['en', 'zh']) {
        const { context, page } = await openStatusCheck(browser, {
          language,
          expandFailing: true
        });
        try {
          for (const width of [390, 320]) {
            await page.setViewportSize({ width, height: 844 });
            await page.waitForFunction((wanted) => globalThis.innerWidth === wanted, width);
            await page.evaluate(settle);
            const where = `${width}px in ${language}`;
            const { seen, clipped, broken } = await page.evaluate(readStatusCheckText);
            assert.ok(seen.length > 0, `the Status Check page shows text at ${where}`);
            for (const text of clipped) problems.push(`${where}: "${text}" is cut off`);
            for (const word of broken)
              problems.push(`${where}: the word ${word} splits across two lines`);

            const label = translator(language)('management.sections.statusCheck.serverLane');
            const heading = await page.evaluate(readHeading, label);
            if (!heading.found) {
              problems.push(`${where}: the "${label}" heading is not on the page`);
            } else {
              if (heading.lines !== 1) {
                problems.push(`${where}: the "${label}" heading takes ${heading.lines} lines`);
              }
              if (!heading.beside) {
                problems.push(`${where}: no button of the "${label}" heading row sits beside it`);
              }
            }
          }
        } finally {
          await context.close();
        }
      }
    } finally {
      await browser.close();
    }
    assert.deepEqual(problems, [], problems.join('\n'));
  }
);
