export const API_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/api`
  : '/api';

export const SIGNALR_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/hubs`
  : '/hubs';

// Services
export const SERVICES = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'] as const;

// Polling rate options (in milliseconds) - Best practices for dashboard refresh rates
export const POLLING_RATES = {
  LIVE: 0, // Live - Real-time SignalR updates, no throttling
  ULTRA: 1000, // 1s - Ultra-fast (very high load, unstable)
  REALTIME: 5000, // 5s - Real-time monitoring (high server load)
  STANDARD: 10000, // 10s - Balanced performance (recommended)
  RELAXED: 30000, // 30s - Low server load
  SLOW: 60000 // 60s - Minimal server impact
} as const;

export type PollingRate = keyof typeof POLLING_RATES;

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
  POLLING_RATE: 'lancache_polling_rate'
} as const;
