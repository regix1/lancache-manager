export const API_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/api`
  : '/api';

export const SIGNALR_BASE = import.meta.env.VITE_API_URL
  ? `${import.meta.env.VITE_API_URL}/hubs`
  : '/hubs';

// ============================================================================
// Window event names - the app's event-bus vocabulary
// ============================================================================
// Every window CustomEvent name the app dispatches or listens for. They live here, not in a
// feature folder, because dispatchers and listeners span services, contexts and components: the
// name IS the contract between two modules that never import each other.
//
// This matters more than a normal constant. A mismatch between the dispatch string and the listen
// string fails SILENTLY - no error, no type complaint, the listener simply never fires - so a
// typo or a half-finished rename is invisible until someone notices a feature quietly stopped
// reacting. Keep every window event name here and there is exactly one spelling to get right.
//
// The VALUES are a runtime contract; renaming one only works if every side changes at once.
export const APP_EVENTS = {
  /** Toast bridge: any module can raise a notification card without importing the notifications context. */
  SHOW_TOAST: 'show-toast',
  /** Tab navigation: dashboard/event widgets ask App to switch the active tab. */
  NAVIGATE_TO_TAB: 'navigate-to-tab',
  /** A notification is animating out; the bar fades it before the context drops it. */
  NOTIFICATION_REMOVING: 'notification-removing',
  /** Theme swapped or edited: charts, sparklines and the favicon re-read their colors. */
  THEME_CHANGE: 'themechange',
  /** A user preference changed and dependent contexts should re-read it. */
  PREFERENCE_CHANGED: 'preference-changed',
  /** All preferences were reset to defaults. */
  PREFERENCES_RESET: 'preferences-reset',
  /** "Disable sticky notifications" toggled; the notification bar re-reads its position. */
  STICKY_NOTIFICATIONS_CHANGE: 'stickynotificationschange',
  /** "Keep notifications visible" toggled; terminal cards re-arm (or cancel) auto-dismiss. */
  NOTIFICATION_VISIBILITY_CHANGE: 'notificationvisibilitychange',
  /** Tooltips enabled/disabled. */
  TOOLTIPS_CHANGE: 'tooltipschange',
  /** Depot mapping failed in a way that requires a full scan; prompts the modal. */
  SHOW_FULL_SCAN_MODAL: 'show-full-scan-modal',
  /** The auth session token changed; SignalR reconnects (or drops) on the new credentials. */
  AUTH_SESSION_UPDATED: 'auth-session-updated',
  /** Auth state changed (e.g. a 401 cleared it); consumers re-evaluate access. */
  AUTH_STATE_CHANGED: 'auth-state-changed',
  /** SignalR reconnected; consumers refetch state that may have drifted while disconnected. */
  SIGNALR_RECONNECTED: 'signalr-reconnected'
} as const;

// Services
export const SERVICES = [
  'steam',
  'epicgames',
  'origin',
  'blizzard',
  'wsus',
  'riot',
  'xbox'
] as const;

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
  HIDE_METADATA: 'lancache_downloads_hide_metadata',
  HIDE_SMALL_FILES: 'lancache_downloads_hide_small',
  REFRESH_RATE: 'lancache_refresh_rate',
  RECENT_DOWNLOADS_DETAILED: 'lancache_dashboard_recent_detailed',
  // Prefill run state, shared by PrefillContext, PrefillPanel and the prefill SignalR hooks.
  // These predate the lancache_ prefix and the VALUES must stay exactly as they are - renaming a
  // storage key silently orphans the state already saved in every user's browser.
  PREFILL_IN_PROGRESS: 'prefill_in_progress',
  PREFILL_SESSION_ID: 'prefill_session_id'
} as const;
