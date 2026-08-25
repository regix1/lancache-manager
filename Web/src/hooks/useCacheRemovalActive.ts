import { useMemo } from 'react';
import { useNotifications } from '@contexts/notifications/useNotifications';
import type { NotificationType } from '@contexts/notifications/types';

/** The three per-item removal types that share the game-cache domain's backend lock. */
function isCacheRemovalType(type: NotificationType): boolean {
  return type === 'game_removal' || type === 'service_removal' || type === 'eviction_removal';
}

/**
 * True while any cache-entity removal is running OR queued (purple waiting card):
 * a single game/service remove, an evicted-item remove, the batched evicted
 * Remove All, or a bulk Remove All run whose own items are cache removals. These
 * ops all mutate the same cache/log files behind one backend lock, so every
 * remove trigger in the game-cache domain (Game Cache Detector + Evicted Items)
 * disables together. A `bulk_removal` card only counts when its `itemTypes`
 * names one of the three types above, so an unrelated batch (log removal) does
 * not disable these controls.
 */
export function useCacheRemovalActive(): boolean {
  const { notifications } = useNotifications();

  return useMemo(
    () =>
      notifications.some(
        (n) =>
          (n.status === 'running' || n.status === 'waiting') &&
          (isCacheRemovalType(n.type) ||
            (n.type === 'bulk_removal' && (n.details?.itemTypes ?? []).some(isCacheRemovalType)))
      ),
    [notifications]
  );
}
