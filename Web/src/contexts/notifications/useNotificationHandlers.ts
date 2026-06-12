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
  createCompletionHandler
} from './handlerFactories';
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
      getId: () => entry.id,
      storageKey: entry.storageKey,
      defaultMessage: started.defaultMessage,
      getMessage: started.getMessage,
      getDetails: started.getDetails,
      replaceExisting: started.replaceExisting
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
      getId: () => entry.id,
      storageKey: entry.storageKey,
      getMessage: progress.getMessage,
      getProgress: progress.getProgress,
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
      getId: () => entry.id,
      storageKey: entry.storageKey,
      getSuccessMessage: entry.complete.getSuccessMessage,
      getSuccessDetails: entry.complete.getSuccessDetails,
      getDetailMessage: entry.complete.getDetailMessage,
      getFailureMessage: entry.complete.getFailureMessage,
      getCancelledMessage: entry.complete.getCancelledMessage,
      getCancelledDetails: entry.complete.getCancelledDetails,
      useAnimationDelay: entry.complete.useAnimationDelay,
      getFastCompletionId: entry.complete.getFastCompletionId
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
      // Special-wiring entries are metadata-only (cancelKind + recovery); their
      // SignalR handlers are hand-built in createSpecialCaseHandlers and wired
      // via SPECIAL_NOTIFICATION_CONTRACTS. Skip them here to avoid
      // double-subscribing. They carry no `events`/`started`/`progress`.
      if (entry.wiring !== 'standard' || !entry.events || !entry.started || !entry.progress) {
        continue;
      }

      // Started handler
      const startedHandler = buildStartedHandler(
        entry,
        entry.started,
        setNotifications,
        cancelAutoDismissTimer
      );
      subscribe(entry.events.started, startedHandler);

      // Progress handler
      const progressHandler = buildProgressHandler(
        entry,
        entry.progress,
        setNotifications,
        scheduleAutoDismiss,
        cancelAutoDismissTimer
      );
      subscribe(entry.events.progress, progressHandler);

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
    // on refresh. On promotion the promoted op's own Started event replaces this card
    // (same per-type singleton id) - no handler needed for the promotion case.
    const waitingHandler = (event: OperationWaitingEvent): void => {
      const entry = findEntryForWireType(registry, event.operationType);
      if (!entry) return;
      cancelAutoDismissTimer(entry.id);
      setNotifications((prev: UnifiedNotification[]) => {
        const filtered = prev.filter((n) => n.id !== entry.id);
        const waitingNotification: UnifiedNotification = {
          id: entry.id,
          type: entry.type,
          status: 'waiting',
          // Prefix the op's display name so several FIFO-queued cards stay distinguishable.
          message: event.name
            ? i18n.t('common.notifications.operationWaitingNamed', { name: event.name })
            : i18n.t('common.notifications.operationWaiting'),
          startedAt: new Date(),
          details: { operationId: event.operationId }
        };
        return [...filtered, waitingNotification];
      });
    };

    const waitingCompleteHandler = (event: OperationWaitingCompleteEvent): void => {
      const entry = findEntryForWireType(registry, event.operationType);
      if (!entry) return;
      setNotifications((prev: UnifiedNotification[]) =>
        prev.map((n) => {
          // Guard: only terminate cards STILL waiting - if promotion already replaced the
          // card with a running one, a late cancel/failure event must not clobber it.
          if (n.id !== entry.id || n.status !== 'waiting') return n;
          return event.cancelled
            ? {
                ...n,
                status: 'completed' as const,
                message: i18n.t('common.notifications.operationWaitingCancelled'),
                details: { ...n.details, cancelled: true }
              }
            : {
                ...n,
                status: 'failed' as const,
                message: event.error ?? i18n.t('signalr.generic.failed'),
                error: event.error
              };
        })
      );
      scheduleAutoDismiss(entry.id);
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
