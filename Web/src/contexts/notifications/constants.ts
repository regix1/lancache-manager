/**
 * Constants for the notification system.
 * Includes timing values, storage keys, and ID generators.
 */

// ============================================================================
// Timing Constants
// ============================================================================

/** Default delay before auto-dismissing completed notifications (5 seconds) */
export const AUTO_DISMISS_DELAY_MS = 5000;

/** Delay before dismissing cancelled operation notifications (3 seconds) */
export const CANCELLED_NOTIFICATION_DELAY_MS = 3000;

/** Duration of notification slide/fade animations (300ms) */
export const NOTIFICATION_ANIMATION_DURATION_MS = 300;

/** Delay before transitioning from running to completed status for visual effect (800ms) */
export const COMPLETION_ANIMATION_DELAY_MS = 800;

/** Delay before dismissing Steam error notifications (10 seconds) */
export const STEAM_ERROR_DISMISS_DELAY_MS = 10000;

/** Default duration for toast notifications (4 seconds) */
export const TOAST_DEFAULT_DURATION_MS = 4000;

/** Number of animation steps for incremental scan progress animation */
export const INCREMENTAL_SCAN_ANIMATION_STEPS = 30;

/** Delay between each animation step (50ms) */
const ANIMATION_STEP_DELAY_MS = 50;

/** Total duration of incremental scan animation (steps × step delay) */
export const INCREMENTAL_SCAN_ANIMATION_DURATION_MS =
  ANIMATION_STEP_DELAY_MS * INCREMENTAL_SCAN_ANIMATION_STEPS;

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
  DEPOT_MAPPING: 'notification_depot_mapping'
} as const;

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
  CORRUPTION_DETECTION: 'corruption_detection'
} as const;
