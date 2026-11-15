import assert from 'node:assert/strict';
import test from 'node:test';
import { readStatusCheckText } from './status-check-page.mjs';
import { builtAppOrigin, server, settle, translator, withBrowser } from './transpile-module.mjs';

// The Status Check page says what happened in a short line, keeps what to do on screen, and puts
// the reason behind a help icon set inline right after the line it explains, with the icon's focus
// outline clear of that text (house rules 32-35). The page is a finished check that shows every
// such message at once: the check used the system resolver, Riot does not resolve, Nintendo has
// two names newly added to the domain list and two names pointing at a public address, Xbox is
// disabled, this browser cannot reach the cache although the server can, and the domain list is
// a cached copy read with a settings file. Checked in English at 1280 and 390 px. On this page,
// with Nintendo, Riot and Origin open, the panel showed 281 words before the change and 232 in the
// mock-up the user picked; the bound below is 240. Runs only with PLAYWRIGHT_PATH set to a
// playwright-core install whose Chromium is downloaded, against the built app.

const wordBound = 240;
// The browser's clock, so the cached domain list reads as fetched 19 hours ago.
const fixedNow = '2026-11-12T12:00:00Z';
const t = translator('en');
const key = (name) => t(`management.sections.statusCheck.${name}`);

const domain = (service, originalEntry, status, overrides = {}) => ({
  domain: originalEntry.replace('*.', 'lancache-check.'),
  originalEntry,
  service,
  status,
  resolvedIps:
    status === 'unresolved'
      ? []
      : status === 'resolved'
        ? ['10.0.0.5']
        : status === 'blocked'
          ? ['0.0.0.0']
          : ['23.10.4.8'],
  expectedIps: ['10.0.0.5'],
  latencyMs: 4,
  heartbeatVerified: status === 'resolved',
  servedBy: status === 'resolved' ? 'lancache' : null,
  edgeProbe: null,
  error: status === 'unresolved' ? 'NXDOMAIN' : null,
  ...overrides
});

const passing = (service, entries, description = service) => ({
  service,
  description,
  status: 'resolved',
  totalCount: entries.length,
  resolvedCount: entries.length,
  domains: entries.map((entry) => domain(service, entry, 'resolved'))
});

const services = [
  passing(
    'steam',
    ['lancache.steamcontent.com', '*.steamcontent.com', 'content1.steampowered.com'],
    'Steam downloads'
  ),
  passing('epicgames', ['epicgames-download1.akamaized.net', 'download.epicgames.com']),
  passing('blizzard', ['*.cdn.blizzard.com', 'level3.blizzard.com']),
  {
    service: 'riot',
    description: 'Riot Games',
    status: 'unresolved',
    totalCount: 1,
    resolvedCount: 0,
    domains: [domain('riot', 'l3cdn.riotgames.com', 'unresolved')]
  },
  passing('wsus', ['*.windowsupdate.com', 'dl.delivery.mp.microsoft.com']),
  {
    service: 'nintendo',
    description: 'Nintendo eShop downloads',
    status: 'partial',
    totalCount: 4,
    resolvedCount: 1,
    domains: [
      domain('nintendo', '*.cdn.nintendo.net', 'resolved'),
      domain('nintendo', '*.hac.p01.eshop.nintendo.net', 'mismatched', {
        addedToListAtUtc: '2026-10-30T18:22:41Z'
      }),
      domain('nintendo', '*.cdn.c.shop.nintendowifi.net', 'unresolved', {
        addedToListAtUtc: '2026-10-30T18:20:05Z'
      }),
      domain('nintendo', 'ccs.cdn.wup.shop.nintendo.net', 'mismatched', { addedToListAtUtc: null })
    ]
  },
  {
    service: 'xboxlive',
    description: 'Xbox Live',
    status: 'disabled',
    totalCount: 2,
    resolvedCount: 0,
    domains: []
  },
  {
    service: 'origin',
    description: 'EA app',
    status: 'resolved',
    totalCount: 2,
    resolvedCount: 1,
    domains: [
      domain('origin', 'origin-a.akamaihd.net', 'resolved', {
        edgeProbe: {
          protocolStatus: 'inconclusive',
          protocolReason: 'noPublicEdges',
          consensusEdges: 0,
          totalPublicEdges: 0,
          edges: []
        }
      }),
      domain('origin', 'telemetry.ea.com', 'blocked')
    ]
  }
];
const domainCount = services.reduce((count, service) => count + service.domains.length, 0);

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
  '/api/status-check': {
    lastResult: {
      startedAtUtc: '2026-11-11T17:30:00Z',
      completedAtUtc: '2026-11-11T17:30:12Z',
      resolverSource: 'system',
      dnsServer: null,
      expectedCacheIps: ['10.0.0.5'],
      expectedIpSource: 'dns',
      heartbeat: { reachable: true, servedBy: 'lancache', cacheIp: '10.0.0.5', error: null },
      services,
      summary: {
        totalServices: services.length,
        resolvedServices: services.filter((service) => service.status === 'resolved').length,
        partialServices: 1,
        unresolvedServices: 1,
        disabledServices: 1,
        unverifiedServices: 0,
        totalDomains: domainCount,
        resolvedDomains: domainCount - 3,
        unverifiedDomains: 0
      },
      avgLatencyMs: 4,
      cacheNodes: [{ servedBy: 'lancache', ips: ['10.0.0.5'] }],
      contentReport: {
        availability: 'available',
        checkedAtUtc: '2026-11-11T17:30:12Z',
        scannedBytes: 52428800,
        scanTruncated: true,
        paths: [
          {
            service: 'steam',
            host: 'cache1-fra1.steamcontent.com',
            pathDisplay: '/depot/731/chunk/0a1b2c',
            cacheEvidence: 'hit',
            cacheObserved: true,
            bytesServed: 1048576,
            httpStatus: 200,
            sampleObservedAtUtc: '2026-11-11T17:20:00Z',
            protocolStatus: 'httpUsable',
            protocolReason: null,
            consensusEdges: 2,
            totalPublicEdges: 2,
            edges: []
          }
        ]
      }
    },
    domainsSource: {
      repoUrl: 'https://github.com/uklans/cache-domains.git',
      branch: 'master',
      envFilePath: '/opt/lancache/docker-compose/lancache-dns/.env',
      envSource: 'envFile',
      noFetch: false,
      fetchedAtUtc: '2026-11-11T17:00:00Z',
      fromCache: true,
      error: null
    },
    isRunning: false,
    operationId: null,
    resolverMode: 'auto'
  },
  '/api/status-check/domains': {
    services: services.map((service) => ({
      name: service.service,
      description: service.description,
      domains: service.domains.map((entry) => entry.originalEntry)
    }))
  }
};

// Each message: the line(s) that say what happened, the line that says what to do where there is
// one, and the reason the help icon after the last of them opens. A line lists the texts it may
// read; the first is the wording of the option the user picked.
const messages = [
  {
    name: 'verdict',
    happened: ['5 of 7 services reach the cache.'],
    reason: key('scopeDescription')
  },
  {
    name: 'facts',
    happened: ['Checked Nov 11, 2026, 17:30; cache 10.0.0.5 answered as lancache.']
  },
  {
    name: 'system resolver',
    todo: ['Set Prefill LancacheDnsIp to confirm these failures.'],
    reason:
      "The check asked this server's system resolver, not your cache DNS, so these failures may come from the resolver."
  },
  {
    name: 'new names',
    happened: [
      '2 names joined the list since Oct 30.',
      '2 names joined the list since Oct 30, 2026.'
    ],
    todo: ['Restart your DNS server to load them.'],
    reason:
      'lancache-dns reads the domain list when it starts. Names added after that are not sent to the cache until it restarts.'
  },
  {
    name: 'wrong address',
    happened: ['23.10.4.8 (public) · 4 ms']
  },
  {
    name: 'this device',
    happened: ['No answer from lancache.steamcontent.com in 4 s.'],
    todo: ['Turn off DNS over HTTPS in this browser.'],
    reason: key('probeResolverMismatch')
  },
  {
    name: 'domain list',
    happened: ['Domain list from uklans/cache-domains@master, cached copy from 19h ago.'],
    reason: 'Settings from /opt/lancache/docker-compose/lancache-dns/.env.'
  }
];

// The long sentences the short lines replace; none of them may still be on screen.
const replaced = [
  'Your DNS is sending',
  key('systemResolverCaveat'),
  'is a public internet address',
  'did not answer from this device within',
  key('probeResolverMismatchShort'),
  'joined the domain list on or after',
  '/opt/lancache/docker-compose/lancache-dns/.env'
];

const plain = (text) => text.replace(/’/g, "'").replace(/\s+/g, ' ').trim();

/**
 * Runs in the page. Finds the deepest shown element in the Status Check page whose whole text is
 * one of `texts`, and the first help icon after that text in the document. Returns where the text
 * and the icon sit, and the icon's index among all help icons on the page.
 */
const readLine = (texts) => {
  const document = globalThis.document;
  const root = document.querySelector('.management-content');
  const normal = (text) => text.replace(/’/g, "'").replace(/\s+/g, ' ').trim();
  const shown = (element) => element.getClientRects().length > 0;
  const element = [...root.querySelectorAll('*')]
    .reverse()
    .find((candidate) => shown(candidate) && texts.includes(normal(candidate.innerText ?? '')));
  if (!element) return { found: false };
  const walker = document.createTreeWalker(element, globalThis.NodeFilter.SHOW_TEXT);
  const textRects = [];
  let firstText = null;
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (!node.textContent.trim() || node.parentElement.closest('button')) continue;
    firstText ??= node;
    const range = document.createRange();
    range.selectNodeContents(node);
    for (const rect of range.getClientRects()) {
      if (rect.width > 0) {
        textRects.push({ left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom });
      }
    }
  }
  if (!firstText) return { found: true, textRects, triggerIndex: -1 };
  const triggers = [...document.querySelectorAll('.help-popover-trigger')];
  const index = triggers.findIndex(
    (trigger) =>
      firstText.compareDocumentPosition(trigger) & globalThis.Node.DOCUMENT_POSITION_FOLLOWING
  );
  return { found: true, textRects, triggerIndex: index };
};

/**
 * Runs in the page with the help icon focused. Returns the icon's box, how far its focus outline
 * and ring reach past that box, and whether the focus came from the keyboard.
 */
const readFocusedIcon = () => {
  const trigger = globalThis.document.activeElement;
  const style = globalThis.getComputedStyle(trigger);
  const outline =
    style.outlineStyle === 'none'
      ? 0
      : parseFloat(style.outlineWidth) + parseFloat(style.outlineOffset);
  // A ring drawn as a box shadow with no offset and no blur reaches as far as its spread.
  const ring = Math.max(
    0,
    ...[...style.boxShadow.matchAll(/(-?[\d.]+)px (-?[\d.]+)px (-?[\d.]+)px (-?[\d.]+)px/g)]
      .filter((match) => Number(match[1]) === 0 && Number(match[2]) === 0)
      .map((match) => Number(match[3]) + Number(match[4]))
  );
  const box = trigger.getBoundingClientRect();
  return {
    isTrigger: trigger.classList.contains('help-popover-trigger'),
    keyboard: trigger.matches(':focus-visible'),
    reach: Math.max(0, outline, ring),
    box: { left: box.left, right: box.right, top: box.top, bottom: box.bottom }
  };
};

const openMixedStatusCheck = async (browser) => {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript(() => {
    globalThis.localStorage.setItem('management-active-section', 'status-check');
    globalThis.localStorage.setItem('lancache_language', 'en');
  });
  const page = await context.newPage();
  await page.clock.setFixedTime(new Date(fixedNow));
  await page.route('**/*', server(answers));
  // The server reached the cache, but this browser cannot.
  await page.route('http://lancache.steamcontent.com/**', (route) => route.abort());
  await page.goto(`${builtAppOrigin}/`);
  await page.getByText(t('nav.management'), { exact: true }).first().click();
  const content = page.locator('.management-content');
  await content.getByText('Nintendo', { exact: true }).first().waitFor();
  for (const [label, marker] of [
    ['Nintendo', 'nintendowifi'],
    ['Riot', 'l3cdn.riotgames.com'],
    ['Origin', 'telemetry.ea.com']
  ]) {
    const isOpen = () =>
      page.evaluate(
        (text) => globalThis.document.querySelector('.management-content').innerText.includes(text),
        marker
      );
    if (!(await isOpen())) {
      await content.getByText(label, { exact: true }).last().click();
      await page.waitForFunction(
        (text) => globalThis.document.querySelector('.management-content').innerText.includes(text),
        marker
      );
    }
  }
  // The device check ends on the unreachable cache, in either wording.
  await page.waitForFunction(() =>
    /lancache\.steamcontent\.com (did not answer|in \d+ s)/.test(
      globalThis.document.querySelector('.management-content').innerText
    )
  );
  await page.mouse.move(1, 1);
  await page.evaluate(settle);
  return { context, page };
};

test(
  'the Status Check page shows short lines, keeps each instruction, and sets each reason behind a help icon right after its line, clear of the text',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    const problems = [];
    await withBrowser(async (browser) => {
      const { context, page } = await openMixedStatusCheck(browser);
      try {
        const words = await page.evaluate(
          () =>
            globalThis.document
              .querySelector('#panel-status-check')
              .innerText.trim()
              .split(/\s+/)
              .filter(Boolean).length
        );
        if (words > wordBound) {
          problems.push(`1280px: the Status Check panel shows ${words} words, over ${wordBound}`);
        }

        for (const width of [1280, 390]) {
          await page.setViewportSize({ width, height: width === 1280 ? 900 : 844 });
          await page.waitForFunction((wanted) => globalThis.innerWidth === wanted, width);
          await page.evaluate(settle);
          const where = `${width}px`;

          const { seen } = await page.evaluate(readStatusCheckText);
          const onScreen = plain(seen.join(' '));
          for (const sentence of replaced) {
            if (onScreen.includes(plain(sentence))) {
              problems.push(`${where}: the long text "${sentence}" is still on screen`);
            }
          }

          for (const message of messages) {
            const lines = [message.happened, message.todo].filter(Boolean);
            let last = null;
            for (const texts of lines) {
              last = await page.evaluate(readLine, texts);
              if (!last.found) {
                problems.push(`${where}, ${message.name}: no line reads "${texts[0]}"`);
              }
            }
            if (!message.reason || !last?.found) continue;
            const line = lines.at(-1)[0];
            if (last.triggerIndex < 0) {
              problems.push(`${where}, ${message.name}: no help icon follows "${line}"`);
              continue;
            }

            const trigger = page.locator('.help-popover-trigger').nth(last.triggerIndex);
            await page.keyboard.press('Shift');
            await trigger.evaluate((element) => element.focus());
            const icon = await page.evaluate(readFocusedIcon);
            if (!icon.isTrigger || !icon.keyboard) {
              problems.push(`${where}, ${message.name}: the help icon did not take keyboard focus`);
            }
            // Read again: the focus may have scrolled the page.
            const { textRects } = await page.evaluate(readLine, lines.at(-1));
            const middle = (icon.box.top + icon.box.bottom) / 2;
            const sameLine = textRects.filter(
              (rect) => rect.top <= middle && rect.bottom >= middle && rect.left < icon.box.left
            );
            const lastLine = textRects.reduce(
              (lowest, rect) => (rect.bottom > lowest.bottom ? rect : lowest),
              textRects[0]
            );
            if (sameLine.length > 0) {
              const textEnd = Math.max(...sameLine.map((rect) => rect.right));
              const outlineStart = icon.box.left - icon.reach;
              if (outlineStart < textEnd - 0.5) {
                problems.push(
                  `${where}, ${message.name}: the help icon's focus outline starts at ${outlineStart.toFixed(1)}px, ` +
                    `over the text "${line}", which ends at ${textEnd.toFixed(1)}px`
                );
              }
              if (icon.box.left - textEnd > 24) {
                problems.push(
                  `${where}, ${message.name}: the help icon sits ${Math.round(icon.box.left - textEnd)}px ` +
                    `after "${line}", not right after it`
                );
              }
            } else if (icon.box.top < lastLine.bottom - 2 || icon.box.top - lastLine.bottom > 8) {
              problems.push(
                `${where}, ${message.name}: the help icon is not on or right below the last line of "${line}"`
              );
            }

            await trigger.click();
            const popover = page.locator('.help-popover').first();
            const shown = await popover
              .waitFor({ timeout: 5000 })
              .then(() => true)
              .catch(() => false);
            if (!shown) {
              problems.push(
                `${where}, ${message.name}: the help icon after "${line}" opened nothing`
              );
              continue;
            }
            const opened = plain(await popover.innerText());
            if (!opened.includes(plain(message.reason))) {
              problems.push(
                `${where}, ${message.name}: the help icon after "${line}" opens "${opened.slice(0, 120)}", ` +
                  `not the reason "${message.reason}"`
              );
            }
            await page.keyboard.press('Escape');
            await popover.waitFor({ state: 'detached', timeout: 5000 });
            await page.mouse.move(1, 1);
            await page.evaluate(settle);
          }
        }
      } finally {
        await context.close();
      }
    });
    assert.deepEqual(problems, [], problems.join('\n'));
  }
);
