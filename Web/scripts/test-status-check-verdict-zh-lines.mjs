import assert from 'node:assert/strict';
import test from 'node:test';
import { openStatusCheck } from './status-check-page.mjs';
import { settle, withBrowser } from './transpile-module.mjs';

// In Chinese at phone widths, the Status Check verdict heading never ends on a line that holds
// fewer than three characters (spaces and punctuation not counted), such as "个。" or "5 个。" left
// alone under the rest of the sentence. Runs only with PLAYWRIGHT_PATH set to a playwright-core
// install whose Chromium is downloaded, against the built app.

// Runs in the page: the verdict heading's text grouped by the line each character is drawn on.
const readVerdictLines = () => {
  const document = globalThis.document;
  const heading = document.querySelector('.status-check-verdict-title');
  if (!heading) return null;
  const lines = new Map();
  const walker = document.createTreeWalker(heading, globalThis.NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    for (let index = 0; index < node.textContent.length; index += 1) {
      const range = document.createRange();
      range.setStart(node, index);
      range.setEnd(node, index + 1);
      const box = [...range.getClientRects()].find((rect) => rect.width > 0);
      if (!box) continue;
      const top = Math.round(box.top);
      lines.set(top, (lines.get(top) ?? '') + node.textContent[index]);
    }
  }
  return [...lines.entries()].sort((a, b) => a[0] - b[0]).map(([, text]) => text);
};

test(
  'in Chinese at 320 to 390 px the Status Check verdict heading leaves no short last line',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    const problems = [];
    await withBrowser(async (browser) => {
      const { context, page } = await openStatusCheck(browser, {
        language: 'zh',
        expandFailing: false
      });
      try {
        for (const width of [390, 360, 320]) {
          await page.setViewportSize({ width, height: 844 });
          await page.waitForFunction((wanted) => globalThis.innerWidth === wanted, width);
          await page.evaluate(settle);
          const lines = await page.evaluate(readVerdictLines);
          if (!lines) {
            problems.push(`${width}px: the verdict heading is not on the page`);
            continue;
          }
          const last = lines.at(-1);
          const counted = last.replace(/[\s\p{P}]/gu, '');
          if (lines.length > 1 && counted.length < 3) {
            problems.push(
              `${width}px: the verdict heading ends on the short line "${last}" (${lines.join(' / ')})`
            );
          }
        }
      } finally {
        await context.close();
      }
    });
    assert.deepEqual(problems, [], problems.join('\n'));
  }
);
