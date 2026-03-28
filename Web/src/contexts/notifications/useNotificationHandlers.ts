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
  NotificationRegistryEntry
} from './types';
import {
  createStartedHandler,
  createStatusAwareProgressHandler,
  createCompletionHandler
} from './handlerFactories';
import { useSignalR } from '../SignalRContext/useSignalR';

/**
 * Creates a started handler for a registry entry and returns the bound handler function.
 */
function buildStartedHandler(
  entry: NotificationRegistryEntry,
  setNotifications: SetNotifications,
  cancelAutoDismissTimer: CancelAutoDismissTimer
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
): (event: any) => void {
  return createStartedHandler(
    {
      type: entry.type,
      getId: () => entry.id,
      storageKey: entry.storageKey,
      defaultMessage: entry.started.defaultMessage,
      getMessage: entry.started.getMessage,
      getDetails: entry.started.getDetails,
      replaceExisting: entry.started.replaceExisting
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
      getMessage: entry.progress.getMessage,
      getProgress: entry.progress.getProgress,
      getStatus: entry.progress.getStatus,
      getCompletedMessage: entry.progress.getCompletedMessage,
      getErrorMessage: entry.progress.getErrorMessage,
      supportFastCompletion: entry.progress.supportFastCompletion,
      getDetails: entry.progress.getDetails
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
      useAnimationDelay: entry.complete.useAnimationDelay,
      supportFastCompletion: entry.complete.supportFastCompletion,
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
      // Started handler
      const startedHandler = buildStartedHandler(entry, setNotifications, cancelAutoDismissTimer);
      subscribe(entry.events.started, startedHandler);

      // Progress handler
      const progressHandler = buildProgressHandler(
        entry,
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
