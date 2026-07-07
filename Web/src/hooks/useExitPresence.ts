import { useEffect, useRef, useState } from 'react';

/**
 * Duration (ms) an exit animation runs before the element unmounts. Must be
 * >= the CSS exit-keyframe duration in dropdowns.css (dropdownSlideOut*,
 * currently 0.14s). The exit keyframes use `forwards` fill, so a slightly
 * longer timer never flashes the resting frame back in before unmount.
 */
export const DROPDOWN_EXIT_MS = 150;

interface ExitPresence {
  /** True while the element should stay mounted (including its exit animation). */
  present: boolean;
  /** True during the exit window, so the caller can swap in the exit animation. */
  closing: boolean;
}

/**
 * Keeps an element mounted through a closing animation.
 *
 * `open` true  -> `present` immediately, `closing` false (play the entrance).
 * `open` false -> `closing` true for `exitMs`, then `present` false (unmount).
 *
 * Reopening mid-close cancels the pending unmount, so the element never
 * flickers out. Click-outside / Escape handlers only need to flip `open`;
 * this hook owns the delayed unmount.
 */
export function useExitPresence(open: boolean, exitMs: number): ExitPresence {
  const [present, setPresent] = useState<boolean>(open);
  const [closing, setClosing] = useState<boolean>(false);
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (open) {
      // (Re)opening: cancel any pending unmount and show immediately.
      if (timeoutRef.current !== null) {
        clearTimeout(timeoutRef.current);
        timeoutRef.current = null;
      }
      setPresent(true);
      setClosing(false);
      return;
    }

    // Closing: nothing to animate out if the element is already unmounted.
    if (!present) return;

    setClosing(true);
    timeoutRef.current = setTimeout(() => {
      timeoutRef.current = null;
      setPresent(false);
      setClosing(false);
    }, exitMs);

    return () => {
      if (timeoutRef.current !== null) {
        clearTimeout(timeoutRef.current);
        timeoutRef.current = null;
      }
    };
  }, [open, exitMs, present]);

  return { present, closing };
}
