/**
 * Subscribes the notification store to the hub, once for the provider's lifetime.
 *
 * Three kinds of message arrive: `OperationUpdated` run rows, which open and end run cards; each
 * registry entry's per-type lifecycle events, which only fill in a run card's text; and the
 * announcements whose single event is the whole story, which raise a card the browser owns.
 */

import { useEffect } from 'react';
import type { DispatchDetail, NotificationRegistryEntry, UnifiedNotification } from './types';
import {
  buildStartedHandler,
  buildProgressHandler,
  buildCompleteHandler,
  buildAnnouncementHandler
} from './handlers';
import { useSignalR } from '../SignalRContext/useSignalR';

/**
 * Registers the hub handlers. Every argument must be stable: a re-subscription would open a gap in
 * which a run row is dropped with nothing to request a snapshot for it.
 *
 * @param registry - The notification registry entries
 * @param dispatchDetail - Hands a per-type event's detail patch to the store
 * @param showAnnouncement - Raises an announcement card
 * @param handleRun - Applies one `OperationUpdated` row
 */
export function useNotificationHandlers(
  registry: NotificationRegistryEntry[],
  dispatchDetail: DispatchDetail,
  showAnnouncement: (card: Omit<UnifiedNotification, 'id' | 'startedAt'>) => void,
  handleRun: (value: unknown) => void
): void {
  const { on, off } = useSignalR();

  useEffect(() => {
    const subscriptions: { eventName: string; handler: (...args: unknown[]) => void }[] = [];

    function subscribe(eventName: string, handler: (...args: unknown[]) => void): void {
      on(eventName, handler);
      subscriptions.push({ eventName, handler });
    }

    for (const entry of registry) {
      // An entry that declares no lifecycle events is metadata-only (cancelKind + recovery); its
      // card is created by client code, so there is nothing here to subscribe.
      if (!entry.events) continue;

      if (entry.events.started && entry.started)
        subscribe(entry.events.started, buildStartedHandler(entry.started, dispatchDetail));
      if (entry.events.progress && entry.progress)
        subscribe(
          entry.events.progress,
          buildProgressHandler(entry, entry.progress, dispatchDetail)
        );
      if (!entry.complete) continue;
      subscribe(
        entry.events.complete,
        entry.events.started || entry.events.progress
          ? buildCompleteHandler(entry, entry.complete, dispatchDetail)
          : buildAnnouncementHandler(entry, entry.complete, showAnnouncement)
      );
    }

    subscribe('OperationUpdated', handleRun);

    return () => {
      for (const { eventName, handler } of subscriptions) off(eventName, handler);
    };
  }, [registry, on, off, dispatchDetail, showAnnouncement, handleRun]);
}
