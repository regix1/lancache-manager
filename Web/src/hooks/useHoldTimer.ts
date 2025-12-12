import { useRef, useCallback, useEffect } from 'react';

interface UseHoldTimerOptions {
  /** Delay before repeating starts (ms) */
  initialDelay?: number;
  /** Interval between repeats (ms) */
  repeatInterval?: number;
}

interface UseHoldTimerReturn {
  startHoldTimer: (callback: () => void) => void;
  stopHoldTimer: () => void;
}

/**
 * Hook for handling hold-to-repeat button interactions.
 * After an initial delay, repeatedly calls the callback at a set interval
 * until stopHoldTimer is called.
 */
export function useHoldTimer(options: UseHoldTimerOptions = {}): UseHoldTimerReturn {
  const { initialDelay = 400, repeatInterval = 150 } = options;

  const holdTimerRef = useRef<NodeJS.Timeout | null>(null);
  const holdTimeoutRef = useRef<NodeJS.Timeout | null>(null);

  const stopHoldTimer = useCallback(() => {
    if (holdTimeoutRef.current) {
      clearTimeout(holdTimeoutRef.current);
      holdTimeoutRef.current = null;
    }
    if (holdTimerRef.current) {
      clearInterval(holdTimerRef.current);
      holdTimerRef.current = null;
    }
  }, []);

  const startHoldTimer = useCallback(
    (callback: () => void) => {
      stopHoldTimer();
      holdTimeoutRef.current = setTimeout(() => {
        holdTimerRef.current = setInterval(callback, repeatInterval);
      }, initialDelay);
    },
    [stopHoldTimer, initialDelay, repeatInterval]
  );

  // Cleanup on unmount
  useEffect(() => () => stopHoldTimer(), [stopHoldTimer]);

  return { startHoldTimer, stopHoldTimer };
}
