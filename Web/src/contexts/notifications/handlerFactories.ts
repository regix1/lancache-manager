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
import { COMPLETION_ANIMATION_DELAY_MS } from './constants';

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
 *     getId: (e) => NOTIFICATION_IDS.gameDetection(e.operationId),
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

    setNotifications((prev) => {
      // Check if already exists (skip if not replacing)
      if (!config.replaceExisting) {
        const existing = prev.find((n) => n.id === notificationId);
        if (existing) return prev;
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
 *     getId: (e) => NOTIFICATION_IDS.logRemoval(e.service),
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

    setNotifications((prev) => {
      // Check if any notification with this ID exists (running, completed, or failed)
      const existingAny = prev.find((n) => n.id === notificationId);
      
      // If notification exists but is completed/failed, ignore late progress events
      // This prevents duplicates when progress events arrive after completion
      if (existingAny && existingAny.status !== 'running') {
        return prev;
      }

      if (existingAny && existingAny.status === 'running') {
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
 *     getId: (e) => NOTIFICATION_IDS.gameRemoval(e.gameAppId),
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

    // Clear from localStorage
    localStorage.removeItem(config.storageKey);

    if (config.useAnimationDelay) {
      // First update progress to 100 while keeping status as 'running'
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);
        if (!existing) return prev;

        return prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              progress: 100,
              message: event.success
                ? (config.getSuccessMessage?.(event, n) ?? n.message)
                : n.message
            };
          }
          return n;
        });
      });

      // After animation delay, update status
      setTimeout(() => {
        setNotifications((prev) => {
          return prev.map((n) => {
            if (n.id === notificationId) {
              if (event.success) {
                return {
                  ...n,
                  status: 'completed' as const,
                  details: {
                    ...n.details,
                    ...config.getSuccessDetails?.(event, n)
                  }
                };
              } else {
                return {
                  ...n,
                  status: 'failed' as const,
                  error: config.getFailureMessage?.(event) ?? event.message ?? 'Operation failed'
                };
              }
            }
            return n;
          });
        });
        scheduleAutoDismiss(notificationId);
      }, COMPLETION_ANIMATION_DELAY_MS);
    } else {
      // Immediate completion
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Fast completion - no prior started event
          if (config.supportFastCompletion) {
            const fastId = config.getFastCompletionId?.(event) ?? notificationId;
            scheduleAutoDismiss(fastId);
            const status: 'completed' | 'failed' = event.success ? 'completed' : 'failed';
            return [
              ...prev,
              {
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
              }
            ];
          }
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success) {
              return {
                ...n,
                status: 'completed' as const,
                details: {
                  ...n.details,
                  ...config.getSuccessDetails?.(event, n)
                }
              };
            } else {
              return {
                ...n,
                status: 'failed' as const,
                error: config.getFailureMessage?.(event) ?? event.message ?? 'Operation failed'
              };
            }
          }
          return n;
        });
      });

      scheduleAutoDismiss(notificationId);
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
 * @param updateNotification - Function to update an existing notification
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
 *   updateNotification,
 *   scheduleAutoDismiss,
 *   cancelAutoDismissTimer
 * );
 * ```
 */
export function createStatusAwareProgressHandler<T>(
  config: StatusAwareProgressConfig<T>,
  setNotifications: SetNotifications,
  updateNotification: (id: string, updates: Partial<UnifiedNotification>) => void,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer?: CancelAutoDismissTimer
): (event: T) => void {
  return (event: T): void => {
    const notificationId = config.getId(event);
    const status = config.getStatus(event);

    if (status?.toLowerCase() === 'completed') {
      // Handle completion
      localStorage.removeItem(config.storageKey);
      updateNotification(notificationId, {
        status: 'completed',
        message: config.getCompletedMessage?.(event) ?? 'Operation completed',
        progress: 100
      });
      scheduleAutoDismiss(notificationId);
    } else if (status?.toLowerCase() === 'failed') {
      // Handle error
      localStorage.removeItem(config.storageKey);
      updateNotification(notificationId, {
        status: 'failed',
        error: config.getErrorMessage?.(event) ?? 'Operation failed'
      });
      scheduleAutoDismiss(notificationId);
    } else {
      // Handle progress - update existing or create new
      setNotifications((prev) => {
        // Check if any notification with this ID exists (running, completed, or failed)
        const existingAny = prev.find((n) => n.id === notificationId);
        
        // If notification exists but is completed/failed, ignore late progress events
        // This prevents duplicates when progress events arrive after completion
        if (existingAny && existingAny.status !== 'running') {
          return prev;
        }

        if (existingAny && existingAny.status === 'running') {
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
