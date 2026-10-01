import { useContext, useMemo } from 'react';
import { createContextHook } from '../createContextHook';
import { SpeedContext } from '../SpeedContext/SpeedContext.types';
import { ActivityContext, type ActivityLookup } from './context';

const useActivity = createContextHook(ActivityContext, 'useActivityStatus');

/**
 * Access the unified activity/presence state (drives every green status dot). Must be used within an
 * ActivityProvider. Returns a stable lookup object; consumers re-render when the underlying snapshot
 * changes.
 */
export const useActivityStatus = (): ActivityLookup => {
  const activity = useActivity();
  const speed = useContext(SpeedContext);

  return useMemo(() => {
    const downloadCount = (key: string): number => {
      if (speed?.speedSnapshot === null || speed === undefined) return 0;
      if (speed.gameSpeeds.some((game) => game.key === key)) return 1;
      return speed.clientSpeeds.find((client) => client.clientIp === key)?.activeGames ?? 0;
    };

    const usesDownloadSnapshot = (
      domain: Parameters<ActivityLookup['isActive']>[0],
      aspect: Parameters<ActivityLookup['isActive']>[2]
    ): boolean => domain === 'download' && aspect === 'downloading';

    const isActive: ActivityLookup['isActive'] = (domain, key, aspect) =>
      usesDownloadSnapshot(domain, aspect)
        ? downloadCount(key) > 0
        : activity.isActive(domain, key, aspect);

    return {
      isActive,
      activeCount: (domain, key, aspect) =>
        usesDownloadSnapshot(domain, aspect)
          ? downloadCount(key)
          : activity.activeCount(domain, key, aspect),
      ready: activity.ready,
      isActiveOrFallback: (domain, key, aspect, fallback) =>
        usesDownloadSnapshot(domain, aspect)
          ? isActive(domain, key, aspect)
          : activity.isActiveOrFallback(domain, key, aspect, fallback)
    };
  }, [activity, speed]);
};
