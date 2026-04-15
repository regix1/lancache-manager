import { get as idbGet, set as idbSet } from 'idb-keyval';

// Module-level in-memory cache — populated before React renders
const cache = new Map<string, unknown>();

// Cache keys — only the two that genuinely benefit from cold-start hydration:
// CACHE_INFO shows cache size immediately; GAME_DETECTION prevents icon flash.
// Other dashboard fields start empty and populate on first batch fetch.
export const IDB_KEYS = {
  CACHE_INFO: 'dashboard_cache_info',
  GAME_DETECTION: 'dashboard_game_detection'
} as const;

/**
 * Pre-load all cached dashboard data from IndexedDB into memory.
 * Called once in main.tsx BEFORE React renders.
 */
export async function preloadDashboardCache(): Promise<void> {
  try {
    const keys = Object.values(IDB_KEYS);
    const results = await Promise.all(keys.map((key) => idbGet(key).catch(() => undefined)));
    keys.forEach((key, i) => {
      if (results[i] !== undefined) {
        cache.set(key, results[i]);
      }
    });
  } catch {
    // IndexedDB unavailable — cache stays empty, skeleton will show
  }
}

/**
 * Synchronously read a pre-loaded cached value.
 * Safe to call in useState initializers.
 */
export function getCachedValue<T>(key: string): T | undefined {
  return cache.get(key) as T | undefined;
}

/**
 * Write a value to both the in-memory cache and IndexedDB.
 * Fire-and-forget — errors are silently ignored.
 */
export function setCachedValue<T>(key: string, value: T): void {
  cache.set(key, value);
  idbSet(key, value).catch(() => undefined);
}
