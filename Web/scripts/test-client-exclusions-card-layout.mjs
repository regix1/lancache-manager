import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { builtAppOrigin, server, settle } from './transpile-module.mjs';

// Opens the built app (run `npm run build` first) in Chromium with every API answer faked, goes
// to Management > Clients, opens the Client Exclusions card holding one prefill rule for any
// address, and measures what the user sees. Runs only with PLAYWRIGHT_PATH set to a
// playwright-core install whose Chromium is downloaded.

const dist = new URL('../dist/', import.meta.url);
const english = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
);
const clients = english.management.sections.clients;

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

// Runs in the page. The card is the smallest bordered box holding both the card title and the
// "Any address" rule; the rule row is the smallest box around that text that holds a radio group.
const measureCard = ({ title, anyAddress, buttonLabels }) => {
  const document = globalThis.document;
  const textElement = (text) =>
    [...document.querySelectorAll('body *')].find(
      (element) =>
        element.children.length === 0 && element.textContent.trim() === text && element.offsetParent
    );
  const titleElement = textElement(title);
  const addressElement = textElement(anyAddress);
  if (!titleElement || !addressElement) return { found: false };
  let card = titleElement.parentElement;
  while (
    card &&
    !(
      card.contains(addressElement) &&
      parseFloat(globalThis.getComputedStyle(card).borderTopWidth) > 0
    )
  ) {
    card = card.parentElement;
  }
  let row = addressElement.parentElement;
  while (row && !row.querySelector('[role="radiogroup"]')) row = row.parentElement;
  if (!card || !row) return { found: false };
  const box = (element) => {
    const rect = element.getBoundingClientRect();
    return { left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom };
  };
  const buttons = [...card.querySelectorAll('button')];
  return {
    found: true,
    width: globalThis.innerWidth,
    card: box(card),
    buttons: buttonLabels.map((label) => {
      const button = buttons.find((element) => element.textContent.trim() === label);
      if (!button) return { label, missing: true };
      const rect = button.getBoundingClientRect();
      return { label, left: rect.left, width: rect.width };
    }),
    controls: [...row.querySelectorAll('button, input, select, [role="radiogroup"]')]
      .filter((element) => element.getClientRects().length > 0)
      .map((element) => ({
        name:
          element.getAttribute('aria-label') ||
          element.textContent.trim() ||
          element.getAttribute('role') ||
          element.tagName,
        ...box(element)
      }))
  };
};

const openCard = async (browser, deviceScaleFactor) => {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    deviceScaleFactor
  });
  await context.addInitScript(() => {
    globalThis.localStorage.setItem('management-active-section', 'clients');
  });
  const page = await context.newPage();
  await page.route('**/*', server(answers));
  await page.goto(`${builtAppOrigin}/`);
  await page.getByText('Management', { exact: true }).first().click();
  await page.getByText(clients.excludeFromStats, { exact: true }).first().click();
  await page.getByText(clients.anyAddress, { exact: true }).first().waitFor();
  return { context, page };
};

test(
  'the client exclusions card keeps its add buttons equal and its prefill rule inside the card',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    assert.ok(existsSync(new URL('index.html', dist)), 'run npm run build before this test');
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const browser = await chromium.launch({ headless: true });
    const labels = {
      title: clients.excludeFromStats,
      anyAddress: clients.anyAddress,
      buttonLabels: [clients.addSelected, clients.add, clients.addPrefillRule]
    };
    try {
      for (const deviceScaleFactor of [1, 1.1]) {
        const { context, page } = await openCard(browser, deviceScaleFactor);
        try {
          for (const viewport of [
            { width: 1280, height: 800 },
            { width: 390, height: 844 }
          ]) {
            await page.setViewportSize(viewport);
            await page.waitForFunction((width) => globalThis.innerWidth === width, viewport.width);
            await page.evaluate(settle);
            const measured = await page.evaluate(measureCard, labels);
            const where = `${viewport.width}x${viewport.height} at scale ${deviceScaleFactor}`;
            assert.ok(measured.found, `the Client Exclusions card and its rule show at ${where}`);
            assert.equal(measured.width, viewport.width, `viewport width at ${where}`);

            for (const button of measured.buttons) {
              assert.ok(!button.missing, `the "${button.label}" button shows at ${where}`);
            }
            const [first, ...others] = measured.buttons;
            for (const button of others) {
              assert.ok(
                Math.abs(button.width - first.width) < 0.5,
                `at ${where} "${button.label}" is ${button.width}px wide but ` +
                  `"${first.label}" is ${first.width}px wide`
              );
              assert.ok(
                Math.abs(button.left - first.left) < 0.5,
                `at ${where} "${button.label}" starts at x=${button.left} but ` +
                  `"${first.label}" starts at x=${first.left}`
              );
            }

            if (viewport.width === 390) {
              assert.ok(
                measured.controls.length > 0,
                `the prefill rule row has controls at ${where}`
              );
              for (const control of measured.controls) {
                assert.ok(
                  control.left >= measured.card.left - 0.5 &&
                    control.right <= measured.card.right + 0.5 &&
                    control.top >= measured.card.top - 0.5 &&
                    control.bottom <= measured.card.bottom + 0.5,
                  `at ${where} the prefill rule control "${control.name}" spans ` +
                    `x=${control.left}..${control.right}, y=${control.top}..${control.bottom} ` +
                    `but the card spans x=${measured.card.left}..${measured.card.right}, ` +
                    `y=${measured.card.top}..${measured.card.bottom}`
                );
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
  }
);
