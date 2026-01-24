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
export const ANIMATION_STEP_DELAY_MS = 50;

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
  DEPOT_MAPPING: 'notification_depot_mapping',
  /** Key for GitHub downloading operation state */
  GITHUB_DOWNLOADING: 'notification_github_downloading'
} as const;

/** Type for storage key values */
export type NotificationStorageKey =
  (typeof NOTIFICATION_STORAGE_KEYS)[keyof typeof NOTIFICATION_STORAGE_KEYS];

// ============================================================================
// Notification ID Generators
// ============================================================================

/**
 * Notification ID constants and generator functions.
 * Fixed IDs are used for singleton operations (only one can run at a time).
 * Generator functions create unique IDs for operations that can run in parallel.
 */
export const NOTIFICATION_IDS = {
  // Fixed (singleton) operation IDs
  /** ID for log processing operations */
  LOG_PROCESSING: 'log_processing',
  /** ID for cache clearing operations */
  CACHE_CLEARING: 'cache_clearing',
  /** ID for database reset operations */
  DATABASE_RESET: 'database_reset',
  /** ID for depot mapping operations */
  DEPOT_MAPPING: 'depot_mapping',

  /**
   * Generates a unique ID for log removal operations.
   * @param service - The service name being removed from logs
   * @returns Unique notification ID
   */
  logRemoval: (service: string): string => `log_removal_${service}`,

  /**
   * Generates a unique ID for game removal operations.
   * @param gameAppId - The game's Steam App ID
   * @returns Unique notification ID
   */
  gameRemoval: (gameAppId: number | string): string => `game_removal_${gameAppId}`,

  /**
   * Generates a unique ID for service removal operations.
   * @param serviceName - The service name being removed
   * @returns Unique notification ID
   */
  serviceRemoval: (serviceName: string): string => `service_removal_${serviceName}`,

  /**
   * Generates a unique ID for corruption removal operations.
   * @param service - The service name being cleaned
   * @returns Unique notification ID
   */
  corruptionRemoval: (service: string): string => `corruption_removal_${service}`,

  /**
   * Generates a unique ID for game detection operations.
   * @param operationId - The unique operation identifier
   * @returns Unique notification ID
   */
  gameDetection: (operationId: string): string => `game_detection_${operationId}`,

  /**
   * Generates a unique ID for corruption detection operations.
   * @param operationId - The unique operation identifier
   * @returns Unique notification ID
   */
  corruptionDetection: (operationId: string): string => `corruption_detection_${operationId}`
} as const;
