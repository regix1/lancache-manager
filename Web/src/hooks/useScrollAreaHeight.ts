import { useEffect, useState } from 'react';

/**
 * Measures a flex-grown scroll area and reports its height in pixels, for handing to
 * `CustomScrollbar`'s `maxHeight`.
 *
 * A percentage max-height does not resolve inside a modal body whose height comes entirely from a
 * flex-grow chain with no explicit CSS height anywhere in it: a plain block descendant needs a
 * definite parent height, so the percentage falls back to unconstrained content height instead of
 * clamping. Measuring the wrapper and passing a concrete pixel value sidesteps that.
 *
 * The element arrives through a callback ref rather than a `useRef`, because `Modal` renders its
 * children one render after `opened` flips true, so an effect keyed on `opened` would find the ref
 * still null and never get a second chance.
 */
export function useScrollAreaHeight(): [(element: HTMLDivElement | null) => void, number | null] {
  const [scrollArea, setScrollArea] = useState<HTMLDivElement | null>(null);
  const [height, setHeight] = useState<number | null>(null);

  useEffect(() => {
    if (!scrollArea) {
      return;
    }

    const updateHeight = () => setHeight(scrollArea.clientHeight);
    updateHeight();

    const resizeObserver = new ResizeObserver(updateHeight);
    resizeObserver.observe(scrollArea);
    return () => resizeObserver.disconnect();
  }, [scrollArea]);

  return [setScrollArea, height];
}
