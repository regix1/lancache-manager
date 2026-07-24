/**
 * App-wide, always-on tracker of genuine user interaction (mouse/keyboard/touch/scroll/wheel),
 * independent of any single component's mount/unmount lifecycle. A browser tab can sit open and
 * VISIBLE on an unattended screen while its own background SignalR-triggered refetches keep firing -
 * Page Visibility alone can't tell "visible" apart from "visible but nobody's there". This module is
 * the thing that can: only a real interaction event moves the needle, so an untouched-but-visible tab
 * correctly ages out even though it's never hidden.
 *
 * Deliberately module-level (not a React hook) so a plain static method like
 * ApiService.getFetchOptions can read it synchronously without needing to be inside a component.
 */

const INTERACTION_EVENTS = [
  'mousedown',
  'mousemove',
  'keydown',
  'touchstart',
  'scroll',
  'wheel'
] as const;

// Coalesce high-frequency events (mousemove/scroll) into at most one timestamp write per second -
// the exact recency doesn't matter, only "was there activity roughly now".
const WRITE_THROTTLE_MS = 1000;

let lastInteractionAtMs: number | null = null;
let lastWriteAtMs = 0;
let listenersAttached = false;

/**
 * Records a genuine interaction right now. Exported so a component that already knows an
 * interaction just happened (e.g. useActivityTracker's own DOM listener firing on the same event)
 * can mark it immediately, rather than relying on this module's own listener - registered
 * independently, on the same event - having already run first. Listener execution order for two
 * separate addEventListener calls on the same event is registration order, which depends on
 * component mount order and isn't something callers should have to reason about.
 */
export function recordInteraction(): void {
  const now = Date.now();
  if (now - lastWriteAtMs < WRITE_THROTTLE_MS) {
    return;
  }
  lastWriteAtMs = now;
  lastInteractionAtMs = now;
}

function ensureListenersAttached(): void {
  if (listenersAttached || typeof window === 'undefined') {
    return;
  }
  listenersAttached = true;

  INTERACTION_EVENTS.forEach((eventName) => {
    window.addEventListener(eventName, recordInteraction, { passive: true });
  });

  // Loading/navigating to the page at all is itself real evidence a human is here right now -
  // without this, a freshly-opened tab would report "no interaction yet" for the first several
  // seconds before any mouse/key event fires.
  recordInteraction();
}

/**
 * True if a genuine interaction event has been observed within the last `thresholdMs`. False for a
 * tab that's open and visible but hasn't actually been touched in that window, regardless of Page
 * Visibility state.
 */
export function hasRecentUserInteraction(thresholdMs: number): boolean {
  ensureListenersAttached();
  if (lastInteractionAtMs === null) {
    return false;
  }
  return Date.now() - lastInteractionAtMs < thresholdMs;
}
