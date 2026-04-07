import { useState, useEffect } from 'react';

export function useCountdownTimer(nextRunUtc: string | null, isProcessing: boolean): number {
  const [secondsRemaining, setSecondsRemaining] = useState(0);

  useEffect(() => {
    if (!nextRunUtc || isProcessing) {
      setSecondsRemaining(0);
      return;
    }

    const calculateRemaining = () => {
      const next = new Date(nextRunUtc).getTime();
      const now = Date.now();
      return Math.max(0, Math.floor((next - now) / 1000));
    };

    setSecondsRemaining(calculateRemaining());

    const interval = setInterval(() => {
      setSecondsRemaining((prev) => {
        if (prev <= 1) return 0;
        return prev - 1;
      });
    }, 1000);

    return () => clearInterval(interval);
  }, [nextRunUtc, isProcessing]);

  return secondsRemaining;
}
