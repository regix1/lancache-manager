import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { builtAppOrigin, server, settle } from './transpile-module.mjs';

// Opens the built app (run `npm run build` first) in Chromium with every API answer faked, opens the
// Full Scan Required prompt the way the schedules list does (the show-full-scan-modal window event,
// with the depot mapping schedule reporting a pending full scan), and reads the prompt twice: once
// with the Steam Web API able to list apps and once without. The prompt offers two alternatives, the
// GitHub import and the full scan. Each says what it does, only one button is drawn as the primary
// action, and the full-scan choice still shows when the Web API is down, with no button that starts
// it. Runs only with PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const dist = new URL('../dist/', import.meta.url);
const locale = (name) =>
  JSON.parse(readFileSync(new URL(`../src/i18n/locales/${name}.json`, import.meta.url), 'utf8'));

const baseAnswers = {
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
  '/api/system/schedules': [
    {
      key: 'depotMapping',
      intervalHours: 1,
      runOnStartup: false,
      isRunning: false,
      lastRunUtc: null,
      nextRunUtc: null,
      notificationMode: 'manual',
      notificationDisplayMode: 'card',
      notificationDisplayModeOverridden: false,
      supportsNotifications: true,
      pendingFullScan: { changeGap: 9053, estimatedAppsToScan: 2468 }
    }
  ]
};

// What GET /api/steam-api-keys/status answers when the Web API can or cannot list apps.
const webApiStatus = (listsApps) => ({
  version: listsApps ? 'V2' : 'None',
  isV2Available: listsApps,
  isV1Available: false,
  hasApiKey: false,
  isFullyOperational: listsApps,
  message: '',
  lastChecked: '2026-11-11T18:05:16Z',
  canManage: true
});

// Runs in the page. The action buttons are the dialog's labeled buttons other than Cancel. A button
// reads as filled when its background is opaque and differs from Cancel's plain surface. Each
// action's choice is the largest box around it that holds no other action, and its texts are the
// visible texts in that box outside any button.
const readDialog = ({ cancel }) => {
  const document = globalThis.document;
  const dialog = document.querySelector('[role="dialog"]');
  if (!dialog) return { found: false };
  const visible = (element) => element.getClientRects().length > 0;
  const ownText = (element) =>
    [...element.childNodes]
      .filter((node) => node.nodeType === 3)
      .map((node) => node.textContent)
      .join('')
      .trim();
  const buttons = [...dialog.querySelectorAll('button')].filter(visible);
  const cancelButton = buttons.find((element) => element.textContent.trim() === cancel);
  const surface = cancelButton ? globalThis.getComputedStyle(cancelButton).backgroundColor : null;
  const opaque = (color) => !/^rgba\(.*,\s*0\)$/.test(color) && color !== 'transparent';
  const actions = buttons.filter(
    (element) => element !== cancelButton && element.textContent.trim().length > 0
  );
  const texts = (root) =>
    [...root.querySelectorAll('*')]
      .filter((element) => visible(element) && !element.closest('button') && ownText(element))
      .map(ownText);
  return {
    found: true,
    hasCancel: Boolean(cancelButton),
    shownTexts: texts(dialog),
    actions: actions.map((button) => {
      let choice = button;
      while (
        choice.parentElement &&
        choice.parentElement !== dialog &&
        !actions.some((other) => other !== button && choice.parentElement.contains(other))
      ) {
        choice = choice.parentElement;
      }
      const background = globalThis.getComputedStyle(button).backgroundColor;
      return {
        label: button.textContent.trim(),
        disabled: button.disabled,
        filled: surface !== null && opaque(background) && background !== surface,
        choiceTexts: choice === button ? [] : texts(choice)
      };
    })
  };
};

const openPrompt = async (browser, language, listsApps) => {
  const strings = locale(language);
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  await context.addInitScript((chosen) => {
    globalThis.localStorage.setItem('lancache_language', chosen);
  }, language);
  const page = await context.newPage();
  await page.route(
    '**/*',
    server({ ...baseAnswers, '/api/steam-api-keys/status': webApiStatus(listsApps) })
  );
  // The prompt reads the Web API status the app loads at start, so it opens only after that answer.
  const statusAnswered = page.waitForResponse((response) =>
    new URL(response.url()).pathname.startsWith('/api/steam-api-keys/status')
  );
  await page.goto(`${builtAppOrigin}/`);
  await page.getByText(strings.nav.management, { exact: true }).first().waitFor();
  await statusAnswered;
  await page.evaluate(settle);
  await page.evaluate(() => globalThis.dispatchEvent(new Event('show-full-scan-modal')));
  await page.locator('[role="dialog"]').waitFor();
  await page.evaluate(settle);
  const read = await page.evaluate(readDialog, { cancel: strings.common.cancel });
  await context.close();
  return read;
};

test(
  'the full scan prompt offers two described choices with one primary action, and keeps the full scan in view without the Web API',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    assert.ok(existsSync(new URL('index.html', dist)), 'run npm run build before this test');
    const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
    const browser = await chromium.launch({ headless: true });
    const problems = [];
    try {
      for (const language of ['en', 'zh']) {
        const up = await openPrompt(browser, language, true);
        const down = await openPrompt(browser, language, false);
        assert.ok(up.found && down.found, `the Full Scan Required prompt opens in ${language}`);
        if (!up.hasCancel || !down.hasCancel) {
          problems.push(`${language}: the prompt has no Cancel button`);
        }

        const filled = up.actions.filter((action) => action.filled).map((action) => action.label);
        if (filled.length !== 1) {
          problems.push(
            `${language}, Web API up: ${filled.length} filled buttons (${JSON.stringify(filled)}), not one primary action`
          );
        }

        // Without the Web API only the GitHub import can start, so the one enabled action left is it.
        const enabledDown = down.actions.filter((action) => !action.disabled);
        if (enabledDown.length !== 1) {
          problems.push(
            `${language}, Web API down: ${enabledDown.length} enabled actions ` +
              `(${JSON.stringify(enabledDown.map((action) => action.label))}), not the GitHub import alone`
          );
          continue;
        }
        const github = up.actions.find((action) => action.label === enabledDown[0].label);
        const full = up.actions.filter((action) => action !== github && !action.disabled);
        if (!github || full.length !== 1) {
          problems.push(
            `${language}, Web API up: the actions ${JSON.stringify(up.actions.map((action) => action.label))} ` +
              `are not one GitHub import and one full scan`
          );
          continue;
        }
        for (const [name, action] of [
          ['GitHub import', github],
          ['full scan', full[0]]
        ]) {
          if (action.choiceTexts.length === 0) {
            problems.push(
              `${language}, Web API up: the ${name} choice ("${action.label}") is a bare button with no text saying what it does`
            );
          }
        }

        const fullScanName = full[0].choiceTexts[0] ?? full[0].label;
        if (
          !down.shownTexts.includes(fullScanName) &&
          !down.actions.some((action) => action.label === fullScanName)
        ) {
          problems.push(
            `${language}, Web API down: the full-scan choice ("${fullScanName}") is gone from the prompt`
          );
        }
      }
    } finally {
      await browser.close();
    }
    assert.deepEqual(problems, [], problems.join('\n'));
  }
);
