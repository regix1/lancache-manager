import { useEffect, useRef, type RefObject } from 'react';

/**
 * Viewport geometry of an anchor (trigger) element. Collision decisions (which
 * side to open on, clamping to the screen edge) are made in viewport space, so
 * this is what the follow handler receives. A plain object rather than the live
 * DOMRect so the previous frame's values survive the next layout.
 */
export interface AnchorRect {
  top: number;
  left: number;
  bottom: number;
  right: number;
  width: number;
  height: number;
}

/** Called when the anchor's position in the *document* changes, with its fresh viewport rect. */
export type AnchorMoveHandler = (rect: AnchorRect) => void;

/** Called once the anchor leaves the viewport or the document. */
type AnchorLostHandler = () => void;

interface AnchorFollowOptions {
  /** Whether the overlay is currently mounted / on screen. */
  enabled: boolean;
  /** The trigger element the overlay is positioned from. */
  anchorRef: RefObject<HTMLElement | null>;
  /** Repositions the overlay. Runs inside the frame, so keep it cheap. */
  onAnchorMove: AnchorMoveHandler;
  /**
   * Dismisses the overlay when its anchor is gone. Without this, an overlay whose
   * trigger has been scrolled off screen keeps floating over unrelated content.
   */
  onAnchorLost?: AnchorLostHandler;
}

/**
 * Sub-pixel threshold below which an anchor is considered stationary. Guards
 * against re-render churn from rounding noise (fractional device pixels,
 * in-flight CSS transforms on ancestors).
 */
const ANCHOR_EPSILON_PX = 0.5;

/** Where the anchor sits on the page, independent of how far the page is scrolled. */
interface DocumentPoint {
  top: number;
  left: number;
  width: number;
  height: number;
}

/** Snapshots an element's viewport box in the same shape the follow loop compares. */
export function readAnchorRect(element: HTMLElement): AnchorRect {
  const rect = element.getBoundingClientRect();
  return {
    top: rect.top,
    left: rect.left,
    bottom: rect.bottom,
    right: rect.right,
    width: rect.width,
    height: rect.height
  };
}

function toDocumentPoint(rect: AnchorRect): DocumentPoint {
  return {
    top: rect.top + window.scrollY,
    left: rect.left + window.scrollX,
    width: rect.width,
    height: rect.height
  };
}

function hasMovedOnPage(previous: DocumentPoint | null, next: DocumentPoint): boolean {
  if (previous === null) return true;
  return (
    Math.abs(previous.top - next.top) > ANCHOR_EPSILON_PX ||
    Math.abs(previous.left - next.left) > ANCHOR_EPSILON_PX ||
    Math.abs(previous.width - next.width) > ANCHOR_EPSILON_PX ||
    Math.abs(previous.height - next.height) > ANCHOR_EPSILON_PX
  );
}

/** True once the anchor has scrolled completely outside the viewport. */
function isAnchorOffscreen(rect: AnchorRect): boolean {
  return (
    rect.bottom <= 0 ||
    rect.right <= 0 ||
    rect.top >= window.innerHeight ||
    rect.left >= window.innerWidth
  );
}

/**
 * Keeps a portalled overlay glued to its trigger.
 *
 * Overlays that snapshot `getBoundingClientRect()` at open time detach from their
 * trigger whenever the page reflows underneath them - the trigger moves, the
 * portalled overlay does not. The common case here is UniversalNotificationBar: it
 * is an in-flow sticky bar, so every notification that appears or finishes shifts
 * the whole page (and every trigger on it) vertically.
 *
 * A reflow fires no scroll and no resize event, and it does not necessarily change
 * the size of any element an observer could watch, so there is nothing to
 * subscribe to: the only universal signal is the anchor's own rect. This polls it
 * once per animation frame while `enabled` (the strategy floating-ui calls
 * `autoUpdate({ animationFrame: true })`).
 *
 * What it compares is the anchor's position *on the page*, not in the viewport.
 * Consumers position their overlay in document coordinates (`position: absolute`
 * offset by `window.scrollX/scrollY`), which means an ordinary scroll moves the
 * overlay and its trigger together, natively, on the compositor - so scrolling
 * reports no movement, does no work, and cannot make the overlay lag its trigger.
 * Anchoring an overlay in viewport coordinates instead (`position: fixed`) forces
 * JavaScript to re-place it every scroll frame, and the main thread cannot keep up
 * with a fast fling: the overlay visibly wobbles against its trigger. Only a real
 * reflow moves the anchor on the page, and those are one-off jumps where a frame
 * of catch-up is invisible.
 *
 * Since the overlay tracks its trigger, callers need not close on scroll to avoid a
 * stale position. What they do need is `onAnchorLost`: once the trigger is scrolled
 * off screen there is nothing left to anchor to, so the overlay should dismiss
 * rather than float over unrelated content.
 *
 * The loop runs only while the overlay is on screen, so keep `enabled` tied to
 * mounted-ness (including any exit animation) rather than to the open flag.
 *
 * Known limit: visibility is judged against the viewport, so an anchor clipped by an
 * inner `overflow` container while still inside the viewport does not count as lost.
 */
export function useAnchorFollow(options: AnchorFollowOptions): void {
  const { enabled, anchorRef, onAnchorMove, onAnchorLost } = options;

  // Held in refs so a new callback identity each render never restarts the loop.
  const moveRef = useRef<AnchorMoveHandler>(onAnchorMove);
  const lostRef = useRef<AnchorLostHandler | undefined>(onAnchorLost);

  useEffect(() => {
    moveRef.current = onAnchorMove;
    lostRef.current = onAnchorLost;
  }, [onAnchorMove, onAnchorLost]);

  useEffect(() => {
    if (!enabled) return;

    let frameId = 0;
    let lastPoint: DocumentPoint | null = null;
    let lost = false;

    const reposition = (): void => {
      const anchor = anchorRef.current;
      if (anchor === null || lost) return;

      if (!anchor.isConnected) {
        lost = true;
        lostRef.current?.();
        return;
      }

      const rect = readAnchorRect(anchor);
      if (isAnchorOffscreen(rect)) {
        lost = true;
        lostRef.current?.();
        return;
      }

      const point = toDocumentPoint(rect);
      if (hasMovedOnPage(lastPoint, point)) {
        lastPoint = point;
        moveRef.current(rect);
      }
    };

    const tick = (): void => {
      reposition();
      frameId = requestAnimationFrame(tick);
    };

    // A resize changes the viewport the overlay is clamped and flipped against
    // without necessarily moving the anchor on the page, so force a recompute.
    const handleResize = (): void => {
      lastPoint = null;
      reposition();
    };

    frameId = requestAnimationFrame(tick);
    window.addEventListener('resize', handleResize);
    return () => {
      cancelAnimationFrame(frameId);
      window.removeEventListener('resize', handleResize);
    };
  }, [enabled, anchorRef]);
}
