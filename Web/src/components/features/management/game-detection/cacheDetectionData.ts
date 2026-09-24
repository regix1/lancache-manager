import i18n from '@/i18n';
import ApiService from '@services/api.service';
import type { GameCacheInfo, ServiceCacheInfo, UnmappedService } from '../../../../types';

interface CachedDetectionSnapshot {
  hasCachedResults: boolean;
  games: GameCacheInfo[];
  services: ServiceCacheInfo[];
  // null when the last scan was incremental: that run has no cache index, so it measured no
  // unmapped set and the response omits the field. Distinct from an empty array, which means
  // a full scan found nothing unclaimed.
  unmappedServices: UnmappedService[] | null;
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
    unmappedServices: result.unmapped_services ?? null,
    lastDetectionTime: result.lastDetectionTime ?? null,
    totalGamesDetected: result.totalGamesDetected ?? 0,
    totalServicesDetected: result.totalServicesDetected ?? 0
  };
};

export const buildLoadedResultsSummary = (snapshot: CachedDetectionSnapshot): string | null => {
  const parts: string[] = [];

  if (snapshot.totalGamesDetected > 0) {
    parts.push(
      i18n.t('management.gameDetection.summary.games', { count: snapshot.totalGamesDetected })
    );
  }

  if (snapshot.totalServicesDetected > 0) {
    parts.push(
      i18n.t('management.gameDetection.summary.services', { count: snapshot.totalServicesDetected })
    );
  }

  return parts.length > 0 ? parts.join(i18n.t('management.gameDetection.summary.join')) : null;
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

    if (target.serviceName && target.gameName) {
      return !(game.service === target.serviceName && game.game_name === target.gameName);
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
