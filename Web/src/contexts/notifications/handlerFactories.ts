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
import type { DepotMappingCompleteEvent } from '../SignalRContext/types';
import {
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS,
  INCREMENTAL_SCAN_ANIMATION_STEPS,
  INCREMENTAL_SCAN_ANIMATION_DURATION_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  CANCELLED_NOTIFICATION_DELAY_MS
} from './constants';

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
  /** Optional function to get detail message (shown below main message) */
  getDetailMessage?: (event: T) => string;
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

    // Track the ID to schedule (may be different for fast completion)
    let idToSchedule = notificationId;

    if (config.useAnimationDelay) {
      // Single atomic update that sets BOTH progress=100 AND final status
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // Validate notification exists
        if (!existing) return prev;

        // Only complete notifications that are still 'running'
        if (existing.status !== 'running') return prev;

        return prev.map((n) => {
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
      });
    } else {
      // Immediate completion
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Fast completion - no prior started event
          if (config.supportFastCompletion) {
            const fastId = config.getFastCompletionId?.(event) ?? notificationId;
            idToSchedule = fastId;  // Update the ID to schedule
            const status: 'completed' | 'failed' = event.success ? 'completed' : 'failed';
            const newNotification = {
              id: fastId,
              type: config.type,
              status,
              message: event.success
                ? (config.getSuccessMessage?.(event) ?? event.message ?? 'Operation completed')
                : (config.getFailureMessage?.(event) ?? event.message ?? 'Operation failed'),
              detailMessage: config.getDetailMessage?.(event),
              startedAt: new Date(),
              progress: 100,
              details: config.getSuccessDetails?.(event),
              error: event.success
                ? undefined
                : (config.getFailureMessage?.(event) ?? event.message)
            };

            return [...prev, newNotification];
          }
          return prev;
        }

        // Only complete notifications that are still 'running'
        if (existing.status !== 'running') return prev;

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success) {
              return {
                ...n,
                progress: 100,
                status: 'completed' as const,
                detailMessage: config.getDetailMessage?.(event) ?? n.detailMessage,
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
      });
    }

    // ALWAYS schedule auto-dismiss - React 18 batching means we can't rely on
    // closure variables set inside setNotifications callback.
    // scheduleAutoDismiss will verify the notification is in terminal state before dismissing.
    scheduleAutoDismiss(idToSchedule);
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
  /** If true, support fast completion (completion event arrives before notification created) */
  supportFastCompletion?: boolean;
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

      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Fast completion - notification doesn't exist yet (operation completed before UI created it)
          if (config.supportFastCompletion) {
            const newNotification: UnifiedNotification = {
              id: notificationId,
              type: config.type,
              status: 'completed' as const,
              message: config.getCompletedMessage?.(event) ?? 'Operation completed',
              progress: 100,
              startedAt: new Date()
            };

            return [...prev, newNotification];
          }
          return prev;
        }

        // Only complete if notification is running
        if (existing.status !== 'running') {
          return prev;
        }

        return prev.map((n) => {
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
      });

      // ALWAYS schedule auto-dismiss for completed status - React 18 batching means
      // we can't rely on closure variables set inside setNotifications callback.
      // scheduleAutoDismiss will verify the notification is in terminal state before dismissing.
      scheduleAutoDismiss(notificationId);
    } else if (status?.toLowerCase() === 'failed') {
      // Handle error - clear localStorage FIRST
      localStorage.removeItem(config.storageKey);

      const errorMessage = config.getErrorMessage?.(event) ?? 'Operation failed';

      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // If notification doesn't exist, nothing to do
        if (!existing) {
          return prev;
        }

        // If already failed/completed, just ensure it gets dismissed
        if (existing.status === 'failed' || existing.status === 'completed') {
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              status: 'failed' as const,
              message: errorMessage,
              error: errorMessage
            };
          }
          return n;
        });
      });

      // Always schedule auto-dismiss for failed status
      scheduleAutoDismiss(notificationId);
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

// ============================================================================
// Depot Mapping Completion Handler Factory
// ============================================================================

/**
 * Creates a specialized completion handler for depot mapping operations.
 * This handles the complex depot mapping completion logic including:
 * - Cancellation handling
 * - Incremental scan progress animation
 * - Full scan immediate completion
 * - Full scan modal trigger for errors requiring full scan
 *
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal
 * @returns A handler function that processes depot mapping completion events
 */
export function createDepotMappingCompletionHandler(
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): (event: DepotMappingCompleteEvent) => void {
  const notificationId = NOTIFICATION_IDS.DEPOT_MAPPING;
  const storageKey = NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING;

  /** Animates progress from current value to 100% over multiple steps */
  const animateProgressToCompletion = (
    startProgress: number,
    successMessage: string,
    successDetails: Record<string, unknown>,
    onComplete: () => void
  ): void => {
    const steps = INCREMENTAL_SCAN_ANIMATION_STEPS;
    const interval = INCREMENTAL_SCAN_ANIMATION_DURATION_MS / steps;
    const progressIncrement = (100 - startProgress) / steps;
    let currentStep = 0;

    const animationInterval = setInterval(() => {
      currentStep++;
      const newProgress = Math.min(100, startProgress + progressIncrement * currentStep);

      setNotifications((prev: UnifiedNotification[]) =>
        prev.map((n) =>
          n.id === notificationId
            ? { ...n, progress: newProgress, message: newProgress >= 100 ? successMessage : n.message }
            : n
        )
      );

      if (currentStep >= steps) {
        clearInterval(animationInterval);
        setTimeout(() => {
          setNotifications((prev: UnifiedNotification[]) => {
            const existing = prev.find((n) => n.id === notificationId);
            if (!existing) return prev;

            localStorage.removeItem(storageKey);
            return prev.map((n) =>
              n.id === notificationId
                ? { ...n, status: 'completed' as const, message: successMessage, details: { ...n.details, ...successDetails } }
                : n
            );
          });
          onComplete();
        }, NOTIFICATION_ANIMATION_DURATION_MS);
      }
    }, interval);
  };

  /** Handles depot mapping cancellation */
  const handleCancelled = (): void => {
    console.debug('[Notifications] DepotMappingComplete: cancellation received');
    localStorage.removeItem(storageKey);
    const newStartedAt = new Date();

    setNotifications((prev: UnifiedNotification[]) => {
      const existing = prev.find((n) => n.id === notificationId);

      if (!existing) {
        // Fast completion - create notification for cancellation
        const newNotification: UnifiedNotification = {
          id: notificationId,
          type: 'depot_mapping',
          status: 'completed',
          message: 'Depot mapping scan cancelled',
          startedAt: newStartedAt,
          progress: 100,
          details: { cancelled: true }
        };
        return [...prev, newNotification];
      }

      // Update existing notification
      return prev.map((n) =>
        n.id === notificationId
          ? { ...n, status: 'completed' as const, message: 'Depot mapping scan cancelled', progress: 100, details: { ...n.details, cancelled: true } }
          : n
      );
    });

    scheduleAutoDismiss(notificationId, CANCELLED_NOTIFICATION_DELAY_MS);
  };

  /** Handles successful depot mapping completion */
  const handleSuccess = (event: DepotMappingCompleteEvent): void => {
    const successMessage = event.message || 'Depot mapping completed successfully';
    const successDetails = { totalMappings: event.totalMappings, downloadsUpdated: event.downloadsUpdated };
    const isIncremental = event.scanMode === 'incremental';

    localStorage.removeItem(storageKey);

    if (isIncremental) {
      // For incremental scans, animate progress to 100%
      setNotifications((prev: UnifiedNotification[]) => {
        const notification = prev.find((n) => n.id === notificationId);

        if (!notification) {
          // Fast completion - create completed notification
          const newNotification: UnifiedNotification = {
            id: notificationId,
            type: 'depot_mapping',
            status: 'completed',
            message: successMessage,
            startedAt: new Date(),
            progress: 100,
            details: successDetails
          };
          return [...prev, newNotification];
        }

        // Animation callback will schedule auto-dismiss when complete
        animateProgressToCompletion(
          notification.progress || 0,
          successMessage,
          successDetails,
          () => scheduleAutoDismiss(notificationId)
        );
        return prev;
      });

      // Schedule auto-dismiss for fast completion case (animation handles the existing case)
      scheduleAutoDismiss(notificationId);
    } else {
      // For full scans, complete immediately
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Fast completion - create completed notification
          const newNotification: UnifiedNotification = {
            id: notificationId,
            type: 'depot_mapping',
            status: 'completed',
            message: successMessage,
            startedAt: new Date(),
            progress: 100,
            details: successDetails
          };
          return [...prev, newNotification];
        }

        // Update existing notification
        return prev.map((n) =>
          n.id === notificationId
            ? { ...n, status: 'completed' as const, message: successMessage, progress: 100, details: { ...n.details, ...successDetails } }
            : n
        );
      });

      scheduleAutoDismiss(notificationId);
    }
  };

  /** Handles failed depot mapping with optional full scan modal trigger */
  const handleFailure = (event: DepotMappingCompleteEvent): void => {
    const errorMessage = event.error || event.message || 'Depot mapping failed';
    const requiresFullScan =
      errorMessage.includes('change gap is too large') ||
      errorMessage.includes('requires full scan') ||
      errorMessage.includes('requires a full scan');

    if (requiresFullScan) {
      window.dispatchEvent(new CustomEvent('show-full-scan-modal', { detail: { error: errorMessage } }));
    }

    localStorage.removeItem(storageKey);

    setNotifications((prev: UnifiedNotification[]) => {
      const existing = prev.find((n) => n.id === notificationId);

      if (!existing) {
        // Fast completion - create failed notification
        const newNotification: UnifiedNotification = {
          id: notificationId,
          type: 'depot_mapping',
          status: 'failed',
          message: 'Depot mapping failed',
          error: errorMessage,
          startedAt: new Date(),
          progress: 100
        };
        return [...prev, newNotification];
      }

      // Update existing notification
      return prev.map((n) =>
        n.id === notificationId
          ? { ...n, status: 'failed' as const, error: errorMessage, progress: 100 }
          : n
      );
    });

    scheduleAutoDismiss(notificationId);
  };

  // Return the main handler function
  return (event: DepotMappingCompleteEvent): void => {
    console.debug('[Notifications] DepotMappingComplete event received:', {
      cancelled: event.cancelled,
      success: event.success,
      message: event.message
    });
    if (event.cancelled) {
      handleCancelled();
    } else if (event.success) {
      handleSuccess(event);
    } else {
      handleFailure(event);
    }
  };
}
