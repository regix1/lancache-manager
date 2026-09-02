import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';

export const FULL_REMOVAL_REFRESH_DELAY_MS = 500;

// True for a partial eviction (some downloads evicted, cache files still present) as well as a
// full one. Games and services share this predicate because eviction is a lifecycle state that
// means the same thing on both: the first removal step completed and the row is retained until
// the user finalizes it. The active filters below deliberately do not share, because those turn
// on cache-path attribution, which treats the two kinds differently.
const hasEvictedContent = (entity: { is_evicted?: boolean; evicted_downloads_count?: number }) =>
  (entity.evicted_downloads_count ?? 0) > 0 || entity.is_evicted === true;

export const getEvictedGames = (games?: GameCacheInfo[]) => (games ?? []).filter(hasEvictedContent);

export const getEvictedServices = (services?: ServiceCacheInfo[]) =>
  (services ?? []).filter(hasEvictedContent);

// No byte test here, deliberately, unlike getActiveServices below: a game that contributed zero
// bytes still has its cache files on disk under a sibling entity that shares them, so it remains a
// title the user can select and remove and must stay listed.
export const getActiveGames = (games: GameCacheInfo[]) =>
  games.filter((game) => !game.is_evicted && game.cache_files_found > 0);

// A service keeps its scan-time file count even when cache-path attribution awards every one of
// those files to a game that claimed them first, leaving it with zero bytes. Unlike a game above,
// a service row is only the residual of what reached no game bucket, so an emptied residual has
// nothing behind it and a file-count-only test would list it as unmapped content on a card
// reading 0 B.
export const getActiveServices = (services: ServiceCacheInfo[]) =>
  services.filter(
    (service) =>
      !service.is_evicted && service.cache_files_found > 0 && service.total_size_bytes > 0
  );
