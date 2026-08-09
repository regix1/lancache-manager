import { useEffect, useState } from 'react';

/** Sub-pixel layout can leave scrollWidth a hair above clientWidth with nothing actually clipped. */
const TRUNCATION_THRESHOLD_PX = 1;

interface TextTruncation {
  /** Attach to the element that carries the clip (the one with `truncate`). */
  ref: (element: HTMLElement | null) => void;
  /** True while that element is too narrow to show all of its text. */
  isTruncated: boolean;
}

/**
 * Whether a single-line element is cutting its own text off.
 *
 * `scrollWidth` is the width the text wants, `clientWidth` is the width it got, so the
 * difference is what the reader cannot see. Callers use it to offer the full text some
 * other way - a tooltip repeating a label that is fully readable is noise, but the same
 * tooltip on a clipped label is the only way to read it.
 *
 * The answer is re-measured whenever `text` changes and whenever the element's own width
 * changes, because a label that fits at one window size is clipped at the next. A stale
 * answer is worse than none: it either hides text with no way to reach it, or leaves a
 * hover box on a label that needs no explaining.
 */
export function useTextTruncation(text: string): TextTruncation {
  // The element is held in state rather than a ref so that mounting or unmounting it
  // re-runs the measurement; a ref object would leave the effect looking at the old node.
  const [element, setElement] = useState<HTMLElement | null>(null);
  const [isTruncated, setIsTruncated] = useState(false);

  useEffect(() => {
    if (!element) {
      setIsTruncated(false);
      return;
    }

    const measure = (): void => {
      setIsTruncated(element.scrollWidth - element.clientWidth >= TRUNCATION_THRESHOLD_PX);
    };

    measure();

    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [element, text]);

  return { ref: setElement, isTruncated };
}
