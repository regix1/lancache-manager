import React, { useEffect, useRef } from 'react';
import { flashHighlight, type HighlightGlowVariant } from '@utils/highlightGlow';
import { glideScroll } from '@utils/scrollGlide';

interface HighlightGlowProps {
  children: React.ReactNode;
  enabled?: boolean;
  variant?: HighlightGlowVariant;
  /** Scroll the target into view before glowing (navigate-to-section flows). */
  scrollIntoView?: boolean;
}

// Falls back to the wrapper's first DOM child when nothing opts in explicitly -
// correct for every current single-root wrapped component. A component that
// returns a Fragment (multiple DOM roots, e.g. EpicDaemonStatus/XboxDaemonStatus
// pairing a Card with a modal) must mark its real target with the
// "highlight-glow-target" class (via its own className prop) so the glow can't
// silently drift onto the wrong node if render order ever changes.
const resolveTarget = (host: HTMLElement): HTMLElement | null => {
  const marked = host.querySelector<HTMLElement>('.highlight-glow-target');
  if (marked) return marked;
  return host.firstElementChild instanceof HTMLElement ? host.firstElementChild : null;
};

// Longest reasonable time to wait for the glide to actually bring the
// target into view before flashing anyway - covers cases where IntersectionObserver
// never reports a change (e.g. threshold never quite crossed).
const SCROLL_SETTLE_TIMEOUT_MS = 800;

// Input that means the user has taken over the scroll. pointerdown also covers touch.
const USER_SCROLL_EVENTS = ['wheel', 'pointerdown', 'keydown'] as const;

// Wrapper form of flashHighlight for children that don't accept a className. The
// glow lands directly on the target element, so the shadow follows its own
// border-radius (works whether the target is a card, a button, or an accordion).
const HighlightGlow: React.FC<HighlightGlowProps> = ({
  children,
  enabled = false,
  variant = 'navigate',
  scrollIntoView = false
}) => {
  const hostRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!enabled) return;
    const target = hostRef.current && resolveTarget(hostRef.current);
    if (!target) return;

    if (!scrollIntoView) {
      flashHighlight(target, variant);
      return;
    }

    const page = document.scrollingElement as HTMLElement;
    // The page position that centers the target. Near the page end the page stops at the bottom, so
    // a target less than half a viewport from the end rests below center by that shortfall.
    const centerTop = (): number => {
      const rect = target.getBoundingClientRect();
      return Math.min(
        Math.max(0, page.scrollHeight - window.innerHeight),
        Math.max(0, page.scrollTop + rect.top + rect.height / 2 - window.innerHeight / 2)
      );
    };
    let glideTarget = centerTop();
    let stopGlide = glideScroll(page, glideTarget);

    // Cards above the target can still be loading when the glide starts, and their growth pushes
    // the target off center. Re-center whenever that moves the center, until the user scrolls,
    // clicks or presses a key, or the page stays the same size for SCROLL_SETTLE_TIMEOUT_MS.
    const stopSettling = (): void => {
      // A wheel, a pointer press or any key means the user took over. The glide only skips its next
      // frame, so the browser applies their input to the page exactly as it would to a still page.
      stopGlide();
      resizeObserver.disconnect();
      window.clearTimeout(settleTimer);
      for (const type of USER_SCROLL_EVENTS) window.removeEventListener(type, stopSettling, true);
    };
    let settleTimer = window.setTimeout(stopSettling, SCROLL_SETTLE_TIMEOUT_MS);
    // The observer fires on observe() and for every card that loads anywhere on the page; a fresh
    // glide on each would restart the travel even when the center did not move.
    const resizeObserver = new ResizeObserver(() => {
      const center = centerTop();
      if (Math.abs(center - glideTarget) > 0.5) {
        stopGlide();
        glideTarget = center;
        stopGlide = glideScroll(page, glideTarget);
      }
      window.clearTimeout(settleTimer);
      settleTimer = window.setTimeout(stopSettling, SCROLL_SETTLE_TIMEOUT_MS);
    });
    resizeObserver.observe(document.documentElement);
    // Capture phase, so input inside a control that stops the event still ends the jump.
    for (const type of USER_SCROLL_EVENTS) {
      window.addEventListener(type, stopSettling, { capture: true, passive: true });
    }

    let started = false;
    const start = (): void => {
      if (started) return;
      started = true;
      flashHighlight(target, variant);
    };

    // The glide takes several frames to arrive - flashing immediately
    // would burn most of the pulse while the target is still off-screen. Wait for
    // it to actually enter the viewport; IntersectionObserver reports the current
    // state immediately on observe(), so this also covers the already-visible
    // case without a separate branch.
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting) {
          observer.disconnect();
          start();
        }
      },
      { threshold: 0.6 }
    );
    observer.observe(target);
    const fallback = window.setTimeout(start, SCROLL_SETTLE_TIMEOUT_MS);

    return () => {
      observer.disconnect();
      window.clearTimeout(fallback);
      stopSettling();
    };
  }, [enabled, variant, scrollIntoView]);

  return (
    <div ref={hostRef} className="highlight-glow-wrapper">
      {children}
    </div>
  );
};

export default HighlightGlow;
