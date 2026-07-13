/**
 * Handler factory functions for creating SignalR event handlers.
 * These factories reduce code duplication by providing a common pattern
 * for handling started, progress, and completion events.
 */

import type {
  NotificationType,
  NotificationProgressMode,
  NotificationStatus,
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss,
  CancelAutoDismissTimer
} from './types';
import type { DepotMappingCompleteEvent } from '../SignalRContext/types';
import { isTerminalNotificationStatus } from './notificationStatus';
import i18n from '@/i18n';

/**
 * Statuses a live event promotes back to 'running'.
 *
 * A progress event is proof the operation is alive, so a card parked in a pre-run status must not
 * ignore its OWN operation's events: a queued card whose promotion (Started) event was missed
 * would otherwise swallow every later progress and completion event and sit frozen forever.
 * 'cancelling' is deliberately NOT promotable: those cards still take progress updates, but must
 * keep showing that a cancel is in flight.
 */
const PROMOTABLE_TO_RUNNING: readonly NotificationStatus[] = ['waiting', 'pending'];

const promoteStatus = (status: NotificationStatus): NotificationStatus =>
  PROMOTABLE_TO_RUNNING.includes(status) ? 'running' : status;

/** operationId carried by any lifecycle event on the wire. */
function eventOperationId(event: unknown): string | undefined {
  const operationId = (event as { operationId?: unknown } | null | undefined)?.operationId;
  return typeof operationId === 'string' ? operationId : undefined;
}

/**
 * Whether a lifecycle event is allowed to touch the card currently in its type's singleton slot.
 *
 * CRITICAL: a non-running card in that slot is NOT necessarily the same operation. Two operations
 * of one type can be live at once - the backend queues the second, and the wait-queue parks the
 * QUEUED op's 'waiting' card in the shared slot while the FIRST op is still running and still
 * emitting progress. Letting those events through would promote the queued card to running,
 * overwrite its operationId (so the X button cancels the WRONG operation), and let the running
 * op's completion auto-dismiss a card whose operation never even started.
 *
 * So a running card keeps the historical behavior of accepting its type's events, while any other
 * card is touched ONLY when the event provably belongs to it (matching operationId). Unprovable
 * means no: that is the pre-existing, safe behavior.
 */
function eventTargetsCard(existing: UnifiedNotification, event: unknown): boolean {
  if (existing.status === 'running') return true;
  const cardOperationId = existing.details?.operationId;
  const incomingOperationId = eventOperationId(event);
  return Boolean(cardOperationId && incomingOperationId && cardOperationId === incomingOperationId);
}

/**
 * Merges incoming event details over existing card details. Stale per-operation cancel
 * flags (cancelRequested/cancelSent/cancelling) are dropped before merging whenever they
 * would otherwise survive onto a NEW operationId - otherwise a leftover flag from a
 * PREVIOUS or not-yet-known op makes the deferred-cancel watchdog in
 * UniversalNotificationBar auto-cancel a brand-new operation (the phantom-cancel half of
 * the cancel->respawn loop).
 *
 * `forNewOperationEvent` distinguishes the two call sites:
 * - createStartedHandler (forNewOperationEvent: true): a Started event only reaches this
 *   merge when the singleton card is ALREADY 'running' (see the caller), which - given the
 *   backend's one-op-per-type lock - can only mean a re-spawned/queue-promoted operation,
 *   never a second delivery for the same op. Stale flags are stripped whenever the
 *   incoming payload carries any operationId, regardless of what (if anything) the
 *   existing card's operationId was.
 * - createStatusAwareProgressHandler (default): progress events are continuations of the
 *   SAME running operation, so flags are stripped only when the existing card already had
 *   a different operationId. This preserves the legitimate deferred-cancel case (user
 *   clicks X before the card has an operationId) - the cancelRequested flag must survive
 *   until a progress event delivers that operation's first operationId.
 */
function mergeEventDetails(
  existing: UnifiedNotification['details'],
  incoming: UnifiedNotification['details'],
  forNewOperationEvent = false
): UnifiedNotification['details'] {
  if (!incoming) return existing;
  const base: NonNullable<UnifiedNotification['details']> = { ...existing };
  const incomingHasOperationId = typeof incoming.operationId === 'string';
  const shouldStripStaleCancelFlags = forNewOperationEvent
    ? incomingHasOperationId
    : incomingHasOperationId &&
      typeof base.operationId === 'string' &&
      incoming.operationId !== base.operationId;
  if (shouldStripStaleCancelFlags) {
    for (const key of LIVE_ONLY_CANCEL_DETAIL_KEYS) {
      delete base[key];
    }
  }
  return { ...base, ...incoming };
}
import {
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS,
  INCREMENTAL_SCAN_ANIMATION_STEPS,
  INCREMENTAL_SCAN_ANIMATION_DURATION_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  CANCELLED_NOTIFICATION_DELAY_MS,
  FULL_PROGRESS_PERCENT,
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  LIVE_ONLY_CANCEL_DETAIL_KEYS
} from './constants';
import { APP_EVENTS } from '@utils/constants';

// ============================================================================
// Started Handler Factory
// ============================================================================

/**
 * Configuration for creating a started event handler.
 * @template T - The type of the SignalR event
 */
interface StartedHandlerConfig<T> {
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: T) => boolean;
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

    if (config.shouldDisplay?.(event) === false) {
      localStorage.removeItem(config.storageKey);
      cancelAutoDismissTimer?.(notificationId);
      const idsToRemove = new Set([notificationId, ...(config.additionalIdsToRemove ?? [])]);
      setNotifications((prev: UnifiedNotification[]) =>
        prev.filter((notification) => !idsToRemove.has(notification.id))
      );
      return;
    }

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
              // A Started event reaching this branch always means a NEW operation (the
              // singleton card is already 'running'); strip stale cancel flags unconditionally.
              details: mergeEventDetails(existing.details, eventDetails, true)
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
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: T) => boolean;
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
  /**
   * Optional function to get detail message (shown below main message). Returning undefined leaves
   * the card's existing detail line in place (see the `?? n.detailMessage` fallbacks below).
   */
  getDetailMessage?: (event: T) => string | undefined;
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

    if (config.shouldDisplay?.(event) === false) {
      localStorage.removeItem(config.storageKey);
      setNotifications((prev: UnifiedNotification[]) =>
        prev.filter((notification) => notification.id !== notificationId)
      );
      return;
    }

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
        i18n.t(GENERIC_FAILURE_I18N_KEY)
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
            i18n.t(GENERIC_COMPLETION_I18N_KEY),
          detailMessage: config.getDetailMessage?.(event),
          startedAt: new Date(),
          progress: FULL_PROGRESS_PERCENT,
          details: config.getSuccessDetails?.(event)
        };
      }

      return {
        id: fastId,
        type: config.type,
        status: isCancelled ? ('cancelled' as const) : ('failed' as const),
        message: failureMessage,
        ...(!isCancelled && { error: failureMessage }),
        detailMessage: config.getDetailMessage?.(event),
        startedAt: new Date(),
        progress: FULL_PROGRESS_PERCENT,
        details: isCancelled
          ? { ...config.getCancelledDetails?.(event), cancelled: true }
          : config.getSuccessDetails?.(event)
      };
    };

    if (config.useAnimationDelay) {
      // Single atomic update that sets BOTH progress=100 AND final status
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // A card in this slot that belongs to a DIFFERENT operation (the wait-queue parks a queued
        // op's waiting card here while this op runs) must be left alone entirely: neither
        // transitioned nor replaced by a fast-completion card.
        if (existing && !eventTargetsCard(existing, event)) {
          return prev;
        }

        // Fast completion - no live slot to transition (missing or already terminal);
        // materialize a terminal card instead of dropping the event
        if (!existing || isTerminalNotificationStatus(existing.status)) {
          const newNotification = buildFastCompletionNotification();
          return [...prev.filter((n) => n.id !== newNotification.id), newNotification];
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success && !isCancelled) {
              return {
                ...n,
                progress: FULL_PROGRESS_PERCENT,
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
              progress: FULL_PROGRESS_PERCENT,
              status: isCancelled ? ('cancelled' as const) : ('failed' as const),
              message: failureMessage,
              ...(!isCancelled && { error: failureMessage }),
              ...(isCancelled && { error: undefined }),
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

        // Never touch a card belonging to a DIFFERENT operation of this type (a queued op's
        // waiting card parked in the shared slot), and never re-terminate a terminal card.
        if (!eventTargetsCard(existing, event) || isTerminalNotificationStatus(existing.status)) {
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (event.success && !isCancelled) {
              return {
                ...n,
                progress: FULL_PROGRESS_PERCENT,
                status: 'completed' as const,
                // Apply the type's success message, exactly like the useAnimationDelay branch
                // above. Without this an entry that configures getSuccessMessage still finished on
                // whatever its LAST PROGRESS event said - which is how a completed scheduled
                // prefill ended up presenting "Riot needs login..." (the last service's progress
                // line) as the outcome of the run the user had just stopped.
                message: config.getSuccessMessage?.(event, n) ?? n.message,
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
              progress: FULL_PROGRESS_PERCENT,
              status: isCancelled ? ('cancelled' as const) : ('failed' as const),
              message: failureMessage,
              ...(!isCancelled && { error: failureMessage }),
              ...(isCancelled && { error: undefined }),
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
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: T) => boolean;
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
  /** Optional secondary metrics shown below the stable primary message. */
  getDetailMessage?: (event: T) => string | undefined;
  /** Optional phase-aware progress semantics. */
  getProgressMode?: (event: T) => NotificationProgressMode | undefined;
  /** Optional textual equivalent of the progress metrics. */
  getProgressAriaValueText?: (event: T) => string | undefined;
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

    if (config.shouldDisplay?.(event) === false) {
      localStorage.removeItem(config.storageKey);
      cancelAutoDismissTimer?.(notificationId);
      setNotifications((prev: UnifiedNotification[]) =>
        prev.filter((notification) => notification.id !== notificationId)
      );
      return;
    }

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
              progress: FULL_PROGRESS_PERCENT,
              startedAt: new Date(),
              details: config.getDetails?.(event)
            };

            return [...prev, newNotification];
          }
          return prev;
        }

        // Only complete if the notification has not already reached a terminal state, and only
        // when this event actually belongs to the card in the slot - a queued op's waiting card
        // must not be completed (and auto-dismissed) by a DIFFERENT op of the same type finishing.
        if (isTerminalNotificationStatus(existing.status) || !eventTargetsCard(existing, event)) {
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              status: 'completed' as const,
              message: config.getCompletedMessage?.(event) ?? 'Operation completed',
              progress: FULL_PROGRESS_PERCENT,
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

        // If already terminal, just ensure it gets dismissed. A failure from a DIFFERENT op of
        // this type must not fail a queued op's waiting card either.
        if (isTerminalNotificationStatus(existing.status) || !eventTargetsCard(existing, event)) {
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

        // Ignore late progress events on a terminal card (prevents duplicates after completion),
        // and progress from a DIFFERENT operation of this type: while one op runs, the wait-queue
        // can park a SECOND op's waiting card in this same slot, and promoting it here would
        // overwrite its operationId so the X button would cancel the wrong operation.
        if (
          existing &&
          (isTerminalNotificationStatus(existing.status) || !eventTargetsCard(existing, event))
        ) {
          return prev;
        }

        if (existing) {
          const eventDetails = config.getDetails?.(event);
          return prev.map((n) => {
            if (n.id === notificationId) {
              return {
                ...n,
                status: promoteStatus(n.status),
                message: config.getMessage(event),
                progress: config.getProgress(event),
                ...(config.getDetailMessage && {
                  detailMessage: config.getDetailMessage(event)
                }),
                ...(config.getProgressMode && {
                  progressMode: config.getProgressMode(event)
                }),
                ...(config.getProgressAriaValueText && {
                  progressAriaValueText: config.getProgressAriaValueText(event)
                }),
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
            ...(config.getDetailMessage && {
              detailMessage: config.getDetailMessage(event)
            }),
            ...(config.getProgressMode && {
              progressMode: config.getProgressMode(event)
            }),
            ...(config.getProgressAriaValueText && {
              progressAriaValueText: config.getProgressAriaValueText(event)
            }),
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
          progress: FULL_PROGRESS_PERCENT,
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
              progress: FULL_PROGRESS_PERCENT,
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
            progress: FULL_PROGRESS_PERCENT,
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
            progress: FULL_PROGRESS_PERCENT,
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
                progress: FULL_PROGRESS_PERCENT,
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
      i18n.t(GENERIC_FAILURE_I18N_KEY);
    const requiresFullScan =
      errorMessage.includes('change gap is too large') ||
      errorMessage.includes('requires full scan') ||
      errorMessage.includes('requires a full scan');

    if (requiresFullScan) {
      window.dispatchEvent(
        new CustomEvent(APP_EVENTS.SHOW_FULL_SCAN_MODAL, { detail: { error: errorMessage } })
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
          progress: FULL_PROGRESS_PERCENT
        };
        return [...prev, newNotification];
      }

      // Update existing notification
      return prev.map((n) =>
        n.id === notificationId
          ? {
              ...n,
              status: 'failed' as const,
              error: errorMessage,
              progress: FULL_PROGRESS_PERCENT
            }
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
