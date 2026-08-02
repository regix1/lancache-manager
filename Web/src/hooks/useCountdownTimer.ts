import { useState, useEffect, useCallback } from 'react';

export function useCountdownTimer(nextRunUtc: string | null, isProcessing: boolean): number {
  const calculateRemaining = useCallback((): number => {
    if (!nextRunUtc || isProcessing) {
      return 0;
    }
    const next = new Date(nextRunUtc).getTime();
    const now = Date.now();
    return Math.max(0, Math.floor((next - now) / 1000));
  }, [nextRunUtc, isProcessing]);

  // Seeded lazily rather than from 0. The effect below only runs after the first commit has
  // painted, so starting at 0 would paint one frame of "no time left" on a deadline that is
  // minutes away - and zero is the reading that is supposed to mean the attempt is genuinely
  // over. [23]
  const [secondsRemaining, setSecondsRemaining] = useState<number>(calculateRemaining);

  useEffect(() => {
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
