import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { builtAppOrigin, server, settle, translator } from './transpile-module.mjs';

// A person on the Events list switches the app from English to Chinese in the header's language
// menu. The event's start time then reads exactly as it does when the page is opened in Chinese,
// not in the English date form it showed before the switch. Runs only with PLAYWRIGHT_PATH set to
// a playwright-core install whose Chromium is downloaded, against the built app.

const winterLan = {
  id: 7,
  name: 'Winter LAN',
  description: '',
  startTimeUtc: '2026-10-10T10:00:00Z',
  endTimeUtc: '2026-10-11T18:00:00Z',
  colorIndex: 2,
  createdAtUtc: '2026-10-01T09:00:00Z'
};

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
  '/api/events': [winterLan],
  '/api/events/active': []
};

// Opens the Events list in the given language at 1280x900 in UTC and waits for the event's card.
const openEventList = async (browser, language) => {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript((chosen) => {
    globalThis.localStorage.setItem('lancache_language', chosen);
  }, language);
  const page = await context.newPage();
  await page.route('**/*', server(answers));
  await page.goto(`${builtAppOrigin}/`);
  const t = translator(language);
  await page.getByText(t('nav.events'), { exact: true }).first().click();
  await page.getByText(t('events.view.list'), { exact: true }).first().click();
  await page.getByText(winterLan.name, { exact: true }).first().waitFor();
  await page.evaluate(settle);
  return { context, page };
};

// Runs in the page: the text beside each calendar icon, which on the Events list is a start time.
const readStartTimes = () =>
  [...globalThis.document.querySelectorAll('svg.lucide-calendar')]
    .map((icon) => icon.nextElementSibling?.textContent?.trim())
    .filter(Boolean);

test(
  'after switching to Chinese the Events list start time reads as it does when opened in Chinese',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    assert.ok(
      existsSync(new URL('../dist/index.html', import.meta.url)),
      'run npm run build before this test'
    );
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const browser = await chromium.launch({ headless: true });
    try {
      const opened = await openEventList(browser, 'zh');
      let inChinese;
      try {
        inChinese = await opened.page.evaluate(readStartTimes);
      } finally {
        await opened.context.close();
      }
      assert.equal(inChinese.length, 1, 'the Events list opened in Chinese shows one start time');

      const { context, page } = await openEventList(browser, 'en');
      try {
        const inEnglish = await page.evaluate(readStartTimes);
        assert.notDeepEqual(
          inEnglish,
          inChinese,
          'the start time reads the same in both languages'
        );

        await page
          .getByRole('combobox', { name: translator('en')('common.languageSelector.title') })
          .first()
          .click();
        await page.getByRole('option', { name: '中文（简体）' }).first().click();
        await page.getByText(translator('zh')('nav.events'), { exact: true }).first().waitFor();
        await page.evaluate(settle);

        const switched = await page.evaluate(readStartTimes);
        assert.deepEqual(
          switched,
          inChinese,
          `after switching to Chinese the event starts "${switched.join(', ')}", but the page opened in Chinese reads "${inChinese.join(', ')}"`
        );
      } finally {
        await context.close();
      }
    } finally {
      await browser.close();
    }
  }
);
