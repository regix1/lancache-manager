/**
 * Generic notification handler registration hook.
 * Loops through the notification registry and creates/registers SignalR handlers
 * for all standard lifecycle notification types using the existing factory functions.
 */

import { useEffect } from 'react';
import type {
  SetNotifications,
  ScheduleAutoDismiss,
  CancelAutoDismissTimer,
  RemoveNotification,
  NotificationRegistryEntry,
  UnifiedNotification
} from './types';
import {
  createStartedHandler,
  createStatusAwareProgressHandler,
  createCompletionHandler,
  eventTargetsCard,
  findBulkCardOwningOperation,
  waitingCardMessage
} from './handlers';
import { isTerminalNotificationStatus } from './notificationStatus';
import { useSignalR } from '../SignalRContext/useSignalR';
import type { OperationWaitingEvent, OperationWaitingCompleteEvent } from '../SignalRContext/types';
import {
  GENERIC_FAILURE_I18N_KEY,
  GENERIC_SKIPPED_I18N_KEY,
  OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE
} from './constants';
import i18n from '@/i18n';

/**
 * Resolves the registry entry whose per-type singleton card a wait-queue event targets.
 * Returns undefined for wire types without a standard notification card.
 */
function findEntryForWireType(
  registry: NotificationRegistryEntry[],
  wireType: string
): NotificationRegistryEntry | undefined {
  const notificationType = OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[wireType];
  if (!notificationType) return undefined;
  return registry.find((entry) => entry.type === notificationType);
}

/**
 * Creates a started handler for a registry entry and returns the bound handler function.
 */
function buildStartedHandler(
  entry: NotificationRegistryEntry,
  started: NonNullable<NotificationRegistryEntry['started']>,
  setNotifications: SetNotifications,
  cancelAutoDismissTimer: CancelAutoDismissTimer
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
): (event: any) => void {
  return createStartedHandler(
    {
      type: entry.type,
      getId: (event: unknown) => entry.getId?.(event) ?? entry.id,
      storageKey: entry.storageKey,
      storesCardsById: entry.getId !== undefined,
      shouldDisplay: started.shouldDisplay,
      defaultMessage: started.defaultMessage,
      getMessage: started.getMessage,
      getDetails: started.getDetails,
      replaceExisting: started.replaceExisting,
      progressMode: started.progressMode
    },
    setNotifications,
    cancelAutoDismissTimer
  );
}

/**
 * Creates a status-aware progress handler for a registry entry.
 */
function buildProgressHandler(
  entry: NotificationRegistryEntry,
  progress: NonNullable<NotificationRegistryEntry['progress']>,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
): (event: any) => void {
  return createStatusAwareProgressHandler(
    {
      type: entry.type,
      getId: (event: unknown) => entry.getId?.(event) ?? entry.id,
      storageKey: entry.storageKey,
      storesCardsById: entry.getId !== undefined,
      shouldDisplay: progress.shouldDisplay,
      getMessage: progress.getMessage,
      getProgress: progress.getProgress,
      getDetailMessage: progress.getDetailMessage,
      getProgressMode: progress.getProgressMode,
      getProgressAriaValueText: progress.getProgressAriaValueText,
      getStatus: progress.getStatus,
      getCompletedMessage: progress.getCompletedMessage,
      getErrorMessage: progress.getErrorMessage,
      supportFastCompletion: progress.supportFastCompletion,
      getDetails: progress.getDetails
    },
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer
  );
}

/**
 * Creates a completion handler for a registry entry, optionally wrapping it
 * with an onComplete callback.
 */
function buildCompleteHandler(
  entry: NotificationRegistryEntry,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  removeNotification: RemoveNotification
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
): ((event: any) => void) | null {
  if (!entry.complete) return null;

  const baseHandler = createCompletionHandler(
    {
      type: entry.type,
      getId: (event: unknown) => entry.getId?.(event) ?? entry.id,
      storageKey: entry.storageKey,
      storesCardsById: entry.getId !== undefined,
      shouldDisplay: entry.complete.shouldDisplay,
      getSuccessMessage: entry.complete.getSuccessMessage,
      getSuccessDetails: entry.complete.getSuccessDetails,
      getDetailMessage: entry.complete.getDetailMessage,
      getFailureMessage: entry.complete.getFailureMessage,
      getCancelledMessage: entry.complete.getCancelledMessage,
      getCancelledDetails: entry.complete.getCancelledDetails,
      succeeded: entry.complete.succeeded,
      announcement: !entry.started && !entry.progress,
      dismissDelayMs: entry.complete.dismissDelayMs,
      useAnimationDelay: entry.complete.useAnimationDelay
    },
    setNotifications,
    scheduleAutoDismiss
  );

  if (entry.onComplete) {
    const onCompleteCb = entry.onComplete;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    return (event: any): void => {
      baseHandler(event);
      onCompleteCb(removeNotification);
    };
  }

  return baseHandler;
}

/**
 * Hook that registers SignalR event handlers for all standard notification types
 * defined in the notification registry. Handles subscription and cleanup lifecycle.
 *
 * This hook is intended to be called once from NotificationsProvider, replacing
 * the manual handler creation and signalR.on/off calls for the 11 standard types.
 *
 * @param registry - The array of notification registry entries to register
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal of notifications
 * @param cancelAutoDismissTimer - Function to cancel pending auto-dismiss timers
 * @param removeNotification - Function to remove a notification by ID
 */
export function useNotificationHandlers(
  registry: NotificationRegistryEntry[],
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer,
  removeNotification: RemoveNotification
): void {
  const signalR = useSignalR();

  useEffect(() => {
    // Track all subscriptions for cleanup
    const subscriptions: { eventName: string; handler: (...args: unknown[]) => void }[] = [];

    function subscribe(eventName: string, handler: (...args: unknown[]) => void): void {
      signalR.on(eventName, handler);
      subscriptions.push({ eventName, handler });
    }

    for (const entry of registry) {
      // An entry that declares no lifecycle events is metadata-only (cancelKind +
      // recovery); its card is created by client code, so there is nothing here to
      // subscribe.
      if (!entry.events) {
        continue;
      }

      // Each phase is subscribed only where the entry declares it: an announcement whose single
      // event is already terminal has no start to open a card for and no progress to report.
      if (entry.events.started && entry.started) {
        const startedHandler = buildStartedHandler(
          entry,
          entry.started,
          setNotifications,
          cancelAutoDismissTimer
        );
        subscribe(entry.events.started, startedHandler);
      }

      if (entry.events.progress && entry.progress) {
        const progressHandler = buildProgressHandler(
          entry,
          entry.progress,
          setNotifications,
          scheduleAutoDismiss,
          cancelAutoDismissTimer
        );
        subscribe(entry.events.progress, progressHandler);
      }

      // Complete handler (optional - some types rely solely on status-aware progress)
      const completeHandler = buildCompleteHandler(
        entry,
        setNotifications,
        scheduleAutoDismiss,
        removeNotification
      );
      if (completeHandler) {
        subscribe(entry.events.complete, completeHandler);
      }
    }

    // ───── Operation wait-queue (purple waiting cards) ─────
    // A queued op is a REAL tracker registration (status Waiting) so this card is never a
    // ghost: cancel works via details.operationId, and /api/operations/waiting recovers it
    // on refresh. On promotion the promoted op's own Started event normally replaces this
    // card (same per-type singleton id); the completion handoff removes it when that operation
    // is intentionally notification-silent.
    const waitingHandler = (event: OperationWaitingEvent): void => {
      const entry = findEntryForWireType(registry, event.operationType);
      if (!entry) return;
      setNotifications((prev: UnifiedNotification[]) => {
        // A batch that owns this item type already has a card on screen, so a second card
        // would just repeat it. The blocker name is the one thing that card does not know,
        // so move this message onto it rather than dropping the event: without it the batch
        // card sits at "Removing 1 of 2" with no sign that the item is parked behind
        // another operation. The batch restores its own message when the item is promoted.
        const owningBulk = findBulkCardOwningOperation(entry.type, event.operationId, prev);
        if (owningBulk) {
          return prev.map((n) =>
            n.id === owningBulk.id
              ? {
                  ...n,
                  status: 'waiting' as const,
                  message: waitingCardMessage(event),
                  // Record which operation the card now speaks for. A batch whose item request is
                  // still on the wire has no id to compare against, and it must claim only one
                  // queued operation on the strength of that: the next one of the same type is a
                  // different operation and needs its own card.
                  details: { ...n.details, currentOperationId: event.operationId }
                }
              : n
          );
        }
        // The slot may already hold ANOTHER operation's live card - two operations of one type
        // are exactly what puts this one in the queue. Replacing it would take away the running
        // operation's progress and point its X at this queued operation instead. A card whose
        // own operation has finished is not live state, so the queued op still takes that slot
        // rather than going unreported.
        const slotCard = prev.find((n) => n.id === entry.id);
        if (
          slotCard &&
          !isTerminalNotificationStatus(slotCard.status) &&
          !eventTargetsCard(slotCard, event)
        ) {
          return prev;
        }
        // Only once this event is going to replace the card in that slot: a terminal card
        // already sitting there would otherwise lose its auto-dismiss timer and stay on
        // screen forever when the update below is skipped.
        cancelAutoDismissTimer(entry.id);
        // A re-emit for the SAME queued op announces a new blocker; keep the original
        // startedAt so the card's age does not reset every time the blocker changes.
        const existing =
          slotCard?.status === 'waiting' && slotCard.details?.operationId === event.operationId
            ? slotCard
            : undefined;
        const filtered = prev.filter((n) => n.id !== entry.id);
        const waitingNotification: UnifiedNotification = {
          id: entry.id,
          type: entry.type,
          status: 'waiting',
          message: waitingCardMessage(event),
          startedAt: existing?.startedAt ?? new Date(),
          details: { operationId: event.operationId }
        };
        return [...filtered, waitingNotification];
      });
    };

    const waitingCompleteHandler = (event: OperationWaitingCompleteEvent): void => {
      const entry = findEntryForWireType(registry, event.operationType);
      if (!entry) return;

      if (event.promoted) {
        setNotifications((prev: UnifiedNotification[]) =>
          prev.filter(
            (n) =>
              n.id !== entry.id ||
              n.status !== 'waiting' ||
              n.details?.operationId !== event.operationId
          )
        );
        return;
      }

      setNotifications((prev: UnifiedNotification[]) => {
        let terminated = false;
        let restored = false;
        const next = prev.map((n) => {
          // A batch card turns purple while its own item is parked, and it carries that item's
          // id in details.currentOperationId, never in the per-type card's slot. The batch run
          // is not over just because one item left the queue, so put the card back to running
          // and leave the wording to the batch, which rewrites it for the item either way.
          if (n.status === 'waiting' && n.details?.currentOperationId === event.operationId) {
            restored = true;
            return { ...n, status: 'running' as const };
          }
          // Guard: only terminate cards STILL waiting - if promotion already replaced the
          // card with a running one, a late cancel/failure event must not clobber it.
          if (
            n.id !== entry.id ||
            n.status !== 'waiting' ||
            n.details?.operationId !== event.operationId
          )
            return n;
          terminated = true;
          if (event.cancelled) {
            return {
              ...n,
              status: 'completed' as const,
              message: i18n.t('common.notifications.operationWaitingCancelled'),
              details: { ...n.details, cancelled: true }
            };
          }
          // A run declined at promotion never started, so it is neither a success nor a
          // failure. Its reason rides in `error` like the failure path; dropping the card
          // instead would leave the reader with no trace of what happened.
          if (event.skipped) {
            return {
              ...n,
              status: 'skipped' as const,
              message: event.error ?? i18n.t(GENERIC_SKIPPED_I18N_KEY),
              error: event.error
            };
          }
          return {
            ...n,
            status: 'failed' as const,
            message: event.error ?? i18n.t(GENERIC_FAILURE_I18N_KEY),
            error: event.error
          };
        });
        // Nothing became terminal, so there is nothing to time out. Arming it regardless put a
        // dismiss timer on whatever else happened to be in that slot.
        if (terminated) {
          scheduleAutoDismiss(entry.id);
        }
        // This runs on every wait-queue completion, most of which belong to a card nobody here is
        // showing. Handing back the same array leaves the list identity alone so those do not
        // re-render every card.
        return terminated || restored ? next : prev;
      });
    };

    subscribe('OperationWaiting', waitingHandler as (...args: unknown[]) => void);
    subscribe('OperationWaitingComplete', waitingCompleteHandler as (...args: unknown[]) => void);

    return () => {
      for (const { eventName, handler } of subscriptions) {
        signalR.off(eventName, handler);
      }
    };
  }, [
    registry,
    signalR,
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer,
    removeNotification
  ]);
}
