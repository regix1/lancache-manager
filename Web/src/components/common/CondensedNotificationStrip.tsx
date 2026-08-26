import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { UnifiedNotification } from '@contexts/notifications';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { useMediaQuery } from '@hooks/useMediaQuery';
import './CondensedNotificationStrip.css';

/**
 * Solid status colours map to their theme glow tone, which carries one shared alpha across
 * the status tokens so no segment outshines its neighbour.
 *
 * Anything unmapped casts no light rather than falling back to the line colour. The glow is
 * painted as a gradient from this value, so a fully opaque colour would start the band at full
 * strength and read as a second solid bar under the line instead of as light coming off it.
 * Every status colour the strip can receive is mapped below, so this fallback is a guard
 * against a future unmapped colour, not a case that renders today.
 */
const UNMAPPED_GLOW_COLOR = 'transparent';

const GLOW_COLOR_BY_STATUS_COLOR: Record<string, string> = {
  'var(--theme-success)': 'var(--theme-success-glow)',
  'var(--theme-error)': 'var(--theme-error-glow)',
  'var(--theme-warning)': 'var(--theme-warning-glow)',
  'var(--theme-info)': 'var(--theme-info-glow)',
  'var(--theme-waiting)': 'var(--theme-waiting-glow)',
  // The grey a stopped run is drawn in has no -glow sibling, so it borrows the muted tier of
  // the same colour. That tier is fainter than the shared glow alpha, which suits the one
  // status that is deliberately quiet.
  'var(--theme-text-secondary)': 'var(--theme-text-secondary-muted)'
};

/**
 * While the panel is open on a hover device, how often the pointer's whereabouts are re-asked.
 * The panel opens in flow, so a revealed card finishing can shrink the panel UNDER a parked
 * pointer, and the browser neither fires a mouseleave nor re-evaluates :hover until the pointer
 * physically moves - enter/leave alone would leave the panel latched open. The recheck decides
 * with two signals whose blind spots are disjoint: `matches(':hover')` goes stale-true exactly
 * in that layout-changed-under-a-parked-pointer case, while hit-testing the last known pointer
 * coordinates goes stale-true only when the pointer left the window in one hardware step (no
 * final pointermove ever landed). Either signal saying "gone" closes the panel. The timer
 * exists only while open.
 */
const OPEN_HOVER_RECHECK_MS = 250;

/**
 * How long the pointer has to rest on the line before the panel opens. The strip spans the
 * width of the bar, so a pointer crossing it on the way somewhere else would otherwise flash
 * the panel open and shut in passing. Leaving the line, or the pointer leaving the window,
 * cancels a pending open, so only a deliberate rest reveals the cards. Closing stays instant.
 */
const HOVER_OPEN_DELAY_MS = 135;

/**
 * How long the revealed panel keeps rendering after it closes, while its fade-and-collapse
 * plays. Slightly longer than the CSS exit animation so the faded end state is what unmounts.
 * The exit animates transform and opacity only, INSIDE the height the layout has already
 * reserved, and that height is handed back in one step at unmount - so a close interrupted by
 * a reopen or by notification churn can never strand a partial height under the line. Like the
 * line and segment exits, the unmount rides a plain timeout rather than an animationend event,
 * which can be lost when churn re-renders mid-animation.
 */
const PANEL_EXIT_MS = 150;

/**
 * How long the strip keeps rendering its final segments after the last compacted notification
 * goes away, while the line's fade-out plays. Slightly longer than the CSS exit animation so
 * the faded end state is what unmounts. The unmount rides this plain timeout, NOT an
 * animationend event: completion events can be lost when notification churn re-renders
 * mid-animation, and a timeout always fires.
 */
const LINE_EXIT_MS = 450;

/**
 * How long a single departing segment stays rendered while its fade-and-shrink plays, when
 * other segments remain on the line. Matches LINE_EXIT_MS so one segment leaving feels like
 * the whole line leaving. Ghost expiry rides timestamps swept by a state-armed timeout, so a
 * flurry of arrivals and departures can never cancel a pending removal into a stuck ghost.
 */
const SEG_EXIT_MS = 450;

interface CondensedStripSegment {
  /** Stable per-service group key; also the live-region transition identity. */
  key: string;
  /** The group's representative notification (the live run when one exists). */
  notification: UnifiedNotification;
  /** Status colour from getNotificationColor, resolved once by the bar and passed down. */
  color: string;
}

interface CondensedNotificationStripProps {
  /** One segment per compacted service, in stack order; together they fill the line's width. */
  segments: CondensedStripSegment[];
  /** Fine hover-capable pointers reveal on hover; touch and keyboard reveal via the tap toggle. */
  canHover: boolean;
  /**
   * Reports whether the revealed cards are currently in the bar's flow, so the bar can keep its
   * bottom border and shadow under them even when no full-size cards render below the strip.
   */
  onOpenChange?: (open: boolean) => void;
  /** Every compacted UnifiedNotificationItem, revealed together in the in-flow panel. */
  children: React.ReactNode;
}

/** A departed segment kept on the line while its exit animation plays. */
interface ExitingSegment {
  segment: CondensedStripSegment;
  /** Where the segment sat before it departed, so the exit plays in place. */
  prevIndex: number;
  /** Timestamp after which the ghost is swept away. */
  expiresAt: number;
}

/**
 * One segment of the line: the status colour dimmed as an underlay across the whole width, the
 * same colour solid up to the run's progress (or a sweep while a run reports no numeric
 * progress), breathing softly while non-terminal. A leaving segment fades and hands its width
 * to its neighbours in the same motion.
 */
const StripSegment: React.FC<{ segment: CondensedStripSegment; leaving?: boolean }> = ({
  segment,
  leaving
}) => {
  const { notification, color } = segment;
  const isRunning = notification.status === 'running';
  const hasDeterminate =
    isRunning &&
    notification.progressMode !== 'indeterminate' &&
    Number.isFinite(notification.progress);
  // A running op with no numeric progress sweeps instead of showing a misleading full bar.
  const isIndeterminate = isRunning && !hasDeterminate;
  const fillPercent = hasDeterminate ? Math.max(0, Math.min(100, notification.progress ?? 0)) : 100;
  const segmentStyle = {
    '--seg-color': color,
    '--seg-fill': `${fillPercent}%`
  } as React.CSSProperties;

  return (
    <span
      className={`condensed-strip-seg${
        isTerminalNotificationStatus(notification.status) ? '' : ' condensed-strip-seg-live'
      }${leaving ? ' is-exiting' : ''}`}
      style={segmentStyle}
    >
      {isIndeterminate ? (
        <span className="condensed-strip-sweep" />
      ) : (
        <span className="condensed-strip-fill" />
      )}
    </span>
  );
};

/**
 * A single thin line standing in for every compacted notification at once. Hover (fine
 * pointers) or tap/Enter (touch and keyboard) reveals all the real UnifiedNotificationItems in
 * an in-flow panel directly under the line, so cancel, dismiss, and progress keep working -
 * the revealed elements ARE the same cards, sharing one stack with any full-size cards the bar
 * renders below instead of overlaying them.
 *
 * Closing fades the panel out over PANEL_EXIT_MS and then unmounts. The animation only ever
 * touches transform and opacity, so the reserved height stays whole until the unmount releases
 * it all at once - never animate the panel's height or margins here, because an in-flow
 * collapse interrupted mid-flight strands residual space under the line.
 */
export const CondensedNotificationStrip: React.FC<CondensedNotificationStripProps> = ({
  segments,
  canHover,
  onOpenChange,
  children
}) => {
  const { t } = useTranslation();
  const wrapperRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);

  // When the last compacted notification goes away the bar hands this component an empty
  // segments array, but an instant unmount blinks the line off. The last non-empty segments
  // are kept as a ghost for LINE_EXIT_MS while the CSS fade-out plays; only then does the
  // strip render null. The ghost is read synchronously in render, so the very first
  // empty-segments render already shows the fading line rather than a one-frame gap.
  const lastSegmentsRef = useRef<CondensedStripSegment[]>([]);
  const hasSegments = segments.length > 0;
  if (hasSegments) {
    lastSegmentsRef.current = segments;
  }
  const [lineGone, setLineGone] = useState(!hasSegments);
  useEffect(() => {
    if (hasSegments) {
      setLineGone(false);
      return;
    }
    if (lastSegmentsRef.current.length === 0) {
      setLineGone(true);
      return;
    }
    setOpen(false);
    const timer = window.setTimeout(() => setLineGone(true), LINE_EXIT_MS);
    return () => window.clearTimeout(timer);
  }, [hasSegments]);
  const displaySegments = hasSegments ? segments : lastSegmentsRef.current;

  // Per-segment exits, for when ONE service's notifications go away while others stay: the
  // departed segment is kept as a ghost at its old position and fades while handing its width
  // to its neighbours, instead of blinking off and snapping the survivors wider. When ALL
  // segments depart together the whole-line fade above owns the goodbye, so no ghosts then.
  //
  // This runs as a layout effect, not a passive one: a passive effect fires AFTER paint, so the
  // browser would first paint the frame where the departed segment is already gone and the
  // survivors have flexed out to fill its place, and only then would the ghost be spliced back
  // in and the survivors snap narrow again to make room for its fade. That paint-then-correct
  // double step reads as a flicker each time a segment leaves. A layout effect commits the ghost
  // before the browser paints, so the survivors hand off their width in one smooth motion.
  const [exiting, setExiting] = useState<Map<string, ExitingSegment>>(new Map());
  const prevByKeyRef = useRef<Map<string, { segment: CondensedStripSegment; index: number }>>(
    new Map()
  );
  useLayoutEffect(() => {
    const currentKeys = new Set(segments.map((segment) => segment.key));
    const departed: ExitingSegment[] = [];
    if (currentKeys.size > 0) {
      prevByKeyRef.current.forEach((entry, key) => {
        if (!currentKeys.has(key)) {
          departed.push({
            segment: entry.segment,
            prevIndex: entry.index,
            expiresAt: Date.now() + SEG_EXIT_MS
          });
        }
      });
    }
    prevByKeyRef.current = new Map(
      segments.map((segment, index) => [segment.key, { segment, index }])
    );
    setExiting((previous) => {
      let changed = false;
      const next = new Map(previous);
      if (currentKeys.size === 0 && next.size > 0) {
        // The whole-line fade owns this departure.
        return new Map();
      }
      for (const ghost of departed) {
        if (!next.has(ghost.segment.key)) {
          next.set(ghost.segment.key, ghost);
          changed = true;
        }
      }
      next.forEach((_, key) => {
        // A service that came back mid-exit reclaims its key as a live segment.
        if (currentKeys.has(key)) {
          next.delete(key);
          changed = true;
        }
      });
      return changed ? next : previous;
    });
  }, [segments]);

  // Ghost sweeper, armed by the exiting STATE rather than any particular change: while ghosts
  // exist, a timeout to the soonest expiry is always pending, so churn cannot cancel a removal.
  useEffect(() => {
    if (exiting.size === 0) {
      return;
    }
    const soonest = Math.min(...[...exiting.values()].map((ghost) => ghost.expiresAt));
    const timer = window.setTimeout(
      () => {
        setExiting((previous) => {
          const cutoff = Date.now();
          let changed = false;
          const next = new Map(previous);
          next.forEach((ghost, key) => {
            if (ghost.expiresAt <= cutoff) {
              next.delete(key);
              changed = true;
            }
          });
          return changed ? next : previous;
        });
      },
      Math.max(0, soonest - Date.now())
    );
    return () => window.clearTimeout(timer);
  }, [exiting]);

  // Live segments in order, with ghosts spliced back into their remembered positions.
  const renderSegments: { segment: CondensedStripSegment; leaving: boolean }[] =
    displaySegments.map((segment) => ({ segment, leaving: false }));
  if (hasSegments && exiting.size > 0) {
    [...exiting.values()]
      .sort((a, b) => a.prevIndex - b.prevIndex)
      .forEach((ghost) => {
        renderSegments.splice(Math.min(ghost.prevIndex, renderSegments.length), 0, {
          segment: ghost.segment,
          leaving: true
        });
      });
  }
  // The panel renders in the bar's flow, so the bar needs to know when it is visible to keep
  // its bottom edge under the revealed cards. Report before paint so the panel and that edge
  // cannot appear in separate frames. The latest-callback ref keeps prop churn from re-reporting.
  const panelOpen = open && hasSegments;

  // The panel outlives its close by PANEL_EXIT_MS so the fade can play. Reopening during the
  // fade cancels it (the effect re-runs and clears the timer), and a reduced-motion viewer
  // keeps the instant unmount. When the segments themselves go away the line's own fade-out
  // owns the goodbye, so the panel leaves with it instead of fading twice.
  const prefersReducedMotion = useMediaQuery('(prefers-reduced-motion: reduce)');
  const [panelClosing, setPanelClosing] = useState(false);
  const panelWasOpenRef = useRef(false);
  useEffect(() => {
    if (panelOpen) {
      panelWasOpenRef.current = true;
      setPanelClosing(false);
      return;
    }
    if (!panelWasOpenRef.current || prefersReducedMotion || !hasSegments) {
      return;
    }
    panelWasOpenRef.current = false;
    setPanelClosing(true);
    const timer = window.setTimeout(() => setPanelClosing(false), PANEL_EXIT_MS);
    return () => window.clearTimeout(timer);
  }, [panelOpen, prefersReducedMotion, hasSegments]);

  const panelVisible = panelOpen || panelClosing;
  const onOpenChangeRef = useRef(onOpenChange);
  useEffect(() => {
    onOpenChangeRef.current = onOpenChange;
  });
  useLayoutEffect(() => {
    onOpenChangeRef.current?.(panelVisible);
  }, [panelVisible]);
  useEffect(() => () => onOpenChangeRef.current?.(false), []);

  // A hover-open waiting out HOVER_OPEN_DELAY_MS, cancelled by anything that takes the pointer
  // off the line before the delay elapses.
  const openTimerRef = useRef<number | null>(null);
  const cancelPendingOpen = useCallback((): void => {
    if (openTimerRef.current !== null) {
      window.clearTimeout(openTimerRef.current);
      openTimerRef.current = null;
    }
  }, []);
  useEffect(() => cancelPendingOpen, [cancelPendingOpen]);

  const handleMouseEnter = (): void => {
    if (canHover) {
      cancelPendingOpen();
      openTimerRef.current = window.setTimeout(() => {
        openTimerRef.current = null;
        setOpen(true);
      }, HOVER_OPEN_DELAY_MS);
    }
  };
  const handleMouseLeave = (): void => {
    if (canHover) {
      cancelPendingOpen();
      setOpen(false);
    }
  };

  // Last known pointer position for the open-state recheck's hit-test. A capture-phase passive
  // listener storing two numbers costs nothing and cannot be swallowed by anything the strip
  // renders, so the coordinates are always current when the recheck needs them.
  const lastPointerRef = useRef<{ x: number; y: number } | null>(null);
  useEffect(() => {
    if (!canHover) {
      return;
    }
    const handlePointerMove = (event: PointerEvent): void => {
      lastPointerRef.current = { x: event.clientX, y: event.clientY };
    };
    document.addEventListener('pointermove', handlePointerMove, { capture: true, passive: true });
    return () => document.removeEventListener('pointermove', handlePointerMove, { capture: true });
  }, [canHover]);

  // The open-state recheck (see OPEN_HOVER_RECHECK_MS): both signals must still place the
  // pointer on the strip, because each one's stale-true case is the other's reliable case.
  useEffect(() => {
    if (!canHover || !open) {
      return;
    }
    const timer = window.setInterval(() => {
      const el = wrapperRef.current;
      if (!el) {
        return;
      }
      if (!el.matches(':hover')) {
        setOpen(false);
        return;
      }
      const coords = lastPointerRef.current;
      if (coords) {
        const under = document.elementFromPoint(coords.x, coords.y);
        if (!under || !el.contains(under)) {
          setOpen(false);
        }
      }
    }, OPEN_HOVER_RECHECK_MS);
    return () => window.clearInterval(timer);
  }, [canHover, open]);

  // Ways the pointer can be gone without a final event ever landing where the strip could see
  // it: the window loses focus, the tab hides, or the pointer exits the document in one
  // hardware step. Each closes the panel outright instead of waiting on a recheck tick whose
  // inputs may be stale. This runs whether or not the panel is open, because the same events
  // can land during a pending hover-open and would otherwise reveal the panel behind a hidden
  // tab, where no later event arrives to close it again.
  useEffect(() => {
    if (!canHover) {
      return;
    }
    const close = (): void => {
      cancelPendingOpen();
      lastPointerRef.current = null;
      setOpen(false);
    };
    const handleVisibility = (): void => {
      if (document.visibilityState === 'hidden') {
        close();
      }
    };
    document.documentElement.addEventListener('mouseleave', close);
    window.addEventListener('blur', close);
    document.addEventListener('visibilitychange', handleVisibility);
    return () => {
      document.documentElement.removeEventListener('mouseleave', close);
      window.removeEventListener('blur', close);
      document.removeEventListener('visibilitychange', handleVisibility);
    };
  }, [canHover, cancelPendingOpen]);

  // Tap-opened panels (no hover to end them) close on a press outside the strip, and any open
  // panel closes on Escape.
  useEffect(() => {
    if (!open) {
      return;
    }
    const handleDocumentPointerDown = (event: PointerEvent): void => {
      const el = wrapperRef.current;
      if (!canHover && el && event.target instanceof Node && !el.contains(event.target)) {
        setOpen(false);
      }
    };
    const handleKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        setOpen(false);
      }
    };
    document.addEventListener('pointerdown', handleDocumentPointerDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('pointerdown', handleDocumentPointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open, canHover]);

  // The accessible name states the action the button will perform in its current state.
  const ariaLabel = open
    ? t('common.notifications.condensedStripCollapse', { count: displaySegments.length })
    : t('common.notifications.condensedStripToggle', { count: displaySegments.length });

  // While collapsed, the cards' own live regions are unmounted, so the strip keeps one. It
  // speaks only on a segment's status transition (never progress ticks) and stays silent while
  // the panel is open, because the revealed cards announce for themselves.
  const [liveStatusText, setLiveStatusText] = useState('');
  const [liveAssertive, setLiveAssertive] = useState(false);
  const lastStatusesRef = useRef<Map<string, UnifiedNotification['status']>>(new Map());
  useEffect(() => {
    const previous = lastStatusesRef.current;
    const next = new Map<string, UnifiedNotification['status']>();
    let transitioned: CondensedStripSegment | null = null;
    for (const segment of segments) {
      next.set(segment.key, segment.notification.status);
      const before = previous.get(segment.key);
      if (before !== undefined && before !== segment.notification.status) {
        transitioned = segment;
      }
    }
    lastStatusesRef.current = next;
    if (transitioned && !open) {
      const statusLabel = t(
        `common.notifications.condensedStatus.${transitioned.notification.status}`,
        { defaultValue: transitioned.notification.status }
      );
      setLiveAssertive(transitioned.notification.status === 'failed');
      setLiveStatusText(
        t('common.notifications.condensedLive', {
          title: transitioned.notification.message,
          status: statusLabel
        })
      );
    }
  }, [segments, open, t]);

  if (!hasSegments && (lineGone || displaySegments.length === 0)) {
    return null;
  }

  return (
    <div
      ref={wrapperRef}
      className={`condensed-strip${hasSegments ? '' : ' is-vanishing'}`}
      onMouseEnter={handleMouseEnter}
      onMouseLeave={handleMouseLeave}
    >
      <span
        className="sr-only"
        role={liveAssertive ? 'alert' : 'status'}
        aria-live={liveAssertive ? 'assertive' : 'polite'}
        aria-atomic="true"
      >
        {liveStatusText}
      </span>
      <button
        type="button"
        className="condensed-strip-line"
        aria-expanded={open}
        aria-label={ariaLabel}
        onClick={() => setOpen((previous) => !previous)}
      >
        <span className="condensed-strip-segments">
          {renderSegments.map(({ segment, leaving }) => (
            <StripSegment key={segment.key} segment={segment} leaving={leaving} />
          ))}
        </span>
        <span className="condensed-strip-glow" aria-hidden="true">
          {renderSegments.map(({ segment, leaving }) => (
            <span
              key={segment.key}
              className={`condensed-strip-glow-seg${leaving ? ' is-exiting' : ''}`}
              style={
                {
                  '--seg-glow-color':
                    GLOW_COLOR_BY_STATUS_COLOR[segment.color] ?? UNMAPPED_GLOW_COLOR
                } as React.CSSProperties
              }
            />
          ))}
        </span>
      </button>
      {/* Mounted while open AND segments are live, plus the exit fade that follows a close.
          During the line's own fade-out the children the bar passes are already empty, so
          nothing renders there. */}
      {panelVisible && (
        <div className={`condensed-strip-panel${panelClosing ? ' is-closing' : ''}`}>
          {children}
        </div>
      )}
    </div>
  );
};
