import { useCallback, useState } from 'react';
import { useTimeoutCallback } from './useTimeoutCallback';

/**
 * Tracks a "just copied" value that clears itself back to `clearedValue` after `delayMs`.
 * Built on {@link useTimeoutCallback}, so a second call inside the window cancels and
 * reschedules the clear instead of letting the first timer fire and hide a still-fresh
 * confirmation. `T` is `boolean` for a single copy button, or a string identity (e.g. the
 * copied item's key) for a list where more than one item can show its own confirmation.
 */
export function useCopyFeedback<T>(clearedValue: T, delayMs = 2000): [T, (value: T) => void] {
  const [copiedValue, setCopiedValue] = useState<T>(clearedValue);
  const schedule = useTimeoutCallback(delayMs);

  const markCopied = useCallback(
    (value: T): void => {
      setCopiedValue(value);
      schedule(() => setCopiedValue(clearedValue));
    },
    [schedule, clearedValue]
  );

  return [copiedValue, markCopied];
}
