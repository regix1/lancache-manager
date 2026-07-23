import { useEffect, useMemo, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useRefreshRate } from '@contexts/useRefreshRate';
import type { EventHandler } from '@contexts/SignalRContext/types';
import { PERSISTENT_PREFILL_CONTAINER_SIGNALR_EVENTS } from './persistentPrefillSignalREvents';

/** Minimal on/off subscription surface for the shared SignalR facade this hook returns. */
interface PersistentPrefillSignalRFacade {
  on: (eventName: string, handler: EventHandler) => void;
  off: (eventName: string, handler: EventHandler) => void;
}

interface UsePersistentPrefillContainerSignalROptions {
  /** When false, listeners are not registered. */
  enabled: boolean;
  /** Called (throttled by the user's refresh rate) after a relevant SignalR event. */
  onRefresh: () => void;
}

interface UsePersistentPrefillContainerSignalRResult {
  /** Shared SignalR facade for wait helpers and other one-shot listeners. */
  signalR: PersistentPrefillSignalRFacade;
}

/**
 * Single SignalR entry point for persistent-container UI: returns a shared facade and,
 * when enabled, subscribes to daemon session/auth events with refresh-rate throttling.
 */
export function usePersistentPrefillContainerSignalR({
  enabled,
  onRefresh
}: UsePersistentPrefillContainerSignalROptions): UsePersistentPrefillContainerSignalRResult {
  const { on, off, isConnected } = useSignalR();
  const signalR = useMemo(() => ({ on, off }), [on, off]);
  const { getRefreshInterval } = useRefreshRate();
  const lastRefreshRef = useRef(0);
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const onRefreshRef = useRef(onRefresh);
  const getRefreshIntervalRef = useRef(getRefreshInterval);
  const wasDisconnectedRef = useRef(false);

  onRefreshRef.current = onRefresh;
  getRefreshIntervalRef.current = getRefreshInterval;

  // A reconnect can swallow the session/auth events for a container that was created, started, or
  // finished authenticating while the socket was down - reconcile from the server whenever the
  // connection returns so the card cannot stay frozen on a pre-drop snapshot.
  useEffect(() => {
    if (!enabled) {
      return;
    }
    if (!isConnected) {
      wasDisconnectedRef.current = true;
      return;
    }
    if (wasDisconnectedRef.current) {
      wasDisconnectedRef.current = false;
      onRefreshRef.current();
    }
  }, [enabled, isConnected]);

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const scheduleRefresh = () => {
      const interval = getRefreshIntervalRef.current() || 500;
      const elapsed = Date.now() - lastRefreshRef.current;

      if (debounceTimerRef.current) {
        clearTimeout(debounceTimerRef.current);
        debounceTimerRef.current = null;
      }

      const runRefresh = () => {
        lastRefreshRef.current = Date.now();
        onRefreshRef.current();
      };

      if (elapsed >= interval) {
        runRefresh();
      } else {
        debounceTimerRef.current = setTimeout(runRefresh, interval - elapsed);
      }
    };

    const handlers = new Map<string, EventHandler>();
    for (const eventName of PERSISTENT_PREFILL_CONTAINER_SIGNALR_EVENTS) {
      const handler: EventHandler = (data?: unknown) => {
        // Guest-prefill progress broadcasts DaemonSessionUpdated on every tick (~every refresh
        // interval) with isPersistent === false. Those must not drive the persistent-container
        // refresh - skip them. Persistent-container events (isPersistent === true) and events
        // that carry no flag at all (auth / terminate keyed only by sessionId) still refresh.
        if (
          data &&
          typeof data === 'object' &&
          'isPersistent' in data &&
          (data as { isPersistent?: boolean }).isPersistent === false
        ) {
          return;
        }
        scheduleRefresh();
      };
      handlers.set(eventName, handler);
      on(eventName, handler);
    }

    return () => {
      if (debounceTimerRef.current) {
        clearTimeout(debounceTimerRef.current);
      }
      handlers.forEach((handler, eventName) => off(eventName, handler));
    };
  }, [enabled, on, off]);

  return { signalR };
}
