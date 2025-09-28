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
export const MOCK_UPDATE_INTERVAL = 10000; // 10 seconds
export const PROCESSING_CHECK_INTERVAL = 2000; // 2 seconds

// Color mappings for services
export const SERVICE_COLORS = {
  steam: '#1e40af',
  epic: '#7c3aed',
  origin: '#ea580c',
  blizzard: '#0891b2',
  wsus: '#16a34a',
  riot: '#dc2626'
} as const;

// Cache hit rate color thresholds
export const CACHE_HIT_COLORS = {
  EXCELLENT: { min: 75, color: 'green' },
  GOOD: { min: 50, color: 'blue' },
  FAIR: { min: 25, color: 'yellow' },
  LOW: { min: 0, color: 'orange' }
} as const;

// File size units
export const FILE_SIZE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'] as const;

// Default pagination
export const DEFAULT_PAGE_SIZE = 50;
export const PAGE_SIZE_OPTIONS = [
  { value: '50', label: '50 items' },
  { value: '100', label: '100 items' },
  { value: '150', label: '150 items' },
  { value: 'unlimited', label: 'Unlimited' }
] as const;

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

// Chart colors
export const CHART_COLORS: readonly string[] = [
  '#3b82f6', // blue
  '#10b981', // emerald
  '#8b5cf6', // purple
  '#f59e0b', // amber
  '#ef4444', // red
  '#06b6d4', // cyan
  '#f97316', // orange
  '#ec4899', // pink
  '#14b8a6', // teal
  '#84cc16' // lime
] as const;
