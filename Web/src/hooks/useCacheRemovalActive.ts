import { useOperationBusy } from './useOperationBusy';

/**
 * True while any cache-entity removal is running OR queued (purple waiting card):
 * a single game/service remove, an evicted-item remove, the batched evicted
 * Remove All, or a bulk Remove All run. These ops all mutate the same cache/log
 * files behind one backend lock, so every remove trigger in the game-cache
 * domain (Game Cache Detector + Evicted Items) disables together.
 */
export function useCacheRemovalActive(): boolean {
  return useOperationBusy({
    types: ['game_removal', 'service_removal', 'eviction_removal', 'bulk_removal'],
    status: ['running', 'waiting']
  });
}
