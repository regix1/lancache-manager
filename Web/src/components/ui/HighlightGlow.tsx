import React, { useEffect, useRef } from 'react';
import { flashHighlight, type HighlightGlowVariant } from '@utils/highlightGlow';

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

// Longest reasonable time to wait for a smooth scrollIntoView to actually bring the
// target into view before flashing anyway - covers cases where IntersectionObserver
// never reports a change (e.g. threshold never quite crossed).
const SCROLL_SETTLE_TIMEOUT_MS = 800;

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

    target.scrollIntoView({ behavior: 'smooth', block: 'center' });

    let started = false;
    const start = (): void => {
      if (started) return;
      started = true;
      flashHighlight(target, variant);
    };

    // scrollIntoView only kicks off an async smooth scroll - flashing immediately
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
    };
  }, [enabled, variant, scrollIntoView]);

  return (
    <div ref={hostRef} className="highlight-glow-wrapper">
      {children}
    </div>
  );
};

export default HighlightGlow;
