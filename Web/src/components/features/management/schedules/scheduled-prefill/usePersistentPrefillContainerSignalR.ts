import { useEffect, useMemo, useRef } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useRefreshRate } from '@contexts/useRefreshRate';
import type { EventHandler } from '@contexts/SignalRContext/types';
import { PERSISTENT_PREFILL_CONTAINER_SIGNALR_EVENTS } from './persistentPrefillSignalREvents';
import type { PersistentPrefillSignalRFacade } from './waitForPersistentContainerAuth';

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
  const { on, off } = useSignalR();
  const signalR = useMemo(() => ({ on, off }), [on, off]);
  const { getRefreshInterval } = useRefreshRate();
  const lastRefreshRef = useRef(0);
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const onRefreshRef = useRef(onRefresh);
  const getRefreshIntervalRef = useRef(getRefreshInterval);

  onRefreshRef.current = onRefresh;
  getRefreshIntervalRef.current = getRefreshInterval;

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
      const handler: EventHandler = () => scheduleRefresh();
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
