/**
 * Types for the unified notification system.
 * These types are used throughout the notification context, handlers, and UI components.
 */

import type { OperationStatus, NotificationVariant } from '../../types/operations';

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
  | 'epic_game_mapping'
  | 'eviction_scan'
  | 'eviction_removal'
  | 'bulk_removal'
  | 'generic';

/**
 * Possible states for a notification. Aligned with the canonical backend
 * `OperationStatus` so SignalR status fields can flow through unchanged.
 * Consumers that only care about the narrower "running | completed | failed"
 * triple continue to work because those three values are still members.
 */
export type NotificationStatus = OperationStatus;

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
    gameAppId?: number;
    gameName?: string;
    epicAppId?: string;
    steamAppId?: string;
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

    // For epic_game_mapping
    totalEpicGames?: number;
    newEpicGames?: number;
    updatedEpicGames?: number;

    // For generic notifications
    notificationType?: NotificationVariant;

    // For data_import
    recordsImported?: number;
    recordsSkipped?: number;
    recordsErrors?: number;
    totalRecords?: number;

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
  /**
   * Schedules a notification to auto-dismiss after the configured delay.
   * Respects the user's "Keep Notifications Visible" preference (no-op when enabled).
   * Used by caller-managed notifications (e.g. useCancellableQueue's bulk_removal)
   * that don't go through a registry handler and therefore don't get auto-dismiss
   * scheduled for them automatically.
   */
  scheduleAutoDismiss: (notificationId: string, delayMs?: number) => void;
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

/**
 * Function to remove a notification by ID.
 * @param notificationId - The notification ID to remove
 */
export type RemoveNotification = (notificationId: string) => void;

// ============================================================================
// Notification Registry Types
// ============================================================================

/**
 * Configuration for a started event handler within a registry entry.
 */
export interface RegistryStartedConfig {
  /** Default message shown when the operation starts */
  defaultMessage: string;
  /** Optional function to get a custom message from the event */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getMessage?: (event: any) => string;
  /** Optional function to get notification details from the event */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getDetails?: (event: any) => UnifiedNotification['details'];
  /** If true, always replace existing notification (for restartable operations) */
  replaceExisting?: boolean;
}

/**
 * Configuration for a progress event handler within a registry entry.
 */
export interface RegistryProgressConfig {
  /** Function to get the progress message from the event */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getMessage: (event: any) => string;
  /** Function to get progress percentage (0-100) from the event */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getProgress: (event: any) => number;
  /** Function to get the status from the event */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getStatus: (event: any) => string | undefined;
  /** Message to show on completion */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getCompletedMessage?: (event: any) => string;
  /** Message to show on error */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getErrorMessage?: (event: any) => string | undefined;
  /** If true, support fast completion */
  supportFastCompletion?: boolean;
  /** Optional function to get notification details from the event */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getDetails?: (event: any) => UnifiedNotification['details'];
}

/**
 * Configuration for a completion event handler within a registry entry.
 */
export interface RegistryCompleteConfig {
  /** Optional function to get the success message */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getSuccessMessage?: (event: any, existing?: UnifiedNotification) => string;
  /** Optional function to get success details */
  getSuccessDetails?: (
    event: any, // eslint-disable-line @typescript-eslint/no-explicit-any
    existing?: UnifiedNotification
  ) => UnifiedNotification['details'];
  /** Optional function to get the cancelled message */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getCancelledMessage?: (event: any, existing?: UnifiedNotification) => string;
  /** Optional function to get cancelled details */
  getCancelledDetails?: (
    event: any, // eslint-disable-line @typescript-eslint/no-explicit-any
    existing?: UnifiedNotification
  ) => UnifiedNotification['details'];
  /** Optional function to get detail message (shown below main message) */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getDetailMessage?: (event: any) => string;
  /** Optional function to get the failure message */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  getFailureMessage?: (event: any) => string;
  /** If true, show a brief animation delay before marking complete */
  useAnimationDelay?: boolean;
  /** If true, support fast completion (no prior started event) */
  supportFastCompletion?: boolean;
  /** Optional function to get ID for fast completion (if different from getId) */
  getFastCompletionId?: () => string;
}

/**
 * Declarative registry entry describing the full lifecycle of a notification type.
 * Each entry specifies the started, progress, and completion handler configs
 * along with the SignalR event names they map to.
 */
export interface NotificationRegistryEntry {
  /** The notification type */
  type: NotificationType;
  /** Singleton notification ID */
  id: string;
  /** localStorage persistence key */
  storageKey: string;
  /** SignalR event names for each lifecycle phase */
  events: {
    started: string;
    progress: string;
    complete: string;
  };
  /** Configuration for the started handler */
  started: RegistryStartedConfig;
  /** Configuration for the progress handler */
  progress: RegistryProgressConfig;
  /** Configuration for the completion handler (optional for types without a separate complete event) */
  complete?: RegistryCompleteConfig;
  /** Optional callback invoked after the completion handler runs (e.g., to remove related notifications) */
  onComplete?: (removeNotification: RemoveNotification) => void;
}
