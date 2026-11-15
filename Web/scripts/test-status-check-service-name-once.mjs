import assert from 'node:assert/strict';
import test from 'node:test';
import { openStatusCheck } from './status-check-page.mjs';
import { withBrowser } from './transpile-module.mjs';

// An open Status Check service shows its name once, in its heading: the description under it reads
// "Nintendo eShop downloads", not "Nintendo: Nintendo eShop downloads". Runs only with
// PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded, against the built app.

test(
  'an open Status Check service shows its description without repeating its name',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    await withBrowser(async (browser) => {
      const { context, page } = await openStatusCheck(browser, {
        language: 'en',
        expandFailing: true
      });
      try {
        const shown = await page.evaluate(
          () => globalThis.document.querySelector('.management-content').innerText
        );
        assert.ok(
          shown.includes('Nintendo eShop downloads'),
          'the open Nintendo service shows its description'
        );
        const repeated = shown.match(/Nintendo\s*:\s*Nintendo eShop downloads/);
        assert.equal(
          repeated,
          null,
          `the open Nintendo service repeats its name before its description: "${repeated?.[0]}"`
        );
      } finally {
        await context.close();
      }
    });
  }
);
