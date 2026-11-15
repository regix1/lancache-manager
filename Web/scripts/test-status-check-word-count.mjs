import assert from 'node:assert/strict';
import test from 'node:test';
import { openStatusCheck, readStatusCheckText } from './status-check-page.mjs';
import { withBrowser } from './transpile-module.mjs';

// The Status Check page after a finished check (six services, one failing) shows far less text than
// it did on 2026-11-11, when the English page at 1280px showed 163 words. The cap of 100 leaves room
// for every label, number, button and service row the page shows today (about 70 words of them)
// plus about 30 words of sentences, where today about 90 words of sentences sit, a 33-word
// paragraph among them. A word is a space-separated piece of seen text holding a Latin letter or a
// digit. Runs only with PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is
// downloaded, against the built app.

const wordCap = 100;

test(
  'the finished Status Check page shows at most 100 words in English',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    await withBrowser(async (browser) => {
      const { context, page } = await openStatusCheck(browser, {
        language: 'en',
        expandFailing: false
      });
      try {
        const { seen } = await page.evaluate(readStatusCheckText);
        const words = seen
          .join(' ')
          .split(/\s+/)
          .filter((piece) => /[A-Za-z0-9]/.test(piece));
        assert.ok(
          words.length <= wordCap,
          `the Status Check page shows ${words.length} words, over the cap of ${wordCap}`
        );
      } finally {
        await context.close();
      }
    });
  }
);
