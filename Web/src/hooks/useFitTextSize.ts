import { useEffect, useState } from 'react';
import { measureTextWidth } from '@utils/textMeasurement';

/** Font sizes a fitted label may step down through, largest first. */
const FIT_STEPS_PX = [14, 12, 11] as const;

interface FitTextSize {
  /** Attach to the single-line element that carries the text. */
  ref: (element: HTMLElement | null) => void;
  /** Index into the size steps: 0 is the full size, higher is smaller. */
  step: number;
}

/**
 * The largest of a few font sizes at which `text` still fits its element on one line.
 *
 * Measures the text once at the full size with the element's own font and scales that
 * width per step, since a label's width is linear in its font size. Re-measured whenever
 * the text or the element's width changes, so a title that fits in landscape is re-fitted
 * in portrait. When even the smallest step is too narrow the last step is returned and the
 * element's own ellipsis takes over.
 */
export function useFitTextSize(text: string): FitTextSize {
  // Held in state rather than a ref so mounting or unmounting the element re-runs the
  // measurement, the same reason useTextTruncation does it.
  const [element, setElement] = useState<HTMLElement | null>(null);
  const [step, setStep] = useState(0);

  useEffect(() => {
    if (!element) {
      setStep(0);
      return;
    }

    const measure = (): void => {
      const { fontWeight, fontFamily } = getComputedStyle(element);
      const fullWidth = measureTextWidth(text, `${fontWeight} ${FIT_STEPS_PX[0]}px ${fontFamily}`);
      const available = element.clientWidth;
      const fits = FIT_STEPS_PX.findIndex((px) => (fullWidth * px) / FIT_STEPS_PX[0] <= available);
      setStep(fits === -1 ? FIT_STEPS_PX.length - 1 : fits);
    };

    measure();

    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [element, text]);

  return { ref: setElement, step };
}
