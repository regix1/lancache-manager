export const API_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/api`
  : '/api';

export const SIGNALR_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/hubs`
  : '/hubs';

// Services
export const SERVICES = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'] as const;
export type ServiceType = (typeof SERVICES)[number];

// Refresh intervals (in milliseconds)
export const REFRESH_INTERVAL = 5000; // 5 seconds
export const PROCESSING_CHECK_INTERVAL = 2000; // 2 seconds

// File size units
export const FILE_SIZE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'] as const;

// Default pagination
export const DEFAULT_PAGE_SIZE = 50;

// Storage keys - COMPLETE VERSION
export const STORAGE_KEYS = {
  DASHBOARD_CARD_ORDER: 'lancache_dashboard_card_order',
  DASHBOARD_CARD_VISIBILITY: 'lancache_dashboard_card_visibility',
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  SHOW_SMALL_FILES: 'lancache_downloads_show_small'
} as const;
