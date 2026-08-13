import { useState, useLayoutEffect, useCallback } from 'react';

export function useCountdownTimer(nextRunUtc: string | null, isProcessing: boolean): number {
  const calculateRemaining = useCallback((): number => {
    if (!nextRunUtc || isProcessing) {
      return 0;
    }
    const next = new Date(nextRunUtc).getTime();
    const now = Date.now();
    // Rounded up, not down. A ten-minute window read a millisecond after it opens has 599.999
    // seconds left, and flooring that painted "9m 59s" on a wait the person was just told was ten
    // minutes. Rounding up also means the count only reaches zero once the deadline has genuinely
    // passed, so it never claims the attempt is over while it is still running.
    return Math.max(0, Math.ceil((next - now) / 1000));
  }, [nextRunUtc, isProcessing]);

  // Seeded lazily rather than from 0, because zero is the reading that is supposed to mean the
  // attempt is genuinely over, and a deadline minutes away must never paint it.
  const [secondsRemaining, setSecondsRemaining] = useState<number>(calculateRemaining);

  // Before the browser paints, not after. The lazy seed above only covers the first mount; when a
  // deadline arrives later - which is every login, since the clock starts on the submit and the
  // component is already on screen - the seed comes from here instead. A plain effect runs after
  // the paint, so the frame in between showed a fresh ten-minute wait as already expired.
  useLayoutEffect(() => {
    if (!nextRunUtc || isProcessing) {
      setSecondsRemaining(0);
      return;
    }

    setSecondsRemaining(calculateRemaining());

    // Every tick re-reads the clock instead of subtracting one from the last value. A throttled
    // background tab, a sleeping laptop or a slow frame all skip or delay ticks, and a decrementing
    // counter would drift behind real time and keep showing time left after the deadline had
    // already passed.
    const interval = setInterval(() => {
      setSecondsRemaining(calculateRemaining());
    }, 1000);

    return () => clearInterval(interval);
  }, [calculateRemaining, nextRunUtc, isProcessing]);

  return secondsRemaining;
}
