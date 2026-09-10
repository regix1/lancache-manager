/**
 * Generic notification handler registration hook.
 * Loops through the notification registry and creates/registers SignalR handlers
 * for all standard lifecycle notification types using the existing factory functions.
 */

import { useEffect, type RefObject } from 'react';
import type {
  SetNotifications,
  ScheduleAutoDismiss,
  CancelAutoDismissTimer,
  NotificationEvents,
  NotificationRegistryEntry,
  UnifiedNotification
} from './types';
import {
  buildStartedHandler,
  buildProgressHandler,
  buildCompleteHandler,
  createCompletionHandler,
  operationCardId,
  eventTargetsCard,
  findBulkCardOwningOperation,
  waitingCardMessage,
  rememberEvent,
  applyHandoff
} from './handlers';
import { isTerminalNotificationStatus } from './notificationStatus';
import { useSignalR } from '../SignalRContext/useSignalR';
import type { OperationWaitingEvent, OperationWaitingCompleteEvent } from '../SignalRContext/types';
import { OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE } from './constants';
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
 * @param events - Values retained throughout the mounted notification session
 */
export function useNotificationHandlers(
  registry: NotificationRegistryEntry[],
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer,
  events: RefObject<NotificationEvents>,
  recover?: () => void
): void {
  const signalR = useSignalR();
  const acknowledgedIds = events.current.acknowledgedIds;

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
          cancelAutoDismissTimer,
          events.current,
          false,
          scheduleAutoDismiss
        );
        subscribe(entry.events.started, (event: unknown) => {
          startedHandler(event);
          const operationId = (event as { operationId?: string } | null)?.operationId;
          if (!operationId || !recover || events.current.terminals.has(operationId)) return;
          setNotifications((prev) => {
            if (
              prev.some((n) => n.type === entry.type && !isTerminalNotificationStatus(n.status)) &&
              !prev.some((n) => n.type === entry.type && n.details?.operationId === operationId)
            )
              queueMicrotask(recover);
            return prev;
          });
        });
      }

      if (entry.events.progress && entry.progress) {
        const progressHandler = buildProgressHandler(
          entry,
          entry.progress,
          setNotifications,
          scheduleAutoDismiss,
          cancelAutoDismissTimer,
          events.current
        );
        subscribe(entry.events.progress, progressHandler);
      }

      // Complete handler (optional - some types rely solely on status-aware progress)
      const completeHandler = buildCompleteHandler(
        entry,
        setNotifications,
        scheduleAutoDismiss,
        events.current
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
      if (!rememberEvent(events.current, entry.type, 'waiting', 'OperationWaiting', event)) return;
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
            const status = existing?.details?.cancelRequested
              ? ('cancelling' as const)
              : ('waiting' as const);
            if (
              existing?.id === id &&
              existing.type === entry.type &&
              existing.controlOnly === true &&
              existing.status === status &&
              existing.message === event.name &&
              existing.details?.operationId === event.operationId
            )
              return prev;
            cancelAutoDismissTimer(id);
            return [
              ...prev.filter((n) => n.id !== id),
              {
                ...existing,
                id,
                type: entry.type,
                controlOnly: true,
                status,
                message: event.name,
                startedAt: existing?.startedAt ?? new Date(),
                details: { ...existing?.details, operationId: event.operationId }
              }
            ];
          });
        const id = `queued_${event.operationId}`;
        if (event.acknowledge === false || acknowledgedIds.has(id)) return;
        acknowledgedIds.add(id);
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
          const message = waitingCardMessage(event);
          if (
            owningBulk.status === 'waiting' &&
            owningBulk.message === message &&
            owningBulk.details?.currentOperationId === event.operationId
          )
            return prev;
          return prev.map((n) =>
            n.id === owningBulk.id
              ? {
                  ...n,
                  status: 'waiting' as const,
                  message,
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
        // A new blocker changes only the message for the same queued operation. Keep its
        // cancellation state, instance identity, and original start time through re-emits.
        const existing =
          (slotCard?.status === 'waiting' || slotCard?.status === 'cancelling') &&
          slotCard.details?.operationId === event.operationId
            ? slotCard
            : undefined;
        const message = waitingCardMessage(event);
        if (existing) {
          if (existing.message === message) return prev;
          return prev.map((n) => (n === existing ? { ...n, message } : n));
        }
        const filtered = prev.filter((n) => n.id !== entry.id);
        // A run told to keep its cards to itself still says it was queued, or no card at the
        // scheduled time reads as the run having been dropped. It says it once and the card times
        // out, where the purple card stays up until the blocker finishes and carries a cancel X.
        // The blocker is not named: this reader asked not to be kept posted on it.
        const waitingNotification: UnifiedNotification = {
          id: entry.id,
          type: entry.type,
          status: 'waiting',
          message,
          startedAt: new Date(),
          details: { operationId: event.operationId }
        };
        return [...filtered, waitingNotification];
      });
    };

    const waitingCompleteHandler = (event: OperationWaitingCompleteEvent): void => {
      const entry = findEntryForWireType(registry, event.operationType);
      if (!entry) return;
      if (!rememberEvent(events.current, entry.type, 'handoff', 'OperationWaitingComplete', event))
        return;
      setNotifications((prev) =>
        applyHandoff(
          prev,
          event,
          events.current,
          entry,
          scheduleAutoDismiss,
          cancelAutoDismissTimer
        )
      );
      if (
        event.promoted &&
        recover &&
        (!event.nextStatus || !events.current.records.has(event.nextOperationId ?? ''))
      )
        queueMicrotask(recover);
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
    events,
    acknowledgedIds,
    recover
  ]);
}
