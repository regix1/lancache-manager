import type { GameCacheInfo } from '../types';

/** Returns true when the game has NOT been evicted and occupies disk space. */
export const isActiveGame = (game: GameCacheInfo): boolean =>
  game.is_evicted !== true && game.total_size_bytes > 0;

/** Returns true when the game has NOT been evicted (regardless of size). */
export const isNonEvictedGame = (game: GameCacheInfo): boolean => game.is_evicted !== true;

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
  detectionLookup: Map<number, GameCacheInfo> | null | undefined,
  detectionByName: Map<string, GameCacheInfo> | null | undefined,
  service?: string | null,
  detectionByService?: Map<string, DetectionResult> | null
): DetectionResult | undefined {
  // Primary lookup by numeric app ID (Steam)
  if (gameAppId && detectionLookup) {
    const byId = detectionLookup.get(gameAppId);
    if (byId) return byId;
  }

  // Fallback lookup by game name (all services)
  if (gameName && detectionByName) {
    const byName = detectionByName.get(gameName.toLowerCase());
    if (byName) return byName;
  }

  // Service-level fallback (aggregate disk usage for the whole service)
  if (service && detectionByService) {
    const bySvc = detectionByService.get(service.toLowerCase());
    if (bySvc) return bySvc;
  }

  return undefined;
}
