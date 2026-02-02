import { useEffect, useRef } from 'react';
import type { SignalRContextType } from '@contexts/SignalRContext/types';

/**
 * Options for configuring the debounced SignalR refresh hook
 */
export interface DebouncedSignalRRefreshOptions {
  /**
   * Function to call when a refresh is triggered
   */
  onRefresh: () => void;

  /**
   * Function that returns the current refresh interval in milliseconds.
   * A value of 0 indicates "LIVE" mode with a minimum 500ms interval.
   */
  getRefreshInterval: () => number;

  /**
   * SignalR context instance for event subscriptions
   */
  signalR: SignalRContextType;

  /**
   * Array of SignalR event names to subscribe to
   */
  events: string[];

  /**
   * Whether the hook is enabled. When false, no event subscriptions are created.
   * Default: true
   */
  enabled?: boolean;
}

/**
 * Hook that subscribes to SignalR events and triggers a debounced refresh callback.
 * This hook implements a two-stage throttling system:
 * 1. 100ms debounce to batch rapid events
 * 2. User-configurable refresh rate throttle to prevent UI thrashing
 *
 * The hook respects the user's refresh rate preference:
 * - LIVE mode (0): minimum 500ms between refreshes
 * - Other modes: user-specified interval
 *
 * @param options - Configuration options for the hook
 *
 * @example
 * ```tsx
 * useDebouncedSignalRRefresh({
 *   onRefresh: () => fetchStats(),
 *   getRefreshInterval,
 *   signalR,
 *   events: SIGNALR_REFRESH_EVENTS,
 *   enabled: !mockMode
 * });
 * ```
 */
export function useDebouncedSignalRRefresh(
  options: DebouncedSignalRRefreshOptions
): void {
  const {
    onRefresh,
    getRefreshInterval,
    signalR,
    events,
    enabled = true
  } = options;

  // Refs to track state without causing re-renders
  const lastSignalRRefresh = useRef<number>(0);
  const pendingRefreshRef = useRef<NodeJS.Timeout | null>(null);

  // Keep function references in refs to avoid stale closures
  const onRefreshRef = useRef(onRefresh);
  const getRefreshIntervalRef = useRef(getRefreshInterval);

  // Update refs on every render
  onRefreshRef.current = onRefresh;
  getRefreshIntervalRef.current = getRefreshInterval;

  useEffect(() => {
    if (!enabled) return;

    /**
     * Debounced handler that respects user's refresh rate setting.
     * This replaces polling - SignalR events are the only source of updates.
     */
    const handleRefreshEvent = (): void => {
      // Clear any pending refresh to debounce rapid events
      if (pendingRefreshRef.current) {
        clearTimeout(pendingRefreshRef.current);
      }

      // Debounce: wait 100ms for more events before processing
      pendingRefreshRef.current = setTimeout(() => {
        const maxRefreshRate = getRefreshIntervalRef.current();
        const now = Date.now();
        const timeSinceLastRefresh = now - lastSignalRRefresh.current;

        // User's setting controls max refresh rate
        // LIVE mode (0) = minimum 500ms to prevent UI thrashing
        const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

        if (timeSinceLastRefresh >= minInterval) {
          lastSignalRRefresh.current = now;
          onRefreshRef.current();
        }
        pendingRefreshRef.current = null;
      }, 100);
    };

    // Subscribe to all specified events
    events.forEach((event) => signalR.on(event, handleRefreshEvent));

    return () => {
      // Unsubscribe from all events
      events.forEach((event) => signalR.off(event, handleRefreshEvent));

      // Clear any pending refresh timeout
      if (pendingRefreshRef.current) {
        clearTimeout(pendingRefreshRef.current);
        pendingRefreshRef.current = null;
      }
    };
  }, [signalR, events, enabled]);
}
