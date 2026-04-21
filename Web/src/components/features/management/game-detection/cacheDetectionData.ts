import ApiService from '@services/api.service';
import type { UnifiedNotification } from '@contexts/notifications/types';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';

interface CachedDetectionSnapshot {
  hasCachedResults: boolean;
  games: GameCacheInfo[];
  services: ServiceCacheInfo[];
  lastDetectionTime: string | null;
  totalGamesDetected: number;
  totalServicesDetected: number;
}

export interface CacheRemovalTarget {
  gameAppId?: number;
  gameName?: string;
  epicAppId?: string;
  serviceName?: string;
}

export const CACHED_DETECTION_RELOAD_DELAY_MS = 500;
export const LOADED_RESULTS_SESSION_KEY = 'gameCacheDetector_loadedNotificationShown';

export const loadCachedDetectionSnapshot = async (): Promise<CachedDetectionSnapshot> => {
  const result = await ApiService.getCachedGameDetection();

  return {
    hasCachedResults: result.hasCachedResults,
    games: result.games ?? [],
    services: result.services ?? [],
    lastDetectionTime: result.lastDetectionTime ?? null,
    totalGamesDetected: result.totalGamesDetected ?? 0,
    totalServicesDetected: result.totalServicesDetected ?? 0
  };
};

export const buildLoadedResultsSummary = (snapshot: CachedDetectionSnapshot): string | null => {
  const parts: string[] = [];

  if (snapshot.totalGamesDetected > 0) {
    parts.push(
      `${snapshot.totalGamesDetected} game${snapshot.totalGamesDetected !== 1 ? 's' : ''}`
    );
  }

  if (snapshot.totalServicesDetected > 0) {
    parts.push(
      `${snapshot.totalServicesDetected} service${snapshot.totalServicesDetected !== 1 ? 's' : ''}`
    );
  }

  return parts.length > 0 ? parts.join(' and ') : null;
};

export const pruneGamesByCompletedRemovalNotifications = (
  games: GameCacheInfo[],
  notifications: UnifiedNotification[]
): GameCacheInfo[] => {
  const removedAppIds = new Set<number>();
  const removedNames = new Set<string>();

  for (const notification of notifications) {
    if (notification.type !== 'game_removal' || notification.status !== 'completed') {
      continue;
    }

    const gameAppId = notification.details?.gameAppId;
    const gameName = notification.details?.gameName;

    if (typeof gameAppId === 'number') {
      removedAppIds.add(gameAppId);
    }

    if (typeof gameName === 'string' && gameName.length > 0) {
      removedNames.add(gameName);
    }
  }

  if (removedAppIds.size === 0 && removedNames.size === 0) {
    return games;
  }

  const nextGames = games.filter((game) => {
    if (removedAppIds.has(game.game_app_id)) {
      return false;
    }

    return !(game.game_name && removedNames.has(game.game_name));
  });

  return nextGames.length === games.length ? games : nextGames;
};

export const pruneServicesByCompletedRemovalNotifications = (
  services: ServiceCacheInfo[],
  notifications: UnifiedNotification[]
): ServiceCacheInfo[] => {
  const removedNames = new Set<string>();

  for (const notification of notifications) {
    if (notification.type !== 'service_removal' || notification.status !== 'completed') {
      continue;
    }

    const serviceName = notification.details?.service;
    if (typeof serviceName === 'string' && serviceName.length > 0) {
      removedNames.add(serviceName);
    }
  }

  if (removedNames.size === 0) {
    return services;
  }

  const nextServices = services.filter((service) => !removedNames.has(service.service_name));
  return nextServices.length === services.length ? services : nextServices;
};

export const pruneGamesByRemovalTarget = (
  games: GameCacheInfo[],
  target: CacheRemovalTarget | null
): GameCacheInfo[] => {
  if (!target) {
    return games;
  }

  const nextGames = games.filter((game) => {
    if (typeof target.gameAppId === 'number' && game.game_app_id === target.gameAppId) {
      return false;
    }

    if (target.epicAppId && game.epic_app_id === target.epicAppId) {
      return false;
    }

    if (target.gameName && game.game_name === target.gameName) {
      return false;
    }

    return true;
  });

  return nextGames.length === games.length ? games : nextGames;
};

export const pruneServicesByRemovalTarget = (
  services: ServiceCacheInfo[],
  target: CacheRemovalTarget | null
): ServiceCacheInfo[] => {
  if (!target?.serviceName) {
    return services;
  }

  const nextServices = services.filter((service) => service.service_name !== target.serviceName);
  return nextServices.length === services.length ? services : nextServices;
};
