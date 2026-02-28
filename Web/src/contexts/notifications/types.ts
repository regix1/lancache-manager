/**
 * Types for the unified notification system.
 * These types are used throughout the notification context, handlers, and UI components.
 */

/**
 * All possible notification types in the system.
 * Each type corresponds to a specific operation or event.
 */
export type NotificationType =
  | 'log_processing'
  | 'cache_clearing'
  | 'log_removal'
  | 'service_removal'
  | 'game_removal'
  | 'corruption_removal'
  | 'corruption_detection'
  | 'database_reset'
  | 'depot_mapping'
  | 'game_detection'
  | 'data_import'
  | 'generic';

/**
 * Possible states for a notification.
 * - running: Operation is in progress
 * - completed: Operation finished successfully
 * - failed: Operation encountered an error
 */
export type NotificationStatus = 'running' | 'completed' | 'failed';

/**
 * Unified notification data structure.
 * Represents all types of notifications in the system with a common interface.
 */
export interface UnifiedNotification {
  /** Unique identifier for this notification */
  id: string;
  /** The type of operation this notification represents */
  type: NotificationType;
  /** Current status of the operation */
  status: NotificationStatus;
  /** Progress percentage (0-100) for operations that support progress tracking */
  progress?: number;
  /** Primary message displayed to the user */
  message: string;
  /** Secondary detail message with additional information */
  detailMessage?: string;
  /** Timestamp when the operation started */
  startedAt: Date;
  /**
   * Version counter for tracking notification instances.
   * Used to prevent race conditions with auto-dismiss timers.
   * When a notification is updated, this counter is incremented so that
   * stale auto-dismiss callbacks can detect they should not proceed.
   * Optional for backwards compatibility - defaults to 0 if not provided.
   */
  instanceVersion?: number;

  /** Type-specific details for the notification */
  details?: {
    // For log_processing
    mbProcessed?: number;
    mbTotal?: number;
    entriesProcessed?: number;
    totalLines?: number;
    estimatedTime?: string;

    // For cache_clearing
    filesDeleted?: number;
    directoriesProcessed?: number;
    bytesDeleted?: number;
    operationId?: string;
    cancelling?: boolean;

    // For service_removal
    service?: string;
    linesProcessed?: number;
    linesRemoved?: number;

    // For game_removal
    gameAppId?: string;
    gameName?: string;
    bytesFreed?: number;
    logEntriesRemoved?: number;

    // For depot_mapping
    totalMappings?: number;
    processedMappings?: number;
    mappingsApplied?: number;
    percentComplete?: number;
    isProcessing?: boolean;
    isLoggedOn?: boolean;
    downloadsUpdated?: number;

    // For game_detection
    scanType?: 'full' | 'incremental';
    totalGamesDetected?: number;
    totalServicesDetected?: number;

    // For generic notifications
    notificationType?: 'success' | 'error' | 'info' | 'warning';

    // Cancellation flag
    cancelled?: boolean;
  };

  /** Error message when status is 'failed' */
  error?: string;
}

/**
 * Context type for the notifications provider.
 * Provides access to notifications state and mutation functions.
 */
export interface NotificationsContextType {
  /** Array of all current notifications */
  notifications: UnifiedNotification[];
  /**
   * Adds a new notification to the system.
   * @param notification - The notification data (id and startedAt are generated automatically)
   * @returns The generated notification ID
   */
  addNotification: (notification: Omit<UnifiedNotification, 'id' | 'startedAt'>) => string;
  /**
   * Updates an existing notification.
   * @param id - The notification ID to update
   * @param updates - Partial notification data to merge
   */
  updateNotification: (id: string, updates: Partial<UnifiedNotification>) => void;
  /**
   * Removes a notification immediately without animation.
   * @param id - The notification ID to remove
   */
  removeNotification: (id: string) => void;
  /**
   * Removes all completed or failed notifications.
   */
  clearCompletedNotifications: () => void;
  /**
   * Returns true if any removal operation is currently running.
   * Used to disable all removal buttons since they share a backend lock.
   */
  isAnyRemovalRunning: boolean;
  /**
   * Returns the type of removal currently running, or null if none.
   */
  activeRemovalType: NotificationType | null;
}

// ============================================================================
// Handler Factory Types
// ============================================================================

/** React setState dispatch function for notifications */
export type SetNotifications = React.Dispatch<React.SetStateAction<UnifiedNotification[]>>;

/**
 * Function to schedule automatic dismissal of a notification.
 * @param notificationId - The notification ID to dismiss
 * @param delayMs - Optional delay in milliseconds before dismissal
 */
export type ScheduleAutoDismiss = (notificationId: string, delayMs?: number) => void;

/**
 * Function to cancel a pending auto-dismiss timer.
 * @param notificationId - The notification ID whose timer should be cancelled
 */
export type CancelAutoDismissTimer = (notificationId: string) => void;
