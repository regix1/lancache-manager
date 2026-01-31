/**
 * Handler factory functions for creating SignalR event handlers.
 * These factories reduce code duplication by providing a common pattern
 * for handling started, progress, and completion events.
 */

import type {
  NotificationType,
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss,
  CancelAutoDismissTimer
} from './types';

// ============================================================================
// Started Handler Factory
// ============================================================================

/**
 * Configuration for creating a started event handler.
 * @template T - The type of the SignalR event
 */
export interface StartedHandlerConfig<T> {
  /** The notification type this handler creates */
  type: NotificationType;
  /** Function to extract the notification ID from the event */
  getId: (event: T) => string;
  /** localStorage key for persisting the notification */
  storageKey: string;
  /** Default message if getMessage is not provided or returns undefined */
  defaultMessage: string;
  /** Optional function to get a custom message from the event */
  getMessage?: (event: T) => string;
  /** Optional function to get notification details from the event */
  getDetails?: (event: T) => UnifiedNotification['details'];
  /** If true, always replace existing notification (for restartable operations) */
  replaceExisting?: boolean;
}

/**
 * Creates a handler function for "started" events.
 * Started handlers create new running notifications when an operation begins.
 *
 * @template T - The type of the SignalR event
 * @param config - Configuration for the handler
 * @param setNotifications - React setState function for notifications
 * @param cancelAutoDismissTimer - Optional function to cancel pending auto-dismiss
 * @returns A handler function that processes the started event
 *
 * @example
 * ```ts
 * const handleGameDetectionStarted = createStartedHandler<GameDetectionStartedEvent>(
 *   {
 *     type: 'game_detection',
 *     getId: () => NOTIFICATION_IDS.GAME_DETECTION,
 *     storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
 *     defaultMessage: 'Detecting games...',
 *     getDetails: (e) => ({ operationId: e.operationId })
 *   },
 *   setNotifications,
 *   cancelAutoDismissTimer
 * );
 * ```
 */
export function createStartedHandler<T>(
  config: StartedHandlerConfig<T>,
  setNotifications: SetNotifications,
  cancelAutoDismissTimer?: CancelAutoDismissTimer
): (event: T) => void {
  return (event: T): void => {
    const notificationId = config.getId(event);

    // Cancel any existing auto-dismiss timer for this notification
    cancelAutoDismissTimer?.(notificationId);

    setNotifications((prev: UnifiedNotification[]) => {
      // Check if already exists in running state (skip if running and not replacing)
      if (!config.replaceExisting) {
        const existing = prev.find((n) => n.id === notificationId);
        // Only skip if existing notification is still running
        // Allow replacing completed/failed notifications with new started ones
        if (existing && existing.status === 'running') return prev;
      }

      const newNotification: UnifiedNotification = {
        id: notificationId,
        type: config.type,
        status: 'running',
        message: config.getMessage?.(event) ?? config.defaultMessage,
        startedAt: new Date(),
        progress: 0,
        details: config.getDetails?.(event)
      };

      // Persist to localStorage for recovery on page refresh
      localStorage.setItem(config.storageKey, JSON.stringify(newNotification));

      // Remove any existing notifications with the same ID and add new one
      const filtered = prev.filter((n) => n.id !== notificationId);
      return [...filtered, newNotification];
    });
  };
}

// ============================================================================
// Progress Handler Factory
// ============================================================================

/**
 * Configuration for creating a progress event handler.
 * @template T - The type of the SignalR event
 */
export interface ProgressHandlerConfig<T> {
  /** The notification type this handler updates */
  type: NotificationType;
  /** Function to extract the notification ID from the event */
  getId: (event: T) => string;
  /** localStorage key for persisting the notification */
  storageKey: string;
  /** Function to get the message, receives both event and existing notification */
  getMessage: (event: T, existing?: UnifiedNotification) => string;
  /** Optional function to get the detail message */
  getDetailMessage?: (event: T, existing?: UnifiedNotification) => string | undefined;
  /** Optional function to get notification details */
  getDetails?: (event: T, existing?: UnifiedNotification) => UnifiedNotification['details'];
  /** Optional function to get progress percentage (0-100) */
  getProgress?: (event: T) => number;
}

/**
 * Creates a handler function for progress events.
 * Progress handlers update existing notifications or create new ones if none exist.
 *
 * @template T - The type of the SignalR event
 * @param config - Configuration for the handler
 * @param setNotifications - React setState function for notifications
 * @param cancelAutoDismissTimer - Optional function to cancel pending auto-dismiss
 * @returns A handler function that processes the progress event
 *
 * @example
 * ```ts
 * const handleLogRemovalProgress = createProgressHandler<LogRemovalProgressEvent>(
 *   {
 *     type: 'log_removal',
 *     getId: () => NOTIFICATION_IDS.LOG_REMOVAL,
 *     storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
 *     getMessage: (e) => `Removing ${e.service} entries...`,
 *     getProgress: (e) => e.percentComplete || 0
 *   },
 *   setNotifications,
 *   cancelAutoDismissTimer
 * );
 * ```
 */
export function createProgressHandler<T>(
  config: ProgressHandlerConfig<T>,
  setNotifications: SetNotifications,
  cancelAutoDismissTimer?: CancelAutoDismissTimer
): (event: T) => void {
  return (event: T): void => {
    const notificationId = config.getId(event);

    setNotifications((prev: UnifiedNotification[]) => {
      const existing = prev.find((n) => n.id === notificationId);

      // If notification exists but is completed/failed, ignore late progress events
      // This prevents duplicates when progress events arrive after completion
      if (existing && existing.status !== 'running') {
        return prev;
      }

      if (existing) {
        // Update existing running notification
        return prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              message: config.getMessage(event, n),
              detailMessage: config.getDetailMessage?.(event, n),
              progress: config.getProgress?.(event) ?? n.progress,
              details: {
                ...n.details,
                ...config.getDetails?.(event, n)
              }
            };
          }
          return n;
        });
      } else {
        // Cancel any existing auto-dismiss timer
        cancelAutoDismissTimer?.(notificationId);

        // Create new notification (only if no existing notification with this ID)
        const newNotification: UnifiedNotification = {
          id: notificationId,
          type: config.type,
          status: 'running',
          message: config.getMessage(event),
          detailMessage: config.getDetailMessage?.(event),
          progress: config.getProgress?.(event) ?? 0,
          startedAt: new Date(),
          details: config.getDetails?.(event)
        };

        // Persist to localStorage for recovery
        localStorage.setItem(config.storageKey, JSON.stringify(newNotification));

        return [...prev, newNotification];
      }
    });
  };
}

// ============================================================================
// Completion Handler Factory
// ============================================================================

/**
 * Configuration for creating a completion event handler.
 * @template T - The type of the SignalR event (must have success and optional message)
 */
export interface CompletionHandlerConfig<T> {
  /** The notification type this handler completes */
  type: NotificationType;
  /** Function to extract the notification ID from the event */
  getId: (event: T) => string;
  /** localStorage key to clear on completion */
  storageKey: string;
  /** Optional function to get the success message */
  getSuccessMessage?: (event: T, existing?: UnifiedNotification) => string;
  /** Optional function to get success details */
  getSuccessDetails?: (event: T, existing?: UnifiedNotification) => UnifiedNotification['details'];
  /** Optional function to get the failure message */
  getFailureMessage?: (event: T) => string;
  /** If true, show a brief animation delay before marking complete */
  useAnimationDelay?: boolean;
  /** If true, support fast completion (no prior started event) */
  supportFastCompletion?: boolean;
  /** Optional function to get ID for fast completion (if different from getId) */
  getFastCompletionId?: (event: T) => string;
}

/**
 * Creates a handler function for completion events.
 * Completion handlers transition notifications from running to completed/failed.
 *
 * @template T - The type of the SignalR event (must have success: boolean)
 * @param config - Configuration for the handler
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal
 * @returns A handler function that processes the completion event
 *
 * @example
 * ```ts
 * const handleGameRemovalComplete = createCompletionHandler<GameRemovalCompleteEvent>(
 *   {
 *     type: 'game_removal',
 *     getId: () => NOTIFICATION_IDS.GAME_REMOVAL,
 *     storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
 *     getSuccessDetails: (e) => ({ filesDeleted: e.filesDeleted })
 *   },
 *   setNotifications,
 *   scheduleAutoDismiss
 * );
 * ```
 */
export function createCompletionHandler<T extends { success: boolean; message?: string }>(
  config: CompletionHandlerConfig<T>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): (event: T) => void {
  return (event: T): void => {
    const notificationId = config.getId(event);

    // Clear from localStorage IMMEDIATELY to prevent stuck state on refresh
    localStorage.removeItem(config.storageKey);

    if (config.useAnimationDelay) {
      // FIXED: Single atomic update that sets BOTH progress=100 AND final status
      // This eliminates the race condition from two-phase updates
      // CSS transitions handle the visual animation, not delayed state changes
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // FIXED: Validate notification exists
        if (!existing) return prev;

        // FIXED: Only complete notifications that are still 'running'
        // This prevents duplicate completion events from causing issues
        if (existing.status !== 'running') return prev;

        const updatedNotifications = prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success) {
              return {
                ...n,
                progress: 100,
                status: 'completed' as const,
                message: config.getSuccessMessage?.(event, n) ?? n.message,
                details: {
                  ...n.details,
                  ...config.getSuccessDetails?.(event, n)
                }
              };
            } else {
              return {
                ...n,
                progress: 100,
                status: 'failed' as const,
                error: config.getFailureMessage?.(event) ?? event.message ?? 'Operation failed'
              };
            }
          }
          return n;
        });

        // FIXED: Schedule auto-dismiss INSIDE callback after confirming notification was updated
        // Use setTimeout to defer to next tick, ensuring state is committed
        setTimeout(() => scheduleAutoDismiss(notificationId), 0);

        return updatedNotifications;
      });
    } else {
      // Immediate completion
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Fast completion - no prior started event
          if (config.supportFastCompletion) {
            const fastId = config.getFastCompletionId?.(event) ?? notificationId;
            const status: 'completed' | 'failed' = event.success ? 'completed' : 'failed';
            const newNotification = {
              id: fastId,
              type: config.type,
              status,
              message: event.success
                ? (config.getSuccessMessage?.(event) ?? event.message ?? 'Operation completed')
                : (config.getFailureMessage?.(event) ?? event.message ?? 'Operation failed'),
              startedAt: new Date(),
              progress: 100,
              details: config.getSuccessDetails?.(event),
              error: event.success
                ? undefined
                : (config.getFailureMessage?.(event) ?? event.message)
            };

            // FIXED: Schedule auto-dismiss inside callback
            setTimeout(() => scheduleAutoDismiss(fastId), 0);

            return [...prev, newNotification];
          }
          return prev;
        }

        // FIXED: Only complete notifications that are still 'running'
        if (existing.status !== 'running') return prev;

        const updatedNotifications = prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success) {
              return {
                ...n,
                progress: 100,
                status: 'completed' as const,
                details: {
                  ...n.details,
                  ...config.getSuccessDetails?.(event, n)
                }
              };
            } else {
              return {
                ...n,
                progress: 100,
                status: 'failed' as const,
                error: config.getFailureMessage?.(event) ?? event.message ?? 'Operation failed'
              };
            }
          }
          return n;
        });

        // FIXED: Schedule auto-dismiss inside callback after confirming update
        setTimeout(() => scheduleAutoDismiss(notificationId), 0);

        return updatedNotifications;
      });
    }
  };
}


// ============================================================================
// Status-Aware Progress Handler Factory
// ============================================================================

/**
 * Configuration for creating a status-aware progress handler.
 * This handler automatically detects completion/error states from the event's status field.
 * @template T - The type of the SignalR event (must have optional status field)
 */
export interface StatusAwareProgressConfig<T> {
  /** The notification type this handler updates */
  type: NotificationType;
  /** Function to extract the notification ID from the event */
  getId: (event: T) => string;
  /** localStorage key for persisting/clearing the notification */
  storageKey: string;
  /** Function to get the progress message */
  getMessage: (event: T) => string;
  /** Function to get progress percentage (0-100) */
  getProgress: (event: T) => number;
  /** Function to get the status from the event */
  getStatus: (event: T) => string | undefined;
  /** Message to show on completion (can use event data) */
  getCompletedMessage?: (event: T) => string;
  /** Message to show on error (uses event message by default) */
  getErrorMessage?: (event: T) => string | undefined;
}

/**
 * Creates a handler function for progress events that automatically handles
 * completion and error states based on the event's status field.
 *
 * This is useful for events that use a single progress handler for all states
 * (started, progress, completed, error) rather than separate handlers.
 *
 * @template T - The type of the SignalR event
 * @param config - Configuration for the handler
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal
 * @param cancelAutoDismissTimer - Optional function to cancel pending auto-dismiss
 * @returns A handler function that processes the progress event
 *
 * @example
 * ```ts
 * const handleDatabaseResetProgress = createStatusAwareProgressHandler<DatabaseResetProgressEvent>(
 *   {
 *     type: 'database_reset',
 *     getId: () => NOTIFICATION_IDS.DATABASE_RESET,
 *     storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
 *     getMessage: (e) => e.message || 'Resetting database...',
 *     getProgress: (e) => e.percentComplete || 0,
 *     getStatus: (e) => e.status,
 *     getCompletedMessage: (e) => e.message || 'Database reset completed'
 *   },
 *   setNotifications,
 *   scheduleAutoDismiss,
 *   cancelAutoDismissTimer
 * );
 * ```
 */
export function createStatusAwareProgressHandler<T>(
  config: StatusAwareProgressConfig<T>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer?: CancelAutoDismissTimer
): (event: T) => void {
  return (event: T): void => {
    const notificationId = config.getId(event);
    const status = config.getStatus(event);

    if (status?.toLowerCase() === 'completed') {
      // Handle completion - clear localStorage FIRST
      localStorage.removeItem(config.storageKey);

      // FIXED: Use setNotifications to validate status before completing
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // Only complete if notification exists and is running
        if (!existing || existing.status !== 'running') return prev;

        const updated = prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              status: 'completed' as const,
              message: config.getCompletedMessage?.(event) ?? 'Operation completed',
              progress: 100
            };
          }
          return n;
        });

        // FIXED: Schedule auto-dismiss inside callback
        setTimeout(() => scheduleAutoDismiss(notificationId), 0);

        return updated;
      });
    } else if (status?.toLowerCase() === 'failed') {
      // Handle error - clear localStorage FIRST
      localStorage.removeItem(config.storageKey);

      // FIXED: Use setNotifications to validate status before failing
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // Only fail if notification exists and is running
        if (!existing || existing.status !== 'running') return prev;

        const updated = prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              status: 'failed' as const,
              error: config.getErrorMessage?.(event) ?? 'Operation failed'
            };
          }
          return n;
        });

        // FIXED: Schedule auto-dismiss inside callback
        setTimeout(() => scheduleAutoDismiss(notificationId), 0);

        return updated;
      });
    } else {
      // Handle progress - update existing or create new
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // If notification exists but is completed/failed, ignore late progress events
        // This prevents duplicates when progress events arrive after completion
        if (existing && existing.status !== 'running') {
          return prev;
        }

        if (existing) {
          return prev.map((n) => {
            if (n.id === notificationId) {
              return {
                ...n,
                message: config.getMessage(event),
                progress: config.getProgress(event)
              };
            }
            return n;
          });
        } else {
          // Cancel any existing auto-dismiss timer
          cancelAutoDismissTimer?.(notificationId);

          // Create new notification (only if no existing notification with this ID)
          const newNotification: UnifiedNotification = {
            id: notificationId,
            type: config.type,
            status: 'running',
            message: config.getMessage(event),
            progress: config.getProgress(event),
            startedAt: new Date()
          };

          // Persist to localStorage for recovery
          localStorage.setItem(config.storageKey, JSON.stringify(newNotification));

          const filtered = prev.filter((n) => n.id !== notificationId);
          return [...filtered, newNotification];
        }
      });
    }
  };
}
