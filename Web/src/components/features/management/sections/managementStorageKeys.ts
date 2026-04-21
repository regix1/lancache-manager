/**
 * Centralized localStorage keys for the Management tab "Storage" area and
 * the Game Cache Detector card. Keeping these in a single module avoids
 * silent drift between getters and setters scattered across components.
 */
export const MANAGEMENT_STORAGE_KEYS = {
  GAME_CACHE_EXPANDED: 'management-game-cache-expanded',
  EVICTED_DATA_EXPANDED: 'management-evicted-data-expanded-v2',
  EVICTION_SETTINGS_EXPANDED: 'management-eviction-settings-expanded',
  EVICTED_ITEMS_EXPANDED: 'management-evicted-items-expanded'
} as const;

/**
 * Name of the `CustomEvent` dispatched by `StorageSection.performEvictionSave`
 * after the server has persisted new eviction settings. Listeners
 * (`DownloadsTab`, `Dashboard`) use it to refresh their in-session
 * `evictedDataMode` without waiting for a remount.
 */
export const EVICTION_SETTINGS_CHANGED_EVENT = 'eviction-settings-changed' as const;

/**
 * Strongly-typed payload for `EVICTION_SETTINGS_CHANGED_EVENT`. Mirrors the
 * response shape of `ApiService.updateEvictionSettings` — keep in sync with
 * the server contract (`/stats/eviction` PUT response).
 */
export interface EvictionSettingsChangedDetail {
  evictedDataMode: string;
  evictionScanNotifications: boolean;
}
