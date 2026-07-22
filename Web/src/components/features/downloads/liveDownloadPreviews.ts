import type { Download, GameSpeedInfo } from '../../../types';

/**
 * Presentation model for traffic that is visible in the live speed window but has no
 * recorded Download row yet. Deliberately NOT structurally compatible with Download:
 * previews carry no database id and must never enter Download[] collections, recorded
 * totals, pagination, associations, or exports.
 */
export interface LiveDownloadPreview {
  /** Stable client-qualified identity: service|client|<app / depot / name / service tier>. */
  key: string;
  clientIp: string;
  /** Normalized raw service name (lowercase). */
  service: string;
  /** Resolved game title, or the truthful service label for service-only traffic. */
  displayName: string;
  /** False when displayName is only a service label, never an identified game. */
  hasResolvedGame: boolean;
  gameAppId: number | null;
  depotId: number | null;
  bytesPerSecond: number;
  /** Bytes observed in the current rolling window only. Never a session total. */
  windowBytes: number;
  windowSeconds: number;
  requestCount: number;
  cacheHitPercent: number;
  /** Client-clock ms when this identity was first observed live. */
  firstSeenAt: number;
  /** Client-clock ms when this identity was last present in a speed snapshot. */
  lastSeenAt: number;
  status: 'in-progress';
}

interface DownloadFingerprint {
  endTimeUtc: string | null;
  totalBytes: number;
  isActive: boolean;
}

export interface LivePreviewLedgerEntry {
  preview: LiveDownloadPreview;
  /**
   * Recorded rows (by download id) that already matched this identity when it was first
   * observed live. They are stale history, not this traffic: the preview stays visible
   * while only these unchanged rows exist, so an old row cannot hide live activity during
   * a long ingestion pause.
   */
  baselineFingerprints: ReadonlyMap<number, DownloadFingerprint>;
  /** True once an authoritative row represents this traffic; the preview stays hidden. */
  reconciled: boolean;
}

const STEAM_APP_PLACEHOLDER = /^Steam App \d+$/;

// Mirror of the speed tracker's service->label fallback: traffic without a resolved game
// arrives with gameName set to one of these labels, so a name equal to the label (or to the
// raw service) is service-only traffic and must never be treated as a resolved title.
const SERVICE_FALLBACK_LABELS: Record<string, string> = {
  epic: 'Epic Games',
  epicgames: 'Epic Games',
  origin: 'EA / Origin',
  ea: 'EA / Origin',
  blizzard: 'Blizzard / Battle.net',
  battlenet: 'Blizzard / Battle.net',
  'battle.net': 'Blizzard / Battle.net',
  riot: 'Riot Games',
  riotgames: 'Riot Games',
  xbox: 'Xbox Live',
  xboxlive: 'Xbox Live',
  wsus: 'Windows Update',
  windows: 'Windows Update',
  uplay: 'Ubisoft',
  ubisoft: 'Ubisoft',
  arenanet: 'ArenaNet',
  sony: 'PlayStation',
  playstation: 'PlayStation',
  nintendo: 'Nintendo',
  rockstar: 'Rockstar Games',
  wargaming: 'Wargaming',
  steam: 'Steam',
  localhost: 'Localhost',
  'ip-address': 'Direct IP',
  unknown: 'Unknown Service'
};

// Xbox content reaches the cache tagged wsus and is later canonicalized to the xbox
// service. NAMED matches may cross this alias group (same title on either side); generic
// service-only matches never do, so generic wsus can never attach to a named Xbox row.
const XBOX_ALIAS_GROUP = new Set(['wsus', 'xbox', 'xboxlive']);

// Folds service aliases the same way the service filter dropdowns do (xboxlive/microsoft
// display as Xbox). Kept dependency-free so this module stays loadable outside the bundler.
const SERVICE_FILTER_ALIASES: Record<string, string> = {
  xboxlive: 'xbox',
  microsoft: 'xbox'
};

const normalizeService = (service: string | null | undefined): string =>
  (service ?? '').trim().toLowerCase();

const normalizeTitle = (title: string | null | undefined): string =>
  (title ?? '').trim().toLowerCase();

const serviceFilterKey = (service: string): string => {
  const raw = normalizeService(service);
  return SERVICE_FILTER_ALIASES[raw] ?? raw;
};

const isResolvedGameName = (
  gameName: string | null | undefined,
  service: string | null | undefined
): boolean => {
  const name = (gameName ?? '').trim();
  if (!name) return false;
  const normalized = name.toLowerCase();
  const raw = normalizeService(service);
  if (normalized === raw) return false;
  const fallback = SERVICE_FALLBACK_LABELS[raw];
  if (fallback && normalized === fallback.toLowerCase()) return false;
  if (STEAM_APP_PLACEHOLDER.test(name)) return false;
  return true;
};

const previewGameAppId = (game: GameSpeedInfo): number | null =>
  game.gameAppId != null && game.gameAppId > 0 ? game.gameAppId : null;

const previewDepotId = (game: GameSpeedInfo): number | null =>
  previewGameAppId(game) === null && game.depotId > 0 ? game.depotId : null;

// Identity tiers: app id (Steam always keys by app, never by name), then unresolved depot,
// then resolved title for named services, then the service-only bucket. Every tier is
// client-qualified so the same game on two clients yields two previews.
const buildTrafficKey = (game: GameSpeedInfo): string => {
  const service = normalizeService(game.service);
  const client = (game.clientIp ?? '').trim();
  const appId = previewGameAppId(game);
  const depotId = previewDepotId(game);
  let identity: string;
  if (appId !== null) {
    identity = `app:${appId}`;
  } else if (depotId !== null) {
    identity = `depot:${depotId}`;
  } else if (isResolvedGameName(game.gameName, game.service)) {
    identity = `name:${normalizeTitle(game.gameName)}`;
  } else {
    identity = 'service';
  }
  return `${service}|${client}|${identity}`;
};

// Display mirrors the Active tab naming: resolved title first, then the raw reported name,
// then a depot placeholder, then the service label. No game title is ever invented for
// service-only traffic.
const previewDisplayName = (game: GameSpeedInfo, resolved: boolean): string => {
  const name = (game.gameName ?? '').trim();
  if (resolved) return name;
  if (name) return name;
  const depotId = previewDepotId(game);
  if (depotId !== null) return `Depot ${depotId}`;
  const raw = normalizeService(game.service);
  return SERVICE_FALLBACK_LABELS[raw] ?? (game.service ?? '').trim();
};

const parseTimeMs = (value: string | null | undefined): number => {
  const ms = Date.parse(value ?? '');
  return Number.isFinite(ms) ? ms : 0;
};

const fingerprintOf = (download: Download): DownloadFingerprint => ({
  endTimeUtc: download.endTimeUtc,
  totalBytes: download.totalBytes,
  isActive: download.isActive
});

const fingerprintAdvanced = (
  baseline: DownloadFingerprint,
  current: DownloadFingerprint
): boolean =>
  current.totalBytes !== baseline.totalBytes ||
  current.endTimeUtc !== baseline.endTimeUtc ||
  current.isActive !== baseline.isActive;

// A row that is still active, or ended within the freshness horizon, represents current
// traffic and reconciles a newly observed identity immediately (page-navigation bootstrap).
const isFreshDownload = (download: Download, now: number, freshWithinMs: number): boolean =>
  download.isActive || now - parseTimeMs(download.endTimeUtc) <= freshWithinMs;

const servicesCompatibleForNamedMatch = (a: string, b: string): boolean =>
  a === b || (XBOX_ALIAS_GROUP.has(a) && XBOX_ALIAS_GROUP.has(b));

// One-to-one suppression matching, mirroring the identity tiers of the key. Every tier
// requires the same client; generic service-only previews match only another generic row
// of the same raw service.
const matchesPreview = (preview: LiveDownloadPreview, download: Download): boolean => {
  if ((download.clientIp ?? '').trim() !== preview.clientIp) return false;
  const downloadService = normalizeService(download.service);
  if (preview.gameAppId !== null) {
    return (
      download.gameAppId === preview.gameAppId &&
      servicesCompatibleForNamedMatch(preview.service, downloadService)
    );
  }
  if (preview.depotId !== null) {
    return download.depotId === preview.depotId && downloadService === preview.service;
  }
  if (preview.hasResolvedGame) {
    return (
      isResolvedGameName(download.gameName, download.service) &&
      normalizeTitle(download.gameName) === normalizeTitle(preview.displayName) &&
      servicesCompatibleForNamedMatch(preview.service, downloadService)
    );
  }
  return (
    downloadService === preview.service && !isResolvedGameName(download.gameName, download.service)
  );
};

const MAX_STICKY_TTL_MS = 15000;

/**
 * How long a preview lingers after its identity leaves the speed snapshot: at least the
 * server's rolling window (so the adaptive window briefly emptying a row cannot cause
 * flicker), floored at 3s and capped at the tracker's maximum window.
 */
export const computeStickyTtlMs = (windowSeconds: number | null | undefined): number => {
  const windowMs = (windowSeconds || 2) * 1000;
  return Math.min(Math.max(3000, windowMs), MAX_STICKY_TTL_MS);
};

interface ReconcileLivePreviewsArgs {
  gameSpeeds: readonly GameSpeedInfo[];
  windowSeconds: number;
  downloads: readonly Download[];
  ledger: ReadonlyMap<string, LivePreviewLedgerEntry>;
  now: number;
}

interface ReconcileLivePreviewsResult {
  previews: LiveDownloadPreview[];
  ledger: Map<string, LivePreviewLedgerEntry>;
}

/**
 * Derives the unmatched in-progress previews from the current speed snapshot, reconciled
 * against the recorded downloads. Pure: inputs are never mutated and a returned ledger fed
 * back in with the same inputs produces the same result. The ledger is bounded by the
 * currently (or recently, within the sticky TTL) live identities, so it cannot grow
 * indefinitely and previews can never outlive the traffic they describe.
 */
export const reconcileLivePreviews = (
  args: ReconcileLivePreviewsArgs
): ReconcileLivePreviewsResult => {
  const { gameSpeeds, windowSeconds, downloads, ledger, now } = args;
  const stickyMs = computeStickyTtlMs(windowSeconds);
  const nextLedger = new Map<string, LivePreviewLedgerEntry>();

  // Identities present in the current snapshot (entries collapsing to one key are merged).
  const liveByKey = new Map<string, { preview: LiveDownloadPreview; cacheHitBytes: number }>();
  for (const game of gameSpeeds) {
    const key = buildTrafficKey(game);
    const existing = liveByKey.get(key);
    if (existing) {
      existing.preview.bytesPerSecond += game.bytesPerSecond;
      existing.preview.windowBytes += game.totalBytes;
      existing.preview.requestCount += game.requestCount;
      existing.cacheHitBytes += game.cacheHitBytes;
      existing.preview.cacheHitPercent =
        existing.preview.windowBytes > 0
          ? (existing.cacheHitBytes / existing.preview.windowBytes) * 100
          : 0;
      continue;
    }

    const resolved = isResolvedGameName(game.gameName, game.service);
    liveByKey.set(key, {
      cacheHitBytes: game.cacheHitBytes,
      preview: {
        key,
        clientIp: (game.clientIp ?? '').trim(),
        service: normalizeService(game.service),
        displayName: previewDisplayName(game, resolved),
        hasResolvedGame: resolved,
        gameAppId: previewGameAppId(game),
        depotId: previewDepotId(game),
        bytesPerSecond: game.bytesPerSecond,
        windowBytes: game.totalBytes,
        windowSeconds,
        requestCount: game.requestCount,
        cacheHitPercent: game.cacheHitPercent,
        firstSeenAt: now,
        lastSeenAt: now,
        status: 'in-progress'
      }
    });
  }

  // Upsert live identities, carrying first-observation state from the previous ledger.
  for (const [key, { preview }] of liveByKey) {
    const prior = ledger.get(key);
    if (prior) {
      nextLedger.set(key, {
        preview: { ...preview, firstSeenAt: prior.preview.firstSeenAt },
        baselineFingerprints: prior.baselineFingerprints,
        reconciled: prior.reconciled
      });
      continue;
    }

    // First observation: existing matching rows become the stale baseline, unless one is
    // already fresh, which reconciles the identity immediately.
    const baseline = new Map<number, DownloadFingerprint>();
    let reconciled = false;
    for (const download of downloads) {
      if (!matchesPreview(preview, download)) continue;
      baseline.set(download.id, fingerprintOf(download));
      if (isFreshDownload(download, now, stickyMs)) {
        reconciled = true;
      }
    }
    nextLedger.set(key, { preview, baselineFingerprints: baseline, reconciled });
  }

  // Sticky carry-over: identities absent from this snapshot linger until the TTL elapses
  // (unless already reconciled), then drop so nothing becomes an immortal row.
  for (const [key, entry] of ledger) {
    if (nextLedger.has(key)) continue;
    if (now - entry.preview.lastSeenAt <= stickyMs) {
      nextLedger.set(key, { ...entry });
    }
  }

  // Reconcile: a NEW matching row, or a baseline row whose fingerprint advanced, hands the
  // identity over to the authoritative data. Unchanged baseline rows keep the preview
  // visible (heavy-operation ingestion pause).
  for (const entry of nextLedger.values()) {
    if (entry.reconciled) continue;
    for (const download of downloads) {
      if (!matchesPreview(entry.preview, download)) continue;
      const baseline = entry.baselineFingerprints.get(download.id);
      if (!baseline || fingerprintAdvanced(baseline, fingerprintOf(download))) {
        entry.reconciled = true;
        break;
      }
    }
  }

  const previews = Array.from(nextLedger.values())
    .filter((entry) => !entry.reconciled)
    .map((entry) => entry.preview)
    .sort((a, b) => b.bytesPerSecond - a.bytesPerSecond);

  return { previews, ledger: nextLedger };
};

interface LivePreviewFilterArgs {
  /** Folded service filter key ('all' passes everything). */
  serviceFilterKey?: string;
  clientFilter?:
    | { type: 'all' }
    | { type: 'ip'; ip: string }
    | { type: 'group'; memberIps: readonly string[] };
  searchQuery?: string;
  hideLocalhost?: boolean;
  hideUnknownSteam?: boolean;
  hitMissFilter?: 'all' | 'hit' | 'miss';
}

/**
 * Applies only the filters that can honestly evaluate a live preview (client, service,
 * search, localhost, unknown-Steam visibility, and window hit/miss). Session-size and
 * event filters have no honest live equivalent and are intentionally not represented here.
 */
export const filterLivePreviews = (
  previews: readonly LiveDownloadPreview[],
  args: LivePreviewFilterArgs
): LiveDownloadPreview[] => {
  const query = (args.searchQuery ?? '').toLowerCase().trim();

  return previews.filter((preview) => {
    if (
      args.serviceFilterKey &&
      args.serviceFilterKey !== 'all' &&
      serviceFilterKey(preview.service) !== args.serviceFilterKey
    ) {
      return false;
    }

    const clientFilter = args.clientFilter;
    if (clientFilter && clientFilter.type === 'ip' && preview.clientIp !== clientFilter.ip) {
      return false;
    }
    if (
      clientFilter &&
      clientFilter.type === 'group' &&
      !clientFilter.memberIps.includes(preview.clientIp)
    ) {
      return false;
    }

    if (args.hideLocalhost && (preview.clientIp === '127.0.0.1' || preview.clientIp === '::1')) {
      return false;
    }

    if (args.hideUnknownSteam && preview.service === 'steam' && !preview.hasResolvedGame) {
      return false;
    }

    if (args.hitMissFilter === 'hit' && preview.cacheHitPercent < 50) return false;
    if (args.hitMissFilter === 'miss' && preview.cacheHitPercent >= 50) return false;

    if (query) {
      const matchesQuery =
        preview.displayName.toLowerCase().includes(query) ||
        preview.service.includes(query) ||
        preview.clientIp.toLowerCase().includes(query) ||
        (preview.depotId !== null && String(preview.depotId).includes(query)) ||
        (preview.gameAppId !== null && String(preview.gameAppId).includes(query));
      if (!matchesQuery) return false;
    }

    return true;
  });
};
