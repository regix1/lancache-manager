import type { CachedDetectionResponse } from '../contexts/DashboardDataContext/types';
import type { GameDetectionSummary } from '../types';

export const EMPTY_CACHED_DETECTION: CachedDetectionResponse = {
  hasCachedResults: false,
  games: [],
  services: [],
  games_on_disk_bytes: 0,
  games_on_disk_count: 0,
  identified_cache_bytes: 0,
  identified_service_bytes: 0
};

/** Returns true when persisted on-disk totals are present and non-zero. */
function hasOnDiskDetectionData(detection: CachedDetectionResponse | null | undefined): boolean {
  return (
    detection?.hasCachedResults === true &&
    detection.games_on_disk_bytes !== undefined &&
    detection.games_on_disk_bytes > 0
  );
}

/** Games list for on-disk charts — empty when summary totals say nothing is on disk. */
export function getChartGames(
  detection: CachedDetectionResponse | null | undefined
): GameDetectionSummary[] {
  if (!hasOnDiskDetectionData(detection)) {
    return [];
  }

  return detection?.games ?? [];
}

export function buildDetectionLookupMaps(detection: CachedDetectionResponse | null | undefined): {
  byAppId: Map<number, GameDetectionSummary>;
  byName: Map<string, GameDetectionSummary>;
  byService: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  >;
} {
  const byAppId = new Map<number, GameDetectionSummary>();
  const byName = new Map<string, GameDetectionSummary>();
  const byService = new Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  >();

  if (detection?.games) {
    for (const game of detection.games) {
      if (game.game_app_id) {
        byAppId.set(game.game_app_id, game);
      }
      if (game.game_name) {
        byName.set(game.game_name.toLowerCase(), game);
      }
    }
  }

  if (detection?.services) {
    for (const svc of detection.services) {
      if (svc.service_name) {
        byService.set(svc.service_name.toLowerCase(), svc);
      }
    }
  }

  return { byAppId, byName, byService };
}

/** Returns true when the game has NOT been evicted and occupies disk space. */
export const isActiveGame = (game: GameDetectionSummary): boolean =>
  game.is_evicted !== true && game.total_size_bytes > 0;

interface GamesOnDiskDisplayStats {
  totalSize: number;
  gameCount: number;
  includesEvicted: boolean;
  evictedCount: number;
}

/**
 * Maps server-computed games-on-disk aggregates for dashboard display.
 * Totals always come from the API — never re-summed on the client.
 */
export function buildGamesOnDiskDisplayStats(
  detection: CachedDetectionResponse | null | undefined,
  options: { showEvictedBadge?: boolean; evictedCount?: number } = {}
): GamesOnDiskDisplayStats | null {
  if (
    !detection?.hasCachedResults ||
    detection.games_on_disk_bytes === undefined ||
    detection.games_on_disk_count === undefined
  ) {
    return null;
  }

  const evictedCount = options.evictedCount ?? 0;
  const showEvictedBadge = options.showEvictedBadge ?? false;

  return {
    totalSize: detection.games_on_disk_bytes,
    gameCount: detection.games_on_disk_count,
    includesEvicted: showEvictedBadge && evictedCount > 0,
    evictedCount
  };
}

interface DetectionResult {
  total_size_bytes: number;
  cache_files_found: number;
}

/**
 * Resolves detection info using a three-tier lookup:
 * 1. Primary: by numeric gameAppId (reliable for Steam)
 * 2. Fallback: by gameName (works for all services: Epic, Xbox, Blizzard, etc.)
 * 3. Service fallback: by service name (aggregate service-level disk usage)
 */
export function resolveGameDetection(
  gameAppId: number | undefined | null,
  gameName: string | undefined | null,
  detectionLookup: Map<number, GameDetectionSummary> | null | undefined,
  detectionByName: Map<string, GameDetectionSummary> | null | undefined,
  service?: string | null,
  detectionByService?: Map<string, DetectionResult> | null
): DetectionResult | undefined {
  if (gameAppId && detectionLookup) {
    const byId = detectionLookup.get(gameAppId);
    if (byId) return byId;
  }

  if (gameName && detectionByName) {
    const byName = detectionByName.get(gameName.toLowerCase());
    if (byName) return byName;
  }

  if (service && detectionByService) {
    const bySvc = detectionByService.get(service.toLowerCase());
    if (bySvc) return bySvc;
  }

  return undefined;
}
