import { builtAppOrigin, server, settle, translator } from './transpile-module.mjs';

/**
 * Opens Management > Status Check in the built app (run `npm run build` first) after a finished
 * check, with every API answer faked: six services, five passing and Nintendo failing three of its
 * four names, two of which were added to the domain list on 2026-10-30. The browser tests of the
 * Status Check page share this page and the text reader below.
 */

const domain = (service, originalEntry, status, overrides = {}) => ({
  domain: originalEntry.replace('*.', 'lancache-check.'),
  originalEntry,
  service,
  status,
  resolvedIps: status === 'unresolved' ? [] : status === 'resolved' ? ['10.0.0.5'] : ['23.10.4.8'],
  expectedIps: ['10.0.0.5'],
  latencyMs: 4,
  heartbeatVerified: status === 'resolved',
  servedBy: status === 'resolved' ? 'lancache' : null,
  edgeProbe: null,
  error: status === 'unresolved' ? 'NXDOMAIN' : null,
  ...overrides
});

const passing = (service, entries) => ({
  service,
  description: service,
  status: 'resolved',
  totalCount: entries.length,
  resolvedCount: entries.length,
  domains: entries.map((entry) => domain(service, entry, 'resolved'))
});

const services = [
  passing('steam', [
    'lancache.steamcontent.com',
    '*.steamcontent.com',
    'content1.steampowered.com'
  ]),
  passing('epicgames', ['epicgames-download1.akamaized.net', 'download.epicgames.com']),
  passing('blizzard', ['*.cdn.blizzard.com', 'level3.blizzard.com']),
  passing('riot', ['l3cdn.riotgames.com']),
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
      resolverSource: 'detected',
      dnsServer: '10.0.0.2',
      expectedCacheIps: ['10.0.0.5'],
      expectedIpSource: 'dns',
      heartbeat: { reachable: true, servedBy: 'lancache', cacheIp: '10.0.0.5', error: null },
      services,
      summary: {
        totalServices: services.length,
        resolvedServices: services.length - 1,
        partialServices: 1,
        unresolvedServices: 0,
        disabledServices: 0,
        unverifiedServices: 0,
        totalDomains: domainCount,
        resolvedDomains: domainCount - 3,
        unverifiedDomains: 0
      },
      avgLatencyMs: 4,
      cacheNodes: [{ servedBy: 'lancache', ips: ['10.0.0.5'] }],
      contentReport: {
        availability: 'noSamples',
        checkedAtUtc: '2026-11-11T17:30:12Z',
        scannedBytes: 1048576,
        scanTruncated: false,
        paths: []
      }
    },
    domainsSource: {
      repoUrl: 'https://github.com/uklans/cache-domains.git',
      branch: 'master',
      envFilePath: null,
      envSource: 'defaults',
      noFetch: false,
      fetchedAtUtc: '2026-11-11T17:00:00Z',
      fromCache: false,
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

/**
 * Opens the page at 1280x900 in UTC, dark, in the given language, waits until the finished check
 * shows, and with `expandFailing` makes sure the failing Nintendo service shows its names (a row
 * that is already open is left open). The caller closes the returned context.
 */
export const openStatusCheck = async (browser, { language, expandFailing }) => {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 900 },
    timezoneId: 'UTC',
    colorScheme: 'dark'
  });
  await context.addInitScript((chosen) => {
    globalThis.localStorage.setItem('management-active-section', 'status-check');
    globalThis.localStorage.setItem('lancache_language', chosen);
  }, language);
  const page = await context.newPage();
  await page.route('**/*', server(answers));
  // The device check fetches the cache's heartbeat host straight from the browser.
  await page.route('http://lancache.steamcontent.com/**', (route) =>
    route.fulfill({
      status: 204,
      headers: {
        'X-LanCache-Processed-By': 'lancache',
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Expose-Headers': 'X-LanCache-Processed-By'
      }
    })
  );
  await page.goto(`${builtAppOrigin}/`);
  await page.getByText(translator(language)('nav.management'), { exact: true }).first().click();
  const content = page.locator('.management-content');
  await content.getByText('Nintendo', { exact: true }).first().waitFor();
  await page.evaluate(settle);
  if (expandFailing) {
    const namesShown = () =>
      page.evaluate(() =>
        globalThis.document.querySelector('.management-content').innerText.includes('nintendowifi')
      );
    if (!(await namesShown())) {
      await content.getByText('Nintendo', { exact: true }).last().click();
      await page.waitForFunction(() =>
        globalThis.document.querySelector('.management-content').innerText.includes('nintendowifi')
      );
    }
    await page.evaluate(settle);
  }
  return { context, page };
};

/**
 * Runs in the page through `page.evaluate(readStatusCheckText)`. Walks every text the reader can see
 * on the Status Check page (inside `.management-content`). A text counts as seen when part of it
 * lies inside every box that clips it; text wholly inside a collapsed or hidden box is skipped.
 * Returns the seen texts, the texts cut off by a clipping box or the viewport's sides, and the Latin
 * words drawn across two lines.
 */
export const readStatusCheckText = () => {
  const document = globalThis.document;
  const root = document.querySelector('.management-content');
  const clipsAxis = (style, axis) => ['hidden', 'clip'].includes(style[`overflow${axis}`]);
  const clippers = (element) => {
    const boxes = [];
    for (let node = element; node && node !== document.documentElement; node = node.parentElement) {
      const style = globalThis.getComputedStyle(node);
      if (style.visibility === 'hidden' || Number(style.opacity) === 0) return null;
      const x = clipsAxis(style, 'X');
      const y = clipsAxis(style, 'Y');
      if (x || y) boxes.push({ box: node.getBoundingClientRect(), x, y });
    }
    return boxes;
  };
  const seen = [];
  const clipped = [];
  const broken = [];
  const walker = document.createTreeWalker(root, globalThis.NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const text = node.textContent.trim();
    if (!text) continue;
    const boxes = clippers(node.parentElement);
    if (!boxes) continue;
    const range = document.createRange();
    range.selectNodeContents(node);
    const rects = [...range.getClientRects()].filter((rect) => rect.width > 0 && rect.height > 0);
    const inside = (rect, { box, x, y }) =>
      (!x || (rect.right > box.left + 0.5 && rect.left < box.right - 0.5)) &&
      (!y || (rect.bottom > box.top + 0.5 && rect.top < box.bottom - 0.5));
    const shown = rects.filter((rect) => boxes.every((clip) => inside(rect, clip)));
    if (shown.length === 0) continue;
    seen.push(text);
    const cut = shown.some(
      (rect) =>
        rect.left < -0.5 ||
        rect.right > globalThis.innerWidth + 0.5 ||
        boxes.some(
          ({ box, x, y }) =>
            (x && (rect.left < box.left - 0.5 || rect.right > box.right + 0.5)) ||
            (y && (rect.top < box.top - 0.5 || rect.bottom > box.bottom + 0.5))
        )
    );
    if (cut) clipped.push(text);
    for (const match of node.textContent.matchAll(/[A-Za-z0-9]+/g)) {
      const word = document.createRange();
      word.setStart(node, match.index);
      word.setEnd(node, match.index + match[0].length);
      const lines = new Set(
        [...word.getClientRects()]
          .filter((rect) => rect.width > 0)
          .map((rect) => Math.round(rect.top))
      );
      if (lines.size > 1) broken.push(`${match[0]} (in "${text.slice(0, 60)}")`);
    }
  }
  return { seen, clipped, broken };
};
