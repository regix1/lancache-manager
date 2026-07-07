import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';

interface CollapsibleRegionProps {
  open: boolean;
  className?: string;
  contentClassName?: string;
  children: React.ReactNode;
}

// Fallback unmount delay for environments where `transitionend` never fires
// (e.g. prefers-reduced-motion sets `transition: none`). Must cover the CSS
// grid-template-rows transition duration (0.25s) with headroom.
const UNMOUNT_FALLBACK_MS = 320;

/**
 * Animated expand/collapse wrapper using the house grid-template-rows 0fr->1fr
 * pattern. Children mount on open and unmount only after the close transition
 * completes, so collapsed content reaches true 0 height (safe for virtualized
 * row measurement) and leaves the tab order.
 */
export const CollapsibleRegion: React.FC<CollapsibleRegionProps> = ({
  open,
  className,
  contentClassName,
  children
}) => {
  const [present, setPresent] = useState<boolean>(open);
  const [isOpen, setIsOpen] = useState<boolean>(open);
  const presentRef = useRef<boolean>(open);
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const fallbackTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const setPresence = useCallback((next: boolean): void => {
    presentRef.current = next;
    setPresent(next);
  }, []);

  // On a fresh open, add is-open only after the collapsed (0fr) wrapper has
  // been committed and its layout flushed, so the 0fr->1fr grid transition
  // runs. A forced reflow replaces the usual double-requestAnimationFrame:
  // rAF never fires in occluded/background windows, which left the region
  // permanently collapsed there.
  useLayoutEffect(() => {
    if (open && present && !isOpen && wrapperRef.current) {
      void wrapperRef.current.offsetHeight;
      setIsOpen(true);
    }
  }, [open, present, isOpen]);

  useEffect(() => {
    const wrapper = wrapperRef.current;

    const clearFallback = (): void => {
      if (fallbackTimerRef.current !== null) {
        clearTimeout(fallbackTimerRef.current);
        fallbackTimerRef.current = null;
      }
    };

    if (open) {
      // Reopening cancels any pending close teardown; the layout effect
      // above flips is-open once the wrapper is mounted.
      clearFallback();
      setPresence(true);
      return;
    }

    // Closing.
    if (!presentRef.current) {
      return;
    }

    setIsOpen(false);

    function finishClose(): void {
      clearFallback();
      wrapper?.removeEventListener('transitionend', handleTransitionEnd);
      setPresence(false);
    }

    function handleTransitionEnd(event: TransitionEvent): void {
      if (event.target === wrapper && event.propertyName === 'grid-template-rows') {
        finishClose();
      }
    }

    wrapper?.addEventListener('transitionend', handleTransitionEnd);
    fallbackTimerRef.current = setTimeout(finishClose, UNMOUNT_FALLBACK_MS);

    return () => {
      wrapper?.removeEventListener('transitionend', handleTransitionEnd);
      clearFallback();
    };
  }, [open, setPresence]);

  if (!present) {
    return null;
  }

  return (
    <div
      ref={wrapperRef}
      className={`collapsible-region${isOpen ? ' is-open' : ''}${className ? ` ${className}` : ''}`}
    >
      <div className="collapsible-region-inner">
        <div className={contentClassName}>{children}</div>
      </div>
    </div>
  );
};
