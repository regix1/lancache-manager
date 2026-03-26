import type { GameCacheInfo } from '../types';

/**
 * Resolves a GameCacheInfo entry using a two-tier lookup:
 * 1. Primary: by numeric gameAppId (reliable for Steam)
 * 2. Fallback: by gameName (works for all services: Epic, Xbox, Blizzard, etc.)
 */
export function resolveGameDetection(
  gameAppId: number | undefined | null,
  gameName: string | undefined | null,
  detectionLookup: Map<number, GameCacheInfo> | null | undefined,
  detectionByName: Map<string, GameCacheInfo> | null | undefined
): GameCacheInfo | undefined {
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

  return undefined;
}
