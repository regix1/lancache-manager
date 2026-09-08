/**
 * Generic notification handler registration hook.
 * Loops through the notification registry and creates/registers SignalR handlers
 * for all standard lifecycle notification types using the existing factory functions.
 */

import { useEffect, useRef } from 'react';
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
  operationCardId,
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
      canControl: (event: unknown) => {
        const fields = event as { operationId?: unknown; serviceId?: unknown };
        return (
          entry.cancelKind !== 'none' &&
          entry.cancelKind !== 'clientQueue' &&
          typeof fields.operationId === 'string' &&
          (entry.type !== 'scheduled_prefill' || typeof fields.serviceId === 'string')
        );
      },
      defaultMessage: started.defaultMessage,
      getMessage: started.getMessage,
      getDetails: (event: unknown) => ({
        ...started.getDetails?.(event),
        ...(entry.type === 'scheduled_prefill'
          ? {
              service: (event as { serviceId?: string }).serviceId,
              operationId: (event as { operationId?: string }).operationId
            }
          : {})
      }),
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
      canControl: (event: unknown) => {
        const fields = event as { operationId?: unknown; serviceId?: unknown };
        return (
          entry.cancelKind !== 'none' &&
          entry.cancelKind !== 'clientQueue' &&
          typeof fields.operationId === 'string' &&
          (entry.type !== 'scheduled_prefill' || typeof fields.serviceId === 'string')
        );
      },
      getMessage: progress.getMessage,
      getProgress: progress.getProgress,
      getDetailMessage: progress.getDetailMessage,
      getProgressMode: progress.getProgressMode,
      getProgressAriaValueText: progress.getProgressAriaValueText,
      getStatus: progress.getStatus,
      getCompletedMessage: progress.getCompletedMessage,
      getErrorMessage: progress.getErrorMessage,
      supportFastCompletion: progress.supportFastCompletion,
      getDetails: (event: unknown) => ({
        ...progress.getDetails?.(event),
        ...(entry.type === 'scheduled_prefill'
          ? {
              service: (event as { serviceId?: string }).serviceId,
              operationId: (event as { operationId?: string }).operationId
            }
          : {})
      })
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
  const acknowledgedIds = useRef(new Set<string>());

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
      if (acknowledgedIds.current.has(`terminal_${event.operationId}`)) return;
      if (event.silent) {
        if (entry.type !== 'scheduled_prefill')
          setNotifications((prev) => {
            if (findBulkCardOwningOperation(entry.type, event.operationId, prev)) return prev;
            const existing = prev.find(
              (n) => n.type !== 'generic' && n.details?.operationId === event.operationId
            );
            if (existing && existing.status !== 'waiting' && existing.status !== 'cancelling')
              return prev;
            const id = existing?.id ?? operationCardId(event.operationId);
            cancelAutoDismissTimer(id);
            return [
              ...prev.filter((n) => n.id !== id),
              {
                ...existing,
                id,
                type: entry.type,
                controlOnly: true,
                status: existing?.details?.cancelRequested
                  ? ('cancelling' as const)
                  : ('waiting' as const),
                message: event.name,
                startedAt: existing?.startedAt ?? new Date(),
                details: { ...existing?.details, operationId: event.operationId }
              }
            ];
          });
        const id = `queued_${event.operationId}`;
        if (event.acknowledge === false || acknowledgedIds.current.has(id)) return;
        acknowledgedIds.current.add(id);
        createCompletionHandler<OperationWaitingEvent & { success: boolean; status: string }>(
          {
            type: 'generic',
            getId: (queued) => `queued_${queued.operationId}`,
            storageKey: '',
            getSuccessMessage: (queued) =>
              i18n.t('management.schedules.queuedUntilCacheFreeNamed', { name: queued.name }),
            getSuccessDetails: (queued) => ({
              operationId: queued.operationId,
              notificationType: 'warning'
            })
          },
          setNotifications,
          scheduleAutoDismiss
        )({ ...event, success: true, status: 'skipped' });
        return;
      }
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
        // A run told to keep its cards to itself still says it was queued, or no card at the
        // scheduled time reads as the run having been dropped. It says it once and the card times
        // out, where the purple card stays up until the blocker finishes and carries a cancel X.
        // The blocker is not named: this reader asked not to be kept posted on it.
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
      acknowledgedIds.current.add(`terminal_${event.operationId}`);
      setNotifications((prev) => {
        const existing = prev.find(
          (n) => n.type !== 'generic' && n.details?.operationId === event.operationId
        );
        const restored = prev.map((n) =>
          n.status === 'waiting' && n.details?.currentOperationId === event.operationId
            ? { ...n, status: 'running' as const }
            : n
        );
        if (event.promoted) {
          const without = restored.filter((n) => n !== existing);
          if (
            !existing?.controlOnly ||
            !event.nextOperationId ||
            without.some((n) => n.details?.operationId === event.nextOperationId)
          )
            return without;
          return [
            ...without,
            {
              ...existing,
              id: operationCardId(event.nextOperationId),
              status:
                event.nextStatus === 'waiting'
                  ? ('waiting' as const)
                  : event.nextStatus === 'cancelling'
                    ? ('cancelling' as const)
                    : ('running' as const),
              details: {
                ...existing.details,
                operationId: event.nextOperationId,
                cancelPending: false,
                cancelRequested: event.nextStatus === 'cancelling',
                cancelSent: false
              }
            }
          ];
        }
        if (event.cancelled || event.skipped) {
          if (!existing) return restored;
          if (existing.controlOnly) return restored.filter((n) => n.id !== existing.id);
          scheduleAutoDismiss(existing.id);
          return restored.map((n) =>
            n.id === existing.id
              ? {
                  ...n,
                  status: event.cancelled ? ('cancelled' as const) : ('skipped' as const),
                  message: event.cancelled
                    ? i18n.t('common.notifications.operationWaitingCancelled')
                    : (event.error ?? i18n.t(GENERIC_SKIPPED_I18N_KEY)),
                  error: undefined,
                  details: { ...n.details, cancelled: event.cancelled }
                }
              : n
          );
        }
        const id = existing?.id ?? operationCardId(event.operationId);
        const message = event.error ?? i18n.t(GENERIC_FAILURE_I18N_KEY);
        scheduleAutoDismiss(id);
        return [
          ...restored.filter((n) => n.id !== id),
          {
            ...existing,
            id,
            type: entry.type,
            status: 'failed' as const,
            controlOnly: undefined,
            message,
            error: message,
            startedAt: existing?.startedAt ?? new Date(),
            details: { ...existing?.details, operationId: event.operationId }
          }
        ];
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
