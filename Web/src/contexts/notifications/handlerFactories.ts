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
 * Text for an operation parked in the wait queue. Two things describe that state and carry the same
 * two fields: the live `OperationWaiting` push and a row from the `/api/operations/waiting`
 * reconciliation. They must word it identically, or the sentence changes when a reconnect swaps one
 * source for the other.
 */
export function waitingCardMessage(source: {
  name?: string;
  blockedByName?: string | null;
}): string {
  if (source.name) {
    return source.blockedByName
      ? i18n.t(OPERATION_WAITING_I18N_KEYS.NAMED_BLOCKED, {
          name: source.name,
          blocker: source.blockedByName
        })
      : i18n.t(OPERATION_WAITING_I18N_KEYS.NAMED, { name: source.name });
  }
  return source.blockedByName
    ? i18n.t(OPERATION_WAITING_I18N_KEYS.BLOCKED, { blocker: source.blockedByName })
    : i18n.t(OPERATION_WAITING_I18N_KEYS.DEFAULT);
}

/** Skip opening a new per-item singleton while the bulk card whose items produce it owns progress. */
export function findBulkCardOwningType(
  type: NotificationType,
  notifications: UnifiedNotification[]
): UnifiedNotification | undefined {
  return notifications.find(
    (notification) =>
      notification.type === 'bulk_removal' &&
      // 'waiting' as well as 'running': the card turns purple while its current item is parked
      // behind another operation, and it still owns the per-item cards for the whole run. Matching
      // only 'running' would let a second card appear for the item it is already reporting on.
      (notification.status === 'running' || notification.status === 'waiting') &&
      notification.details?.itemTypes?.includes(type) === true
  );
}

function suppressNewItemCardDuringBulk(
  type: NotificationType,
  notifications: UnifiedNotification[]
): boolean {
  return findBulkCardOwningType(type, notifications) !== undefined;
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
 * A known operation-id mismatch is always rejected, including for a running card. When either side
 * does not yet have an operation id, a running card keeps the historical type-level fallback; any
 * other card is touched ONLY when the event provably belongs to it (matching operationId).
 */
function eventTargetsCard(existing: UnifiedNotification, event: unknown): boolean {
  const cardOperationId = existing.details?.operationId;
  const incomingOperationId = eventOperationId(event);
  if (cardOperationId && incomingOperationId && cardOperationId !== incomingOperationId) {
    return false;
  }
  if (existing.status === 'running') return true;
  return Boolean(cardOperationId && incomingOperationId && cardOperationId === incomingOperationId);
}

function clearPersistedNotificationIfTargeted(storageKey: string, event: unknown): boolean {
  const persisted = localStorage.getItem(storageKey);
  if (!persisted) {
    return true;
  }

  try {
    const notification = JSON.parse(persisted) as UnifiedNotification;
    if (!eventTargetsCard(notification, event)) {
      return false;
    }
  } catch {
    return false;
  }

  localStorage.removeItem(storageKey);
  return true;
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
  CANCELLED_NOTIFICATION_DELAY_MS,
  FULL_PROGRESS_PERCENT,
  GENERIC_CANCELLED_I18N_KEY,
  GENERIC_COMPLETION_I18N_KEY,
  GENERIC_FAILURE_I18N_KEY,
  LIVE_ONLY_CANCEL_DETAIL_KEYS,
  OPERATION_WAITING_I18N_KEYS
} from './constants';

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
  /**
   * Progress semantics for the card this handler creates. Left unset the card is determinate at
   * the 0% below, which is a lie for an operation that starts by waiting on a person.
   */
  progressMode?: NotificationProgressMode;
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
      const idsToRemove = new Set([notificationId, ...(config.additionalIdsToRemove ?? [])]);
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((notification) => notification.id === notificationId);
        if (
          (existing && !eventTargetsCard(existing, event)) ||
          !clearPersistedNotificationIfTargeted(config.storageKey, event)
        ) {
          return prev;
        }

        cancelAutoDismissTimer?.(notificationId);
        return prev.filter((notification) => !idsToRemove.has(notification.id));
      });
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

      if (suppressNewItemCardDuringBulk(config.type, prev)) {
        return prev;
      }

      const newNotification: UnifiedNotification = {
        id: notificationId,
        type: config.type,
        status: 'running',
        message: config.getMessage?.(event) ?? config.defaultMessage,
        startedAt: new Date(),
        progress: 0,
        progressMode: config.progressMode,
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
    /** Wire status. Only `'skipped'` is read here; every other outcome is decided by success/cancelled. */
    status?: string;
  }
>(
  config: CompletionHandlerConfig<T>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): (event: T) => void {
  return (event: T): void => {
    const notificationId = config.getId(event);

    if (config.shouldDisplay?.(event) === false) {
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((notification) => notification.id === notificationId);
        if (
          (existing && !eventTargetsCard(existing, event)) ||
          !clearPersistedNotificationIfTargeted(config.storageKey, event)
        ) {
          return prev;
        }

        return prev.filter((notification) => notification.id !== notificationId);
      });
      return;
    }

    const isCancelled = event.cancelled === true;
    // A skipped run reports success:true (it did not fail) and status:'skipped', so the outcome
    // can only be read off the wire status. Checked before the success branches below.
    const isSkipped = event.status === 'skipped' && !isCancelled;

    /**
     * A skipped run's own stage key already names the reason it did nothing, so the card reuses
     * the same resolver a successful run uses. It never falls back to the failure text.
     */
    const resolveSkippedMessage = (existing?: UnifiedNotification): string =>
      config.getSuccessMessage?.(event, existing) ??
      (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
      existing?.message ??
      i18n.t(GENERIC_COMPLETION_I18N_KEY);

    const resolveFailureMessage = (existing?: UnifiedNotification): string => {
      if (isCancelled) {
        return (
          config.getCancelledMessage?.(event, existing) ??
          event.message ??
          (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
          i18n.t(GENERIC_CANCELLED_I18N_KEY)
        );
      }

      return (
        config.getFailureMessage?.(event) ??
        (event.stageKey ? i18n.t(event.stageKey, event.context ?? {}) : undefined) ??
        i18n.t(GENERIC_FAILURE_I18N_KEY)
      );
    };

    // Track the ID to schedule (may be different for fast completion)
    let idToSchedule = notificationId;

    /** Builds a terminal card for fast completion (no prior started event). */
    const buildFastCompletionNotification = (): UnifiedNotification => {
      const fastId = config.getFastCompletionId?.(event) ?? notificationId;
      idToSchedule = fastId;
      const failureMessage = resolveFailureMessage();

      if (isSkipped) {
        return {
          id: fastId,
          type: config.type,
          status: 'skipped' as const,
          message: resolveSkippedMessage(),
          detailMessage: config.getDetailMessage?.(event),
          startedAt: new Date(),
          // No progress at all: a run that did nothing has nothing to fill a bar with.
          details: config.getSuccessDetails?.(event)
        };
      }

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
        if (!clearPersistedNotificationIfTargeted(config.storageKey, event)) {
          return prev;
        }

        // Fast completion - no live slot to transition (missing or already terminal);
        // materialize a terminal card instead of dropping the event
        if (!existing || isTerminalNotificationStatus(existing.status)) {
          if (suppressNewItemCardDuringBulk(config.type, prev)) {
            return prev;
          }
          const newNotification = buildFastCompletionNotification();
          scheduleAutoDismiss(
            idToSchedule,
            isCancelled ? CANCELLED_NOTIFICATION_DELAY_MS : undefined
          );
          return [...prev.filter((n) => n.id !== newNotification.id), newNotification];
        }

        scheduleAutoDismiss(
          idToSchedule,
          isCancelled ? CANCELLED_NOTIFICATION_DELAY_MS : undefined
        );
        return prev.map((n) => {
          if (n.id === notificationId) {
            if (isSkipped) {
              return {
                ...n,
                // Drop the bar the started event seeded at 0 rather than filling it to 100.
                progress: undefined,
                status: 'skipped' as const,
                message: resolveSkippedMessage(n),
                error: undefined,
                details: {
                  ...n.details,
                  ...config.getSuccessDetails?.(event, n)
                }
              };
            }

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
          if (!clearPersistedNotificationIfTargeted(config.storageKey, event)) {
            return prev;
          }
          if (suppressNewItemCardDuringBulk(config.type, prev)) {
            return prev;
          }
          const newNotification = buildFastCompletionNotification();
          scheduleAutoDismiss(
            idToSchedule,
            isCancelled ? CANCELLED_NOTIFICATION_DELAY_MS : undefined
          );
          return [...prev, newNotification];
        }

        // Never touch a card belonging to a DIFFERENT operation of this type (a queued op's
        // waiting card parked in the shared slot), and never re-terminate a terminal card.
        if (!eventTargetsCard(existing, event) || isTerminalNotificationStatus(existing.status)) {
          return prev;
        }
        if (!clearPersistedNotificationIfTargeted(config.storageKey, event)) {
          return prev;
        }

        scheduleAutoDismiss(
          idToSchedule,
          isCancelled ? CANCELLED_NOTIFICATION_DELAY_MS : undefined
        );
        return prev.map((n) => {
          if (n.id === notificationId) {
            if (isSkipped) {
              return {
                ...n,
                // Drop the bar the started event seeded at 0 rather than filling it to 100.
                progress: undefined,
                status: 'skipped' as const,
                message: resolveSkippedMessage(n),
                error: undefined,
                detailMessage: config.getDetailMessage?.(event) ?? n.detailMessage,
                details: {
                  ...n.details,
                  ...config.getSuccessDetails?.(event, n)
                }
              };
            }

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
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((notification) => notification.id === notificationId);
        if (
          (existing && !eventTargetsCard(existing, event)) ||
          !clearPersistedNotificationIfTargeted(config.storageKey, event)
        ) {
          return prev;
        }

        cancelAutoDismissTimer?.(notificationId);
        return prev.filter((notification) => notification.id !== notificationId);
      });
      return;
    }

    const status = config.getStatus(event);

    if (status?.toLowerCase() === 'completed') {
      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          if (!clearPersistedNotificationIfTargeted(config.storageKey, event)) {
            return prev;
          }
          if (suppressNewItemCardDuringBulk(config.type, prev)) {
            return prev;
          }
          // Fast completion - notification doesn't exist yet (operation completed before UI created it)
          if (config.supportFastCompletion) {
            const newNotification: UnifiedNotification = {
              id: notificationId,
              type: config.type,
              status: 'completed' as const,
              message: config.getCompletedMessage?.(event) ?? i18n.t(GENERIC_COMPLETION_I18N_KEY),
              progress: FULL_PROGRESS_PERCENT,
              startedAt: new Date(),
              details: config.getDetails?.(event)
            };

            scheduleAutoDismiss(notificationId);
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
        if (!clearPersistedNotificationIfTargeted(config.storageKey, event)) {
          return prev;
        }

        scheduleAutoDismiss(notificationId);
        return prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              status: 'completed' as const,
              message: config.getCompletedMessage?.(event) ?? i18n.t(GENERIC_COMPLETION_I18N_KEY),
              progress: FULL_PROGRESS_PERCENT,
              details: { ...n.details, ...config.getDetails?.(event) }
            };
          }
          return n;
        });
      });
    } else if (status?.toLowerCase() === 'failed') {
      const errorMessage = config.getErrorMessage?.(event) ?? i18n.t(GENERIC_FAILURE_I18N_KEY);

      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        // If notification doesn't exist, nothing to do
        if (!existing) {
          clearPersistedNotificationIfTargeted(config.storageKey, event);
          return prev;
        }

        // An already-terminal card is left to its existing dismiss timer. A failure from a
        // DIFFERENT op of this type must not fail a queued op's waiting card either.
        if (isTerminalNotificationStatus(existing.status) || !eventTargetsCard(existing, event)) {
          return prev;
        }
        if (!clearPersistedNotificationIfTargeted(config.storageKey, event)) {
          return prev;
        }

        scheduleAutoDismiss(notificationId);
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
              const updatedNotification: UnifiedNotification = {
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
              localStorage.setItem(config.storageKey, JSON.stringify(updatedNotification));
              return updatedNotification;
            }
            return n;
          });
        } else {
          if (suppressNewItemCardDuringBulk(config.type, prev)) {
            return prev;
          }
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
