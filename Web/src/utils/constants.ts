export const API_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/api`
  : '/api';

export const SIGNALR_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/hubs`
  : '/hubs';

// Services
export const SERVICES = ['steam', 'epicgames', 'origin', 'blizzard', 'wsus', 'riot'] as const;

// Refresh rate options (in milliseconds) - controls how often SignalR updates are applied
export const REFRESH_RATES = {
  LIVE: 0, // Live - Real-time SignalR updates, minimum 500ms throttle
  ULTRA: 1000, // 1s - Ultra-fast updates
  REALTIME: 5000, // 5s - Real-time monitoring
  STANDARD: 10000, // 10s - Balanced performance (recommended)
  RELAXED: 30000, // 30s - Low update frequency
  SLOW: 60000 // 60s - Minimal updates
} as const;

export type RefreshRate = keyof typeof REFRESH_RATES;

// File size units
export const FILE_SIZE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'] as const;

// Storage keys - COMPLETE VERSION
export const STORAGE_KEYS = {
  DASHBOARD_CARD_ORDER: 'lancache_dashboard_card_order',
  DASHBOARD_CARD_VISIBILITY: 'lancache_dashboard_card_visibility',
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small',
  REFRESH_RATE: 'lancache_refresh_rate',
  // Dashboard data cache
  DASHBOARD_CACHE_INFO: 'lancache_dashboard_cache_info',
  DASHBOARD_CLIENT_STATS: 'lancache_dashboard_client_stats',
  DASHBOARD_SERVICE_STATS: 'lancache_dashboard_service_stats',
  DASHBOARD_STATS: 'lancache_dashboard_stats',
  DASHBOARD_LATEST_DOWNLOADS: 'lancache_dashboard_latest_downloads'
} as const;
