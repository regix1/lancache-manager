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
import i18n from '@/i18n';

/**
 * Merges incoming event details over existing card details. When the incoming details
 * carry a DIFFERENT operationId (a re-spawned or queue-promoted operation reusing the same
 * per-type singleton card), stale per-operation cancel flags are dropped first - otherwise
 * a leftover cancelRequested/cancelSent from the PREVIOUS op makes the deferred-cancel
 * watchdog in UniversalNotificationBar auto-cancel the brand-new operation (the
 * phantom-cancel half of the cancel->respawn loop).
 */
function mergeEventDetails(
  existing: UnifiedNotification['details'],
  incoming: UnifiedNotification['details']
): UnifiedNotification['details'] {
  if (!incoming) return existing;
  const base: NonNullable<UnifiedNotification['details']> = { ...existing };
  if (
    typeof incoming.operationId === 'string' &&
    typeof base.operationId === 'string' &&
    incoming.operationId !== base.operationId
  ) {
    delete base.cancelRequested;
    delete base.cancelSent;
    delete base.cancelling;
  }
  return { ...base, ...incoming };
}
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
interface StartedHandlerConfig<T> {
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
  /** Extra notification ids to remove when this operation starts */
  additionalIdsToRemove?: string[];
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
        if (existing && existing.status === 'running') {
          const eventDetails = config.getDetails?.(event);
          if (eventDetails && Object.keys(eventDetails).length > 0) {
            const merged: UnifiedNotification = {
              ...existing,
              message: config.getMessage?.(event) ?? existing.message,
              details: mergeEventDetails(existing.details, eventDetails)
            };
            localStorage.setItem(config.storageKey, JSON.stringify(merged));
            return prev.map((n) => (n.id === notificationId ? merged : n));
          }
          return prev;
        }
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

      // Remove this notification and any legacy/extra ids, then add the new running slot.
      const idsToRemove = new Set([notificationId, ...(config.additionalIdsToRemove ?? [])]);
      const filtered = prev.filter((n) => !idsToRemove.has(n.id));
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
interface CompletionHandlerConfig<T> {
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
  /** Optional function to get the cancelled message */
  getCancelledMessage?: (event: T, existing?: UnifiedNotification) => string;
  /** Optional function to get cancelled details */
  getCancelledDetails?: (
    event: T,
    existing?: UnifiedNotification
  ) => UnifiedNotification['details'];
  /** Optional function to get detail message (shown below main message) */
  getDetailMessage?: (event: T) => string;
  /** Optional function to get the failure message */
  getFailureMessage?: (event: T) => string;
  /** If true, show a brief animation delay before marking complete */
  useAnimationDelay?: boolean;
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
export function createCompletionHandler<
  T extends {
    success: boolean;
    stageKey?: string;
    context?: Record<string, unknown>;
    message?: string;
    cancelled?: boolean;
  }
>(
  config: CompletionHandlerConfig<T>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): (event: T) => void {
  return (event: T): void => {
    const notificationId = config.getId(event);
    const isCancelled = event.cancelled === true;

    const resolveFailureMessage = (existing?: UnifiedNotification): string => {
      if (isCancelled) {
        return (
          config.getCancelledMessage?.(event, existing) ??
          event.message ??
          (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
          'Operation cancelled'
        );
      }

      return (
        config.getFailureMessage?.(event) ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        i18n.t('signalr.generic.failed')
      );
    };

    // Clear from localStorage IMMEDIATELY to prevent stuck state on refresh
    localStorage.removeItem(config.storageKey);

    // Track the ID to schedule (may be different for fast completion)
    let idToSchedule = notificationId;

    /** Builds a terminal card for fast completion (no prior started event). */
    const buildFastCompletionNotification = (): UnifiedNotification => {
      const fastId = config.getFastCompletionId?.(event) ?? notificationId;
      idToSchedule = fastId;
      const failureMessage = resolveFailureMessage();

      if (event.success && !isCancelled) {
        return {
          id: fastId,
          type: config.type,
          status: 'completed' as const,
          message:
            config.getSuccessMessage?.(event) ??
            (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
            i18n.t('signalr.generic.complete'),
          detailMessage: config.getDetailMessage?.(event),
          startedAt: new Date(),
          progress: 100,
          details: config.getSuccessDetails?.(event)
        };
      }

      return {
        id: fastId,
        type: config.type,
        status: 'failed' as const,
        message: failureMessage,
        error: failureMessage,
        detailMessage: config.getDetailMessage?.(event),
        startedAt: new Date(),
        progress: 100,
        details: isCancelled
          ? { ...config.getCancelledDetails?.(event), cancelled: true }
          : config.getSuccessDetails?.(event)
      };
    };

    if (config.useAnimationDelay) {
      // Single atomic update that sets BOTH progress=100 AND final status
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // Fast completion - no live running slot to transition (missing or already
        // terminal); materialize a terminal card instead of dropping the event
        if (!existing || existing.status !== 'running') {
          const newNotification = buildFastCompletionNotification();
          return [...prev.filter((n) => n.id !== newNotification.id), newNotification];
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success && !isCancelled) {
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
            }

            const failureMessage = resolveFailureMessage(n);
            return {
              ...n,
              progress: 100,
              status: 'failed' as const,
              message: failureMessage,
              error: failureMessage,
              ...(isCancelled && {
                details: {
                  ...n.details,
                  ...config.getCancelledDetails?.(event, n),
                  cancelled: true
                }
              })
            };
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
          return [...prev, buildFastCompletionNotification()];
        }

        // Only complete notifications that are still 'running'
        if (existing.status !== 'running') return prev;

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success && !isCancelled) {
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
            }

            const failureMessage = resolveFailureMessage(n);
            return {
              ...n,
              progress: 100,
              status: 'failed' as const,
              message: failureMessage,
              error: failureMessage,
              detailMessage: config.getDetailMessage?.(event) ?? n.detailMessage,
              ...(isCancelled && {
                details: {
                  ...n.details,
                  ...config.getCancelledDetails?.(event, n),
                  cancelled: true
                }
              })
            };
          }
          return n;
        });
      });
    }

    scheduleAutoDismiss(idToSchedule, isCancelled ? CANCELLED_NOTIFICATION_DELAY_MS : undefined);
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
interface StatusAwareProgressConfig<T> {
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
  /** Optional function to get notification details from the event (e.g., operationId for cancel support) */
  getDetails?: (event: T) => UnifiedNotification['details'];
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
              startedAt: new Date(),
              details: config.getDetails?.(event)
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
              progress: 100,
              details: { ...n.details, ...config.getDetails?.(event) }
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

        const eventDetails = config.getDetails?.(event);
        return prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              status: 'failed' as const,
              message: errorMessage,
              error: errorMessage,
              // Merge event details (e.g., operationId) to match completed/progress branches
              ...(eventDetails ? { details: { ...n.details, ...eventDetails } } : {})
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
          const eventDetails = config.getDetails?.(event);
          return prev.map((n) => {
            if (n.id === notificationId) {
              return {
                ...n,
                message: config.getMessage(event),
                progress: config.getProgress(event),
                // Merge event details (e.g., operationId) into existing details; stale
                // cancel flags are dropped when the operationId changed (see mergeEventDetails).
                ...(eventDetails ? { details: mergeEventDetails(n.details, eventDetails) } : {})
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
            startedAt: new Date(),
            details: config.getDetails?.(event)
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
            ? {
                ...n,
                progress: newProgress,
                message: newProgress >= 100 ? successMessage : n.message
              }
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
                ? {
                    ...n,
                    status: 'completed' as const,
                    message: successMessage,
                    details: { ...n.details, ...successDetails }
                  }
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
          message: i18n.t('signalr.depotMapping.cancelled'),
          startedAt: newStartedAt,
          progress: 100,
          details: { cancelled: true }
        };
        return [...prev, newNotification];
      }

      // Update existing notification
      return prev.map((n) =>
        n.id === notificationId
          ? {
              ...n,
              status: 'completed' as const,
              message: i18n.t('signalr.depotMapping.cancelled'),
              progress: 100,
              details: { ...n.details, cancelled: true }
            }
          : n
      );
    });

    scheduleAutoDismiss(notificationId, CANCELLED_NOTIFICATION_DELAY_MS);
  };

  /** Handles successful depot mapping completion */
  const handleSuccess = (event: DepotMappingCompleteEvent): void => {
    const successMessage = event.stageKey
      ? i18n.t(event.stageKey, event.context ?? {})
      : i18n.t('signalr.depotMapping.finalized', { updated: event.downloadsUpdated ?? 0 });
    const successDetails = {
      totalMappings: event.totalMappings,
      downloadsUpdated: event.downloadsUpdated
    };
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
            ? {
                ...n,
                status: 'completed' as const,
                message: successMessage,
                progress: 100,
                details: { ...n.details, ...successDetails }
              }
            : n
        );
      });

      scheduleAutoDismiss(notificationId);
    }
  };

  /** Handles failed depot mapping with optional full scan modal trigger */
  const handleFailure = (event: DepotMappingCompleteEvent): void => {
    const errorMessage =
      event.error ??
      (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
      i18n.t('signalr.generic.failed');
    const requiresFullScan =
      errorMessage.includes('change gap is too large') ||
      errorMessage.includes('requires full scan') ||
      errorMessage.includes('requires a full scan');

    if (requiresFullScan) {
      window.dispatchEvent(
        new CustomEvent('show-full-scan-modal', { detail: { error: errorMessage } })
      );
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
    if (event.cancelled) {
      handleCancelled();
    } else if (event.success) {
      handleSuccess(event);
    } else {
      handleFailure(event);
    }
  };
}
