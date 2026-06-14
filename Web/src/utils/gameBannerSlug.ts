/**
 * Name-keyed banner slug + service helpers for Blizzard/Riot games that are identified only by
 * GameName (no Steam appId, no Epic catalog id). MUST stay in lockstep with the backend
 * NameKeyedBannerSource.Slug / NameKeyedBannerSource.NormalizeService so the frontend requests
 * the exact (service, slug) the GameImageFetchService stored under.
 */

type NameKeyedService = 'blizzard' | 'riot';

/**
 * Normalizes a raw service string to the canonical name-keyed service key, or null if the
 * service is not one this banner source covers.
 */
function normalizeNameKeyedService(service: string | null | undefined): NameKeyedService | null {
  if (!service) return null;
  switch (service.toLowerCase()) {
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return 'blizzard';
    case 'riot':
    case 'riotgames':
      return 'riot';
    default:
      return null;
  }
}

/**
 * Produces a stable, URL-safe slug from a GameName: lowercase, non-alphanumeric runs collapsed
 * to single hyphens, leading/trailing hyphens trimmed. Mirrors the C# NameKeyedBannerSource.Slug.
 */
function gameBannerSlug(gameName: string): string {
  return gameName
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

/**
 * Builds the available-images set key for a name-keyed banner, matching the stored GameImage
 * AppId (the slug). Returns null when the game is not a covered name-keyed service or has no name.
 */
export function nameKeyedImageKey(
  service: string | null | undefined,
  gameName: string | null | undefined
): { service: NameKeyedService; slug: string } | null {
  const normalized = normalizeNameKeyedService(service);
  if (!normalized || !gameName || !gameName.trim()) return null;
  return { service: normalized, slug: gameBannerSlug(gameName) };
}
