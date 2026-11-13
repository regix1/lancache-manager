import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { builtAppOrigin, fakeHub, server, translator } from './transpile-module.mjs';

// A user starts a full scan from the Full Scan Required prompt. The scan ends without doing the
// work, so the server still asks for a full scan, and the Schedules row's "show the prompt" button
// must bring the prompt back. The scan's end reaches the page either as its DepotMappingComplete
// event, or not at all because the live connection dropped while the scan ended and the page
// reconnected afterwards (the server does not replay what a reconnecting page missed). In both
// orders the button must open the prompt. Runs only with PLAYWRIGHT_PATH set to a playwright-core
// install whose Chromium is downloaded, against the built app (`npm run build` first).

const t = translator('en');
const promptTitle = t('app.fullScanRequired.title');
const startScan = t('modals.fullScan.full.startButton');
const EndEventArrives = 'the scan end event reaches the page';
const EndMissedDuringReconnect = 'the scan ends while the page reconnects';

const pendingFullScan = { changeGap: 1234, estimatedAppsToScan: 2468 };
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
  // The scan ends without the work done, so the server keeps asking for it, and no depot run runs.
  '/api/system/schedules': [{ key: 'depotMapping', isRunning: false, pendingFullScan }],
  '/api/steam-api-keys/status': {
    isFullyOperational: true,
    canManage: true,
    version: 'V2Active',
    isV2Available: true,
    isV1Available: false,
    hasApiKey: false,
    message: 'Steam Web API V2 is available',
    lastChecked: '2026-11-12T12:00:00Z'
  }
};

/** What the Schedules row's "show the prompt" button sends (SchedulesSection.tsx). */
const showPromptButton = (pending) =>
  globalThis.dispatchEvent(new CustomEvent('show-full-scan-modal', { detail: pending }));

for (const order of [EndEventArrives, EndMissedDuringReconnect]) {
  test(
    `after a full scan from the prompt that ends without the work done (${order}), the Schedules button brings the prompt back`,
    { skip: !process.env.PLAYWRIGHT_PATH },
    async () => {
      assert.ok(
        existsSync(new URL('../dist/index.html', import.meta.url)),
        'run npm run build before this test'
      );
      const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_PATH).href);
      const browser = await chromium.launch({ headless: true });
      try {
        const context = await browser.newContext({
          viewport: { width: 1280, height: 900 },
          timezoneId: 'UTC'
        });
        await context.addInitScript(() =>
          globalThis.localStorage.setItem('lancache_language', 'en')
        );
        const page = await context.newPage();
        await page.route('**/*', server(answers));
        let scanAsked;
        const scanStarted = new Promise((resolve) => {
          scanAsked = resolve;
        });
        await page.route(`${builtAppOrigin}/api/depots/rebuild**`, async (route) => {
          await route.fulfill({
            status: 202,
            json: { started: true, message: 'Depot rebuild started' }
          });
          scanAsked();
        });
        const hub = await fakeHub(page);
        await page.goto(`${builtAppOrigin}/`);
        await hub.handshakesReach(1);

        // The prompt is on screen, and the user starts the full scan from it. The button is pressed
        // again until the page has signed in and listens for it.
        const title = page.getByText(promptTitle, { exact: true });
        const visibleSoon = () =>
          title
            .waitFor({ timeout: 500 })
            .then(() => true)
            .catch(() => false);
        const pressUntilShown = async (presses) => {
          for (let press = 0; press < presses; press += 1) {
            await page.evaluate(showPromptButton, pendingFullScan);
            if (await visibleSoon()) return true;
          }
          return false;
        };
        assert.ok(
          await pressUntilShown(20),
          'the prompt never opened before the scan, so the page did not load'
        );
        await page.getByRole('button', { name: startScan }).click();
        await scanStarted;
        await title.waitFor({ state: 'hidden', timeout: 10_000 });

        if (order === EndEventArrives) {
          hub.send('DepotMappingComplete', {
            operationId: '00000000-0000-0000-0000-000000000001',
            success: false,
            status: 'failed',
            canceled: false,
            error: 'signalr.depotMapping.failed',
            stageKey: 'signalr.depotMapping.failed',
            scanMode: 'full',
            isLoggedOn: false,
            timestamp: '2026-11-12T12:00:00Z'
          });
        } else {
          // The scan ends while the socket is down; its end event goes to no one.
          hub.drop();
          await hub.handshakesReach(2);
        }

        // The user presses the button, and again while nothing shows, for five seconds.
        assert.ok(
          await pressUntilShown(10),
          `after the full scan ended (${order}) and the server still asks for a full scan, the ` +
            "Schedules row's button left the Full Scan Required prompt closed"
        );
        await context.close();
      } finally {
        await browser.close();
      }
    }
  );
}
