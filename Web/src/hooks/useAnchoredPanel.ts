import { useCallback, useEffect, useLayoutEffect, useState, type RefObject } from 'react';
import { useAnchorFollow, readAnchorRect, type AnchorRect } from './useAnchorFollow';
import { useExitPresence, DROPDOWN_EXIT_MS } from './useExitPresence';
import { clampToViewport } from '@utils/viewportClamp';

/** Where the panel's near edges sit, plus which way it opened. */
export interface PanelPlacement {
  top: number;
  left: number;
  /** True when the panel opened above its anchor, so the caller can pick the matching keyframe. */
  openUpward: boolean;
  /**
   * Height the panel has room for on the side it opened. Only a placement that sizes the
   * panel into its room returns this; the hook hands it back with the coordinates, so the
   * decision stays a function of the space it was given rather than a write during layout.
   */
  availableHeight?: number;
}

/**
 * Everything a placement decision can read, all of it in viewport CSS pixels.
 * The panel's own size is 0 on the pass before it mounts; see `useAnchoredPanel`.
 */
export interface PanelSpace {
  anchor: AnchorRect;
  panelWidth: number;
  panelHeight: number;
  viewportWidth: number;
  viewportHeight: number;
  gutter: number;
}

/** Replaces the built-in placement; returns viewport coordinates, same as it received. */
type PanelPlaceHandler = (space: PanelSpace) => PanelPlacement;

interface AnchoredPanelOptions {
  open: boolean;
  /** The trigger the panel is positioned from and follows. */
  anchorRef: RefObject<HTMLElement | null>;
  /** The panel itself, measured to decide the flip and the clamp. */
  panelRef: RefObject<HTMLElement | null>;
  /** Called on Escape and once the anchor is gone. */
  onClose: () => void;
  /**
   * Called on Escape, immediately after `onClose`. Separate from it because the hook also
   * closes when the anchor is lost, and a panel that returns focus to its trigger must not
   * do that to a trigger the reader has just scrolled away from.
   */
  onEscape?: () => void;
  /** Smallest gap kept between the panel and the viewport edge: `MENU_GUTTER_PX` or `POPOVER_GUTTER_PX`. */
  gutter: number;
  /** Which of the panel's edges lines up with the anchor's. */
  align?: 'left' | 'right';
  /** Gap between the anchor and the panel. Popovers use 8. */
  gap?: number;
  /** For a panel whose position is not an aligned box under its trigger. */
  place?: PanelPlaceHandler;
}

interface AnchoredPanel {
  /** True while the panel should stay mounted, including its exit animation. */
  present: boolean;
  /** True during the exit window, so the caller can swap in the exit animation. */
  closing: boolean;
  /** Document coordinates, for an absolutely positioned panel in a body portal. */
  position: PanelPlacement;
  /** The anchor's measured width, for a panel that takes its trigger's width as a minimum. */
  anchorWidth: number;
}

const DEFAULT_ANCHOR_GAP_PX = 4;
/** Position deltas at or below this are rounding noise, not movement. */
const POSITION_EPSILON_PX = 0.5;

function isSamePlacement(a: PanelPlacement, b: PanelPlacement): boolean {
  return (
    a.openUpward === b.openUpward &&
    a.availableHeight === b.availableHeight &&
    Math.abs(a.top - b.top) <= POSITION_EPSILON_PX &&
    Math.abs(a.left - b.left) <= POSITION_EPSILON_PX
  );
}

function placeBelowAnchor(space: PanelSpace, align: 'left' | 'right', gap: number): PanelPlacement {
  const { anchor, panelWidth, panelHeight, viewportWidth, viewportHeight, gutter } = space;

  // Line the panel's matching edge up with the anchor's, then keep it on screen.
  const desiredLeft = align === 'right' ? anchor.right - panelWidth : anchor.left;
  const left = clampToViewport(desiredLeft, panelWidth, viewportWidth, gutter);

  // Open upward when the panel would not fit between the anchor and the viewport
  // bottom and there is more room above. Without this the absolutely positioned panel
  // overflows past the document's end, which grows the page's scroll height and reads
  // as the panel shoving the footer down instead of overlaying content. Height is 0
  // until the panel mounts, and a zero-height panel must not pick a side.
  const spaceBelow = viewportHeight - anchor.bottom;
  const fitsBelow = spaceBelow >= panelHeight + gap + gutter;
  const openUpward = panelHeight > 0 && !fitsBelow && anchor.top > spaceBelow;
  const desiredTop = openUpward ? anchor.top - panelHeight - gap : anchor.bottom + gap;
  // Flipping alone only buys the anchor's distance from the edge, so a panel taller
  // than the room on the side it picked still hangs off the viewport.
  const top = clampToViewport(desiredTop, panelHeight, viewportHeight, gutter);

  return { top, left, openUpward };
}

/**
 * Places a portalled panel against its trigger and keeps it there.
 *
 * This is the composition every floating panel here needs: clamp the panel inside the
 * viewport (`clampToViewport`), carry it with its trigger through a reflow
 * (`useAnchorFollow`), re-place it when its own height changes, hold it mounted through
 * its exit animation (`useExitPresence`), and close it on Escape. None of that is
 * re-implemented here.
 *
 * `position` comes back in DOCUMENT coordinates, so the panel is `position: absolute`
 * in a body portal. Anchoring it in viewport coordinates instead (`position: fixed`)
 * forces JavaScript to re-place it on every scroll frame and it visibly wobbles against
 * its trigger; absolute placement lets the compositor carry both together for free.
 *
 * There is deliberately no scroll listener and no click-outside handling. Scrolling can
 * no longer misposition a panel that follows its trigger, and a panel whose trigger has
 * scrolled away closes through `onClose` rather than floating over unrelated content.
 * Click-outside stays with the caller, whose trigger detection differs panel by panel.
 */
export function useAnchoredPanel(options: AnchoredPanelOptions): AnchoredPanel {
  const {
    open,
    anchorRef,
    panelRef,
    onClose,
    onEscape,
    gutter,
    align = 'left',
    gap = DEFAULT_ANCHOR_GAP_PX,
    place
  } = options;

  const [position, setPosition] = useState<PanelPlacement>({
    top: 0,
    left: 0,
    openUpward: false
  });
  const [anchorWidth, setAnchorWidth] = useState(0);
  const { present, closing } = useExitPresence(open, DROPDOWN_EXIT_MS);

  const handleAnchorMove = useCallback(
    (anchor: AnchorRect): void => {
      const panel = panelRef.current;
      const space: PanelSpace = {
        anchor,
        // offsetWidth/offsetHeight, NOT getBoundingClientRect: the entrance keyframes
        // scale the panel, and a rect measured mid-animation reports that scaled size,
        // so an upward panel, placed by subtracting its height from the anchor's top,
        // would land on top of the trigger. Both are 0 before the panel mounts.
        panelWidth: panel?.offsetWidth ?? 0,
        panelHeight: panel?.offsetHeight ?? 0,
        // clientWidth/clientHeight over innerWidth/innerHeight: the scrollbar is not
        // space the panel can occupy, and clamping against it leaves the panel under it.
        viewportWidth: document.documentElement.clientWidth || window.innerWidth,
        viewportHeight: document.documentElement.clientHeight || window.innerHeight,
        gutter
      };

      const placement = place ? place(space) : placeBelowAnchor(space, align, gap);
      const next: PanelPlacement = {
        top: placement.top + window.scrollY,
        left: placement.left + window.scrollX,
        openUpward: placement.openUpward,
        availableHeight: placement.availableHeight
      };

      // An anchor sliding along while the panel is pinned at a gutter recomputes to the
      // same place every frame; without this that is a re-render per frame.
      setPosition((prev) => (isSamePlacement(prev, next) ? prev : next));
      setAnchorWidth((prev) =>
        Math.abs(prev - anchor.width) <= POSITION_EPSILON_PX ? prev : anchor.width
      );
    },
    [panelRef, gutter, align, gap, place]
  );

  // Keyed on `present` as well as `open`: the panel mounts a render after `open` flips,
  // and the flip decision needs its measured height, so place it again in the same
  // commit it appears in, before paint, rather than a frame later. This effect stops
  // once `open` goes false; the follow loop below stays on through the exit, so a panel
  // closing while the page reflows under it still fades out against its trigger.
  useLayoutEffect(() => {
    if (!open || !anchorRef.current) return;
    handleAnchorMove(readAnchorRect(anchorRef.current));
  }, [open, present, anchorRef, handleAnchorMove]);

  useAnchorFollow({
    enabled: present,
    anchorRef,
    onAnchorMove: handleAnchorMove,
    // Nothing left to anchor to once the trigger is scrolled off screen.
    onAnchorLost: onClose
  });

  // The follow loop watches the ANCHOR, so a panel that changes its own height reports
  // nothing. A panel opened upward is placed by subtracting its height from the trigger,
  // so a menu that filtering makes shorter would drift off its button as the reader types.
  useEffect(() => {
    const panel = panelRef.current;
    if (!present || panel === null) return;

    const sizeObserver = new ResizeObserver(() => {
      const anchor = anchorRef.current;
      if (anchor !== null) handleAnchorMove(readAnchorRect(anchor));
    });
    sizeObserver.observe(panel);
    return () => sizeObserver.disconnect();
  }, [present, panelRef, anchorRef, handleAnchorMove]);

  useEffect(() => {
    if (!open) return;

    const handleEscape = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape') return;
      onClose();
      onEscape?.();
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [open, onClose, onEscape]);

  return { present, closing, position, anchorWidth };
}
