import { useCallback, useEffect, useRef } from 'react';

type TimeoutId = ReturnType<typeof setTimeout>;

/**
 * Floor for the live cadence. The LIVE refresh rate is 0, which would otherwise refetch on every
 * event a busy download raises.
 */
const MIN_REFRESH_INTERVAL_MS = 500;

/**
 * Runs a refresh on the user's configured refresh rate: straight away when that long has already
 * passed since the last run, otherwise once the remainder elapses, so a burst collapses into one
 * run instead of one per event.
 *
 * Every live surface answers SignalR through this, so a finished download reaches the Retro list
 * and the card, normal and compact views at the same moment. A surface with its own timing drifts
 * away from the rest and looks slow next to them.
 *
 * @param getIntervalMs reads the current rate each time, so changing the setting takes effect
 *                      without remounting the caller.
 */
export function useRefreshThrottle(getIntervalMs: () => number): (run: () => void) => void {
  const timerRef = useRef<TimeoutId | null>(null);
  const lastRunAtRef = useRef<number>(0);
  const getIntervalRef = useRef(getIntervalMs);
  getIntervalRef.current = getIntervalMs;

  useEffect(
    () => () => {
      if (timerRef.current !== null) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
    },
    []
  );

  return useCallback((run: () => void): void => {
    const interval = getIntervalRef.current() || MIN_REFRESH_INTERVAL_MS;

    const fire = (): void => {
      timerRef.current = null;
      lastRunAtRef.current = Date.now();
      run();
    };

    // A newer request replaces a pending one: the later call carries the fresher intent and the
    // fetch reads current state anyway.
    if (timerRef.current !== null) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }

    const elapsed = Date.now() - lastRunAtRef.current;
    if (elapsed >= interval) {
      fire();
      return;
    }

    timerRef.current = setTimeout(fire, interval - elapsed);
  }, []);
}
