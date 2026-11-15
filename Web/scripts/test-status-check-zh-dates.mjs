import assert from 'node:assert/strict';
import test from 'node:test';
import { openStatusCheck, readStatusCheckText } from './status-check-page.mjs';
import { withBrowser } from './transpile-module.mjs';

// In Chinese, the dates on the Status Check page read in the Chinese form: the check finished on
// 2026-11-11 ("Last checked") and two failing Nintendo names were added to the domain list on
// 2026-10-30. Neither may read "Nov 11, 2026" or "Oct 30, 2026". Runs only with PLAYWRIGHT_PATH set
// to a playwright-core install whose Chromium is downloaded, against the built app.

const englishDate =
  /\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4}/;
const chineseDate = (month, day) =>
  new RegExp(
    `2026\\s*(?:年\\s*${month}\\s*月\\s*${day}\\s*日|[/.-]\\s*${month}\\s*[/.-]\\s*${day})`
  );

test(
  'in Chinese the Status Check dates use the Chinese date form',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    const problems = [];
    await withBrowser(async (browser) => {
      const { context, page } = await openStatusCheck(browser, {
        language: 'zh',
        expandFailing: true
      });
      try {
        const { seen } = await page.evaluate(readStatusCheckText);
        for (const text of seen.filter((item) => englishDate.test(item))) {
          problems.push(`"${text}" shows an English date`);
        }
        const pageText = seen.join('\n');
        if (!chineseDate(11, 11).test(pageText)) {
          problems.push('the finished check (2026-11-11) shows no Chinese date');
        }
        if (!chineseDate(10, 30).test(pageText)) {
          problems.push('the names added to the domain list (2026-10-30) show no Chinese date');
        }
      } finally {
        await context.close();
      }
    });
    assert.deepEqual(problems, [], problems.join('\n'));
  }
);
