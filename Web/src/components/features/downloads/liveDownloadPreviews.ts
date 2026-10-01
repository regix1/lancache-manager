import type { Download, GameSpeedInfo } from '../../../types';
import { getServiceFilterKey } from '../../../utils/serviceDisplayName.ts';

/**
 * Presentation model for current traffic that has no recorded Download row for the same inferred
 * session. It has no database identity and never enters recorded totals, paging, associations, or
 * exports.
 */
export interface LiveDownloadPreview {
  key: string;
  clientIp: string;
  service: string;
  displayName: string;
  gameName: string | null | undefined;
  displayNameKey: string | null;
  hasResolvedGame: boolean;
  gameAppId: number | null;
  depotId: number | null;
  datasources: string[];
  firstSeenUtc: string;
  bytesPerSecond: number;
  windowBytes: number;
  windowSeconds: number;
  requestCount: number;
  cacheHitPercent: number;
  status: 'in-progress';
}

const STEAM_APP_PLACEHOLDER = /^Steam App \d+$/;

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

const SERVICE_LABEL_KEYS: Record<string, string> = {
  'ip-address': 'downloads.services.directIp',
  unknown: 'downloads.services.unknown'
};

const XBOX_ALIAS_GROUP = new Set(['wsus', 'xbox', 'xboxlive']);

const normalizeService = (service: string | null | undefined): string =>
  (service ?? '').trim().toLowerCase();

const normalizeTitle = (title: string | null | undefined): string =>
  (title ?? '').trim().toLowerCase();

export const isResolvedGameName = (
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

export const getGameDisplayName = (
  gameName: string | null | undefined,
  service: string,
  emptyName: string
): string => {
  const normalizedName = normalizeTitle(gameName);
  const raw = normalizeService(service);
  const serviceKey = getServiceFilterKey(raw);

  if (SERVICE_LABEL_KEYS[raw]) return normalizedName ? gameName! : emptyName;
  if (normalizedName && STEAM_APP_PLACEHOLDER.test(gameName!.trim())) return gameName!;

  if (!normalizedName) {
    if (!raw) return emptyName;
    if (raw === 'steam') return normalizeTitle(emptyName) === 'steam' ? 'steam' : emptyName;
    return serviceKey;
  }

  if (
    serviceKey === 'xbox' &&
    (getServiceFilterKey(normalizedName) === 'xbox' ||
      normalizedName === normalizeTitle(SERVICE_FALLBACK_LABELS.xboxlive))
  ) {
    return 'xbox';
  }

  return isResolvedGameName(gameName, service) ? gameName! : serviceKey;
};

const previewGameAppId = (game: GameSpeedInfo): number | null =>
  game.gameAppId != null && game.gameAppId > 0 ? game.gameAppId : null;

const previewDepotId = (game: GameSpeedInfo): number | null =>
  previewGameAppId(game) === null && game.depotId > 0 ? game.depotId : null;

/** The server validates and publishes the stable client-qualified identity. */
export const buildTrafficKey = (game: GameSpeedInfo): string => game.key;

const previewDisplayName = (
  game: GameSpeedInfo
): { displayName: string; displayNameKey: string | null } => {
  const name = (game.gameName ?? '').trim();
  const displayName = getGameDisplayName(name, game.service, '');
  if (displayName) return { displayName, displayNameKey: null };
  const depotId = previewDepotId(game);
  if (depotId !== null) {
    return { displayName: `Depot ${depotId}`, displayNameKey: 'downloads.active.depotLabel' };
  }
  const raw = normalizeService(game.service);
  return {
    displayName: getGameDisplayName(
      SERVICE_FALLBACK_LABELS[raw],
      game.service,
      game.service.trim()
    ),
    displayNameKey: SERVICE_LABEL_KEYS[raw] ?? null
  };
};

const servicesCompatibleForNamedMatch = (left: string, right: string): boolean =>
  left === right || (XBOX_ALIAS_GROUP.has(left) && XBOX_ALIAS_GROUP.has(right));

const matchesPreview = (preview: LiveDownloadPreview, download: Download): boolean => {
  if (download.clientIp.trim() !== preview.clientIp) return false;
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
      normalizeTitle(download.gameName) === normalizeTitle(preview.gameName) &&
      servicesCompatibleForNamedMatch(preview.service, downloadService)
    );
  }
  return (
    downloadService === preview.service && !isResolvedGameName(download.gameName, download.service)
  );
};

const representsSession = (preview: LiveDownloadPreview, download: Download): boolean => {
  if (!matchesPreview(preview, download) || !download.datasource) return false;
  const datasource = download.datasource.trim().toLowerCase();
  if (!preview.datasources.some((alias) => alias.toLowerCase() === datasource)) return false;

  const startMs = Date.parse(download.startTimeUtc);
  const endMs = Date.parse(download.endTimeUtc ?? '');
  const latestMs = Math.max(
    Number.isFinite(startMs) ? startMs : Number.NEGATIVE_INFINITY,
    Number.isFinite(endMs) ? endMs : Number.NEGATIVE_INFINITY
  );
  return latestMs >= Date.parse(preview.firstSeenUtc);
};

const toPreview = (game: GameSpeedInfo, windowSeconds: number): LiveDownloadPreview => {
  const display = previewDisplayName(game);
  return {
    key: buildTrafficKey(game),
    clientIp: game.clientIp.trim(),
    service: normalizeService(game.service),
    displayName: display.displayName,
    gameName: game.gameName,
    displayNameKey: display.displayNameKey,
    hasResolvedGame: isResolvedGameName(game.gameName, game.service),
    gameAppId: previewGameAppId(game),
    depotId: previewDepotId(game),
    datasources: Array.from(
      new Set(game.sources.flatMap((source) => source.datasources.map((name) => name.trim())))
    ),
    firstSeenUtc: game.firstSeenUtc,
    bytesPerSecond: game.bytesPerSecond,
    windowBytes: game.totalBytes,
    windowSeconds,
    requestCount: game.requestCount,
    cacheHitPercent: game.cacheHitPercent,
    status: 'in-progress'
  };
};

interface ReconcileLivePreviewsArgs {
  gameSpeeds: readonly GameSpeedInfo[];
  windowSeconds: number;
  downloads: readonly Download[];
}

/**
 * Projects the current server-owned sessions that recorded rows have not taken over. The result has
 * no browser clock, lease, or carry-over state; an identity disappears in the same render as the
 * accepted server snapshot that removes it.
 */
export const reconcileLivePreviews = (args: ReconcileLivePreviewsArgs): LiveDownloadPreview[] =>
  args.gameSpeeds
    .map((game) => toPreview(game, args.windowSeconds))
    .filter((preview) => !args.downloads.some((download) => representsSession(preview, download)))
    .sort(
      (left, right) =>
        left.service.localeCompare(right.service) ||
        left.displayName.localeCompare(right.displayName) ||
        left.clientIp.localeCompare(right.clientIp) ||
        left.key.localeCompare(right.key)
    );

interface LivePreviewFilterArgs {
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

export const filterLivePreviews = (
  previews: readonly LiveDownloadPreview[],
  args: LivePreviewFilterArgs
): LiveDownloadPreview[] => {
  const query = (args.searchQuery ?? '').toLowerCase().trim();

  return previews.filter((preview) => {
    if (
      args.serviceFilterKey &&
      args.serviceFilterKey !== 'all' &&
      getServiceFilterKey(preview.service) !== args.serviceFilterKey
    ) {
      return false;
    }

    const clientFilter = args.clientFilter;
    if (clientFilter?.type === 'ip' && preview.clientIp !== clientFilter.ip) return false;
    if (clientFilter?.type === 'group' && !clientFilter.memberIps.includes(preview.clientIp)) {
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
        (preview.gameName?.toLowerCase().includes(query) ?? false) ||
        preview.service.includes(query) ||
        preview.clientIp.toLowerCase().includes(query) ||
        (preview.depotId !== null && String(preview.depotId).includes(query)) ||
        (preview.gameAppId !== null && String(preview.gameAppId).includes(query));
      if (!matchesQuery) return false;
    }

    return true;
  });
};
