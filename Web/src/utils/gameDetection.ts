import type { CachedDetectionResponse } from '../contexts/DashboardDataContext/types';
import type { GameDetectionSummary } from '../types';

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
