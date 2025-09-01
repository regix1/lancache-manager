// API Configuration
const getApiUrl = () => {
  // If we have an environment variable set, use it (for development)
  if (import.meta.env.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }
  
  // Since the frontend is served by the same container as the API,
  // use the same origin (empty string means same host/port/protocol)
  // This automatically works with whatever port is mapped in docker-compose
  return '';
};

export const API_BASE = `${getApiUrl()}/api`;

// Refresh intervals (in milliseconds)
export const REFRESH_INTERVAL = 5000; // 5 seconds
export const MOCK_UPDATE_INTERVAL = 3000; // 3 seconds for mock data updates

// Service names
export const SERVICES = ['steam', 'epic', 'origin', 'blizzard', 'wsus', 'riot'];

// Chart colors - Extended palette for better variety
export const CHART_COLORS = [
  '#60a5fa', // blue-400
  '#34d399', // emerald-400
  '#fbbf24', // amber-400
  '#a78bfa', // violet-400
  '#f87171', // red-400
  '#fb923c', // orange-400
  '#4ade80', // green-400
  '#e879f9', // fuchsia-400
  '#38bdf8', // sky-400
  '#facc15', // yellow-400
  '#818cf8', // indigo-400
  '#2dd4bf', // teal-400
  '#f472b6', // pink-400
  '#94a3b8', // slate-400
];

// Extended color classes for StatCard gradients
export const COLOR_CLASSES = {
  blue: 'from-blue-500 to-blue-600',
  green: 'from-green-500 to-green-600',
  purple: 'from-purple-500 to-purple-600',
  yellow: 'from-yellow-500 to-yellow-600',
  red: 'from-red-500 to-red-600',
  indigo: 'from-indigo-500 to-indigo-600',
  pink: 'from-pink-500 to-pink-600',
  gray: 'from-gray-500 to-gray-600',
  emerald: 'from-emerald-500 to-emerald-600',
  orange: 'from-orange-500 to-orange-600',
  cyan: 'from-cyan-500 to-cyan-600',
  teal: 'from-teal-500 to-teal-600',
  lime: 'from-lime-500 to-lime-600',
  amber: 'from-amber-500 to-amber-600',
  violet: 'from-violet-500 to-violet-600',
  fuchsia: 'from-fuchsia-500 to-fuchsia-600',
  rose: 'from-rose-500 to-rose-600',
  sky: 'from-sky-500 to-sky-600'
};

// Status colors for various UI states
export const STATUS_COLORS = {
  active: 'text-green-400',
  inactive: 'text-gray-400',
  warning: 'text-yellow-400',
  error: 'text-red-400',
  info: 'text-blue-400',
  success: 'text-green-400'
};

// Cache hit rate color thresholds
export const CACHE_HIT_COLORS = {
  excellent: { threshold: 75, color: 'bg-green-500', textColor: 'text-green-400' },
  good: { threshold: 50, color: 'bg-blue-500', textColor: 'text-blue-400' },
  fair: { threshold: 25, color: 'bg-yellow-500', textColor: 'text-yellow-400' },
  poor: { threshold: 0, color: 'bg-orange-500', textColor: 'text-orange-400' }
};

// Get cache hit color based on percentage
export const getCacheHitColor = (percentage) => {
  if (percentage >= CACHE_HIT_COLORS.excellent.threshold) return CACHE_HIT_COLORS.excellent;
  if (percentage >= CACHE_HIT_COLORS.good.threshold) return CACHE_HIT_COLORS.good;
  if (percentage >= CACHE_HIT_COLORS.fair.threshold) return CACHE_HIT_COLORS.fair;
  return CACHE_HIT_COLORS.poor;
};

// Pagination options
export const ITEMS_PER_PAGE_OPTIONS = [10, 25, 50, 100, 200, 500, 'unlimited'];

// localStorage keys for persistence
export const STORAGE_KEYS = {
  // Downloads tab
  SERVICE_FILTER: 'lancache_downloads_service',
  ITEMS_PER_PAGE: 'lancache_downloads_items',
  GROUP_GAMES: 'lancache_downloads_group',
  SHOW_METADATA: 'lancache_downloads_metadata',
  
  // Dashboard preferences
  CHART_SIZE: 'lancache_dashboard_chart_size',
  CHART_TAB: 'lancache_dashboard_chart_tab',
  DASHBOARD_CARD_VISIBILITY: 'lancache_dashboard_card_visibility',
  
  // Global preferences
  THEME: 'lancache_theme',
  REFRESH_RATE: 'lancache_refresh_rate'
};

// Time period options for stats
export const TIME_PERIODS = [
  { value: '1h', label: 'Last Hour' },
  { value: '6h', label: 'Last 6 Hours' },
  { value: '12h', label: 'Last 12 Hours' },
  { value: '24h', label: 'Last 24 Hours' },
  { value: '7d', label: 'Last 7 Days' },
  { value: '30d', label: 'Last 30 Days' },
  { value: 'all', label: 'All Time' }
];

// Chart interval options
export const CHART_INTERVALS = [
  { value: '5min', label: '5 Minutes' },
  { value: '15min', label: '15 Minutes' },
  { value: '30min', label: '30 Minutes' },
  { value: 'hourly', label: 'Hourly' },
  { value: 'daily', label: 'Daily' }
];

// Export default configuration
export default {
  API_BASE,
  REFRESH_INTERVAL,
  MOCK_UPDATE_INTERVAL,
  SERVICES,
  CHART_COLORS,
  COLOR_CLASSES,
  STATUS_COLORS,
  CACHE_HIT_COLORS,
  ITEMS_PER_PAGE_OPTIONS,
  STORAGE_KEYS,
  TIME_PERIODS,
  CHART_INTERVALS,
  getCacheHitColor
};