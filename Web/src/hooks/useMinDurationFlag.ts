import { useEffect, useRef, useState } from 'react';

/**
 * Holds `true` for at least minDurationMs after value flips true, even if value flips back to false
 * sooner - so a fast state transition (e.g. a schedule run completing in milliseconds) still renders
 * long enough to be seen, without holding a genuinely longer-running state any longer than it lasts.
 */
export function useMinDurationFlag(value: boolean, minDurationMs: number): boolean {
  const [visible, setVisible] = useState(value);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const becameTrueAtRef = useRef<number | null>(null);

  useEffect(() => {
    if (value) {
      becameTrueAtRef.current = Date.now();
      setVisible(true);
      if (timerRef.current) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
      return;
    }

    if (becameTrueAtRef.current === null) {
      setVisible(false);
      return;
    }

    const elapsed = Date.now() - becameTrueAtRef.current;
    const remaining = minDurationMs - elapsed;
    if (remaining <= 0) {
      setVisible(false);
      becameTrueAtRef.current = null;
      return;
    }

    timerRef.current = setTimeout(() => {
      setVisible(false);
      becameTrueAtRef.current = null;
    }, remaining);

    return () => {
      if (timerRef.current) {
        clearTimeout(timerRef.current);
      }
    };
  }, [value, minDurationMs]);

  return visible;
}
