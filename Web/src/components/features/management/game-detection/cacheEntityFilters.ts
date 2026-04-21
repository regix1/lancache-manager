import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';

export const FULL_REMOVAL_REFRESH_DELAY_MS = 30_000;

const isEvictedGame = (game: GameCacheInfo) =>
  (game.evicted_downloads_count ?? 0) > 0 || game.is_evicted === true;

const isEvictedService = (service: ServiceCacheInfo) =>
  (service.evicted_downloads_count ?? 0) > 0 || service.is_evicted === true;

export const getEvictedGames = (games?: GameCacheInfo[]) => (games ?? []).filter(isEvictedGame);

export const getEvictedServices = (services?: ServiceCacheInfo[]) =>
  (services ?? []).filter(isEvictedService);

export const getActiveGames = (games: GameCacheInfo[]) =>
  games.filter((game) => !game.is_evicted && (game.cache_files_found ?? 0) > 0);

export const getActiveServices = (services: ServiceCacheInfo[]) =>
  services.filter((service) => !service.is_evicted && (service.cache_files_found ?? 0) > 0);
