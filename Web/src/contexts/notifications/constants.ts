/**
 * Constants for the notification system.
 * Includes timing values, storage keys, and ID generators.
 */

import type { NotificationType } from './types';

/**
 * Backend OperationType wire string (camelCase) -> notification type.
 * Used by the operation wait-queue plumbing (OperationWaiting/OperationWaitingComplete
 * SignalR events and the /api/operations/waiting recovery endpoint) to attach the purple
 * waiting card to the same per-type singleton card the running operation will use.
 */
export const OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE: Record<string, NotificationType> = {
  cacheClearing: 'cache_clearing',
  corruptionRemoval: 'corruption_removal',
  corruptionDetection: 'corruption_detection',
  gameDetection: 'game_detection',
  logProcessing: 'log_processing',
  gameRemoval: 'game_removal',
  serviceRemoval: 'service_removal',
  depotMapping: 'depot_mapping',
  dataImport: 'data_import',
  databaseReset: 'database_reset',
  logRemoval: 'log_removal',
  epicMapping: 'epic_game_mapping',
  xboxMapping: 'xbox_game_mapping',
  evictionScan: 'eviction_scan',
  evictionRemoval: 'eviction_removal',
  cacheSizeScan: 'cache_size_scan',
  scheduledPrefill: 'scheduled_prefill'
};

/**
 * Cancel state that lives ONLY in this browser session and that no server payload can know:
 * the X button's two-stage soft-cancel -> force-kill intent (`cancelRequested`/`cancelSent`,
 * read by UniversalNotificationBar's cancel handler and deferred-cancel watchdog) and the
 * bulk queue's cancel signal (`cancelling`, the only flag useCancellableQueue's cascade honours).
 *
 * These are NOT `details.cancelled`, which is the TERMINAL outcome the server reports and which
 * renders the card red - see cacheRemovalHelpers, which sets both at once as
 * `{ cancelled: true, cancelling: false }`.
 *
 * Because a persisted card and a REST recovery snapshot both predate (or simply cannot see) the
 * live intent, these keys are stripped wherever card state is rehydrated or merged: the
 * localStorage restore in NotificationsContext, mergeEventDetails in handlerFactories, and
 * reconcileRecoveredCard in recoveryFactory.
 */
export const LIVE_ONLY_CANCEL_DETAIL_KEYS = [
  'cancelRequested',
  'cancelSent',
  'cancelling'
] as const;

// ============================================================================
// Shared lifecycle values
// ============================================================================

/** Full progress shared by terminal notification cards and bulk-progress calculations. */
export const FULL_PROGRESS_PERCENT = 100;

/** Highest displayed progress for operations that have not emitted completion yet. */
export const ACTIVE_PROGRESS_PERCENT_CAP = 99.9;

/** Generic completion fallback shared by lifecycle handlers and message formatters. */
export const GENERIC_COMPLETION_I18N_KEY = 'signalr.generic.complete';

/** Generic failure fallback shared by lifecycle handlers and message formatters. */
export const GENERIC_FAILURE_I18N_KEY = 'signalr.generic.failed';

/** Waiting-card message keys shared by live SignalR creation and REST recovery. */
export const OPERATION_WAITING_I18N_KEYS = {
  DEFAULT: 'common.notifications.operationWaiting',
  NAMED: 'common.notifications.operationWaitingNamed'
} as const;

/** Game-removal progress key shared by direct, bulk, and recovered removal cards. */
export const REMOVING_GAME_I18N_KEY = 'management.gameDetection.removingGame';

/** Game-removal failure fallback shared by direct and bulk removal flows. */
export const FAILED_TO_REMOVE_GAME_I18N_KEY = 'management.gameDetection.failedToRemoveGame';

// Window event names (including the notification system's own) live in one registry:
// APP_EVENTS in @utils/constants. A window event name is a contract between modules that never
// import each other, so keeping them all in one place is what makes a rename safe.

// ============================================================================
// Timing Constants
// ============================================================================

/** Default delay before auto-dismissing completed notifications (5 seconds) */
export const AUTO_DISMISS_DELAY_MS = 5000;

/** Delay before dismissing cancelled operation notifications (3 seconds) */
export const CANCELLED_NOTIFICATION_DELAY_MS = 3000;

/** Duration of notification slide/fade animations (300ms) */
export const NOTIFICATION_ANIMATION_DURATION_MS = 300;

/** Delay before dismissing Steam error notifications (10 seconds) */
export const STEAM_ERROR_DISMISS_DELAY_MS = 10000;

/** Default duration for toast notifications (4 seconds) */
export const TOAST_DEFAULT_DURATION_MS = 4000;

/** Number of animation steps for incremental scan progress animation */
export const INCREMENTAL_SCAN_ANIMATION_STEPS = 30;

/** Total duration of incremental scan animation (50ms × 30 steps = 1500ms) */
export const INCREMENTAL_SCAN_ANIMATION_DURATION_MS = 50 * INCREMENTAL_SCAN_ANIMATION_STEPS;

// ============================================================================
// Storage Keys
// ============================================================================

/**
 * localStorage keys for persisting notification state across page refreshes.
 * Each key maps to a specific notification type.
 */
export const NOTIFICATION_STORAGE_KEYS = {
  /** Key for log processing operation state */
  LOG_PROCESSING: 'notification_log_processing',
  /** Key for log removal operation state */
  LOG_REMOVAL: 'notification_log_removal',
  /** Key for game removal operation state */
  GAME_REMOVAL: 'notification_game_removal',
  /** Key for service removal operation state */
  SERVICE_REMOVAL: 'notification_service_removal',
  /** Key for corruption removal operation state */
  CORRUPTION_REMOVAL: 'notification_corruption_removal',
  /** Key for corruption detection operation state */
  CORRUPTION_DETECTION: 'notification_corruption_detection',
  /** Key for game detection operation state */
  GAME_DETECTION: 'notification_game_detection',
  /** Key for cache clearing operation state */
  CACHE_CLEARING: 'notification_cache_clearing',
  /** Key for database reset operation state */
  DATABASE_RESET: 'notification_database_reset',
  /** Key for depot mapping operation state */
  DEPOT_MAPPING: 'notification_depot_mapping',
  /** Key for data import operation state */
  DATA_IMPORT: 'notification_data_import',
  /** Key for Epic game mapping operation state */
  EPIC_GAME_MAPPING: 'notification_epic_game_mapping',
  /** Key for Xbox game mapping operation state */
  XBOX_GAME_MAPPING: 'notification_xbox_game_mapping',
  /** Key for eviction scan operation state */
  EVICTION_SCAN: 'eviction-scan-storage',
  /** Key for eviction removal operation state */
  EVICTION_REMOVAL: 'eviction-removal-storage',
  /** Key for cache file scan operation state */
  CACHE_SIZE_SCAN: 'cache-size-scan-storage',
  /** Key for scheduled prefill operation state */
  SCHEDULED_PREFILL: 'notification_scheduled_prefill',
  /** Key for scheduled log rotation run state */
  LOG_ROTATION: 'notification_log_rotation',
  /** Key for scheduled game image fetch run state */
  GAME_IMAGE_FETCH: 'notification_game_image_fetch',
  /** Key for scheduled Steam service refresh run state */
  STEAM_SERVICE_REFRESH: 'notification_steam_service_refresh',
  /** Key for scheduled cache snapshot run state */
  CACHE_SNAPSHOT: 'notification_cache_snapshot',
  /** Key for scheduled operation history cleanup run state */
  OPERATION_HISTORY_CLEANUP: 'notification_operation_history_cleanup',
  /** Key for scheduled performance optimization run state */
  PERFORMANCE_OPTIMIZATION: 'notification_performance_optimization',
  /** Key for scheduled dashboard cache warmer run state */
  DASHBOARD_CACHE_WARMER: 'notification_dashboard_cache_warmer'
} as const;

/** Pre-registry generic toast id for scheduled prefill Run Now (never completed). */
export const SCHEDULED_PREFILL_LEGACY_GENERIC_NOTIFICATION_ID = 'generic_Scheduled_prefill_started';

// ============================================================================
// Notification ID Generators
// ============================================================================

/**
 * Notification ID constants.
 * All IDs are singleton because only one operation of each type can run at a time
 * due to backend locks (_cacheLock, _startLock, etc.).
 */
export const NOTIFICATION_IDS = {
  /** ID for log processing operations */
  LOG_PROCESSING: 'log_processing',
  /** ID for cache clearing operations */
  CACHE_CLEARING: 'cache_clearing',
  /** ID for database reset operations */
  DATABASE_RESET: 'database_reset',
  /** ID for depot mapping operations */
  DEPOT_MAPPING: 'depot_mapping',
  /** ID for log removal operations */
  LOG_REMOVAL: 'log_removal',
  /** ID for game removal operations */
  GAME_REMOVAL: 'game_removal',
  /** ID for service removal operations */
  SERVICE_REMOVAL: 'service_removal',
  /** ID for corruption removal operations */
  CORRUPTION_REMOVAL: 'corruption_removal',
  /** ID for game detection operations */
  GAME_DETECTION: 'game_detection',
  /** ID for corruption detection operations */
  CORRUPTION_DETECTION: 'corruption_detection',
  /** ID for data import operations */
  DATA_IMPORT: 'data_import',
  /** ID for Epic game mapping updates */
  EPIC_GAME_MAPPING: 'epic_game_mapping',
  /** ID for Xbox game mapping updates */
  XBOX_GAME_MAPPING: 'xbox_game_mapping',
  /** ID for Steam session errors */
  STEAM_SESSION_ERROR: 'steam_session_error',
  /** ID for eviction scan operations */
  EVICTION_SCAN: 'eviction-scan-notification',
  /** ID for eviction removal operations */
  EVICTION_REMOVAL: 'eviction-removal-notification',
  /** ID for cache file scan operations */
  CACHE_SIZE_SCAN: 'cache-size-scan-notification',
  /** ID for scheduled prefill operations */
  SCHEDULED_PREFILL: 'scheduled_prefill',
  /** ID for scheduled log rotation runs */
  LOG_ROTATION: 'log_rotation',
  /** ID for scheduled game image fetch runs */
  GAME_IMAGE_FETCH: 'game_image_fetch',
  /** ID for scheduled Steam service refresh runs */
  STEAM_SERVICE_REFRESH: 'steam_service_refresh',
  /** ID for scheduled cache snapshot runs */
  CACHE_SNAPSHOT: 'cache_snapshot',
  /** ID for scheduled operation history cleanup runs */
  OPERATION_HISTORY_CLEANUP: 'operation_history_cleanup',
  /** ID for scheduled performance optimization runs */
  PERFORMANCE_OPTIMIZATION: 'performance_optimization',
  /** ID for scheduled dashboard cache warmer runs */
  DASHBOARD_CACHE_WARMER: 'dashboard_cache_warmer'
} as const;
