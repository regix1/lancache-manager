import { useCallback, useEffect, useRef } from 'react';

type TimeoutId = ReturnType<typeof setTimeout>;

/**
 * Returns a stable `schedule` function that wraps `setTimeout` with automatic
 * cleanup. Each new schedule cancels any pending timer so only one callback
 * is pending per component instance at a time. The pending timer is also
 * cleared when the component unmounts.
 */
export function useTimeoutCallback(delayMs: number): (fn: () => void) => void {
  const timerRef = useRef<TimeoutId | null>(null);

  const schedule = useCallback(
    (fn: () => void): void => {
      if (timerRef.current !== null) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
      timerRef.current = setTimeout(() => {
        timerRef.current = null;
        fn();
      }, delayMs);
    },
    [delayMs]
  );

  useEffect(
    () => () => {
      if (timerRef.current !== null) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
    },
    []
  );

  return schedule;
}
