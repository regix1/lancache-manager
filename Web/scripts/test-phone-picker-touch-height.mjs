import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { builtAppOrigin, server, settle } from './transpile-module.mjs';

// Opens the built app (run `npm run build` first) in Chromium with every API answer faked, goes to
// Management > Clients in English and in Chinese, and measures the Client Exclusions card at a phone
// width. The client picker sits in the add-clients rows beside an address input and the add
// buttons, which take the 44px phone touch height; the picker must be as tall as they are. The
// services picker inside a prefill rule row stays compact beside its segmented controls, which
// test-prefill-rule-control-heights.mjs asserts, so it is not measured again here. Runs only with
// PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const dist = new URL('../dist/', import.meta.url);
const locale = (name) =>
  JSON.parse(readFileSync(new URL(`../src/i18n/locales/${name}.json`, import.meta.url), 'utf8'));

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
  '/api/stats/exclusions': {
    ips: [],
    rules: [{ ip: '', mode: 'exclude', traffic: 'prefill', services: [] }]
  }
};

// Runs in the page. Each control is found by the text the reader sees on it: the client picker by
// its placeholder, the address input by its placeholder, and the add buttons by their labels.
const measurePickers = ({ selectClients, addIpsPlaceholder, addSelected, add }) => {
  const document = globalThis.document;
  const visible = (element) => element.getClientRects().length > 0;
  const height = (element) => (element ? element.getBoundingClientRect().height : null);
  const buttonWith = (text) =>
    [...document.querySelectorAll('button')].find(
      (element) => visible(element) && element.textContent.trim() === text
    );
  return {
    width: globalThis.innerWidth,
    clientPicker: height(
      [...document.querySelectorAll('button')].find(
        (element) => visible(element) && element.textContent.trim().startsWith(selectClients)
      )
    ),
    input: height(
      [...document.querySelectorAll('input')].find(
        (element) => visible(element) && element.placeholder === addIpsPlaceholder
      )
    ),
    addSelected: height(buttonWith(addSelected)),
    add: height(buttonWith(add))
  };
};

test(
  'at a phone width the client picker is as tall as the touch-sized controls beside it',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    assert.ok(existsSync(new URL('index.html', dist)), 'run npm run build before this test');
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const browser = await chromium.launch({ headless: true });
    try {
      for (const language of ['en', 'zh']) {
        const strings = locale(language);
        const clients = strings.management.sections.clients;
        const labels = {
          selectClients: clients.selectClients,
          addIpsPlaceholder: clients.addIpsPlaceholder,
          addSelected: clients.addSelected,
          add: clients.add
        };
        for (const deviceScaleFactor of [1, 1.1]) {
          const context = await browser.newContext({
            viewport: { width: 1280, height: 800 },
            deviceScaleFactor
          });
          try {
            await context.addInitScript((chosen) => {
              globalThis.localStorage.setItem('management-active-section', 'clients');
              globalThis.localStorage.setItem('lancache_language', chosen);
            }, language);
            const page = await context.newPage();
            await page.route('**/*', server(answers));
            await page.goto(`${builtAppOrigin}/`);
            await page.getByText(strings.nav.management, { exact: true }).first().click();
            await page.getByText(clients.excludeFromStats, { exact: true }).first().click();
            await page.getByText(clients.anyAddress, { exact: true }).first().waitFor();

            await page.setViewportSize({ width: 390, height: 844 });
            await page.waitForFunction(() => globalThis.innerWidth === 390);
            await page.evaluate(settle);
            const measured = await page.evaluate(measurePickers, labels);
            const where = `390x844 in ${language} at scale ${deviceScaleFactor}`;
            assert.equal(measured.width, 390, `viewport width at ${where}`);
            for (const name of ['clientPicker', 'input', 'addSelected', 'add']) {
              assert.ok(measured[name] !== null, `the ${name} control shows at ${where}`);
            }

            assert.ok(
              measured.clientPicker >= 44 - 0.5,
              `at ${where} the client picker is ${measured.clientPicker}px tall, under the 44px ` +
                `touch height of the controls beside it`
            );
            for (const name of ['input', 'addSelected', 'add']) {
              assert.ok(
                Math.abs(measured.clientPicker - measured[name]) < 0.5,
                `at ${where} the client picker is ${measured.clientPicker}px tall but the ` +
                  `${name} control beside it is ${measured[name]}px tall`
              );
            }
          } finally {
            await context.close();
          }
        }
      }
    } finally {
      await browser.close();
    }
  }
);
