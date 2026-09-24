import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { UnifiedNotification } from '@contexts/notifications';
import type { BadgeVariant } from '@components/ui/Badge.types';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import { useMediaQuery } from '@hooks/useMediaQuery';
import { useExitPresence } from '@hooks/useExitPresence';
import './CondensedNotificationStrip.css';

/**
 * How long the pointer has to rest on the line before the panel opens. The strip spans the
 * width of the bar, so a pointer crossing it on the way somewhere else would otherwise flash
 * the panel open and shut in passing. Leaving the line, or the pointer leaving the window,
 * cancels a pending open, so only a deliberate rest reveals the cards.
 */
const HOVER_OPEN_DELAY_MS = 135;

/**
 * How long the revealed panel keeps rendering after it closes, while its fade-and-collapse
 * plays. Slightly longer than the CSS exit animation so the faded end state is what unmounts.
 * The panel floats over the page, so its exit animates transform and opacity only and moves
 * nothing around it. Like the line and segment exits, the unmount rides a plain timeout rather
 * than an animationend event, which can be lost when churn re-renders mid-animation.
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
  /** Status variant from getNotificationVariant, resolved once by the bar and passed down. */
  variant: BadgeVariant;
}

interface CondensedNotificationStripProps {
  /** One segment per compacted service, in stack order; together they fill the line's width. */
  segments: CondensedStripSegment[];
  /** Fine hover-capable pointers reveal on hover; touch and keyboard reveal via the tap toggle. */
  canHover: boolean;
  /** Every compacted UnifiedNotificationItem, revealed together in the floating panel. */
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
 * One segment of the line: the status color dimmed as an underlay across the whole width, the
 * same color solid up to the run's progress (or a sweep while a run reports no numeric
 * progress), breathing softly while non-terminal. A leaving segment fades and hands its width
 * to its neighbours in the same motion. The color comes from the shared status class.
 */
const StripSegment: React.FC<{ segment: CondensedStripSegment; leaving?: boolean }> = ({
  segment,
  leaving
}) => {
  const { notification, variant } = segment;
  const isRunning = notification.status === 'running';
  const hasDeterminate =
    isRunning &&
    notification.progressMode !== 'indeterminate' &&
    Number.isFinite(notification.progress);
  // A running op with no numeric progress sweeps instead of showing a misleading full bar.
  const isIndeterminate = isRunning && !hasDeterminate;
  const fillPercent = hasDeterminate ? Math.max(0, Math.min(100, notification.progress ?? 0)) : 100;
  const segmentStyle = {
    '--seg-fill': `${fillPercent}%`
  } as React.CSSProperties;

  return (
    <span
      className={`condensed-strip-seg notification-status--${variant}${
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
 * A single thin line standing in for every compacted notification at once. Resting a fine
 * pointer on the line, or a click, tap or Enter, reveals all the real UnifiedNotificationItems
 * in a panel that floats directly under the line, so cancel, dismiss, and progress keep working -
 * the revealed elements ARE the same cards. The panel lies over the page instead of pushing it
 * down, so opening and closing it never moves anything below the bar.
 *
 * Closing fades the panel out over PANEL_EXIT_MS and then unmounts; the animation only ever
 * touches transform and opacity.
 */
export const CondensedNotificationStrip: React.FC<CondensedNotificationStripProps> = ({
  segments,
  canHover,
  children
}) => {
  const { t } = useTranslation();
  const wrapperRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const keyboardOpenRef = useRef(false);
  // Whether the open panel was revealed by the pointer resting on the line. A click on the line
  // then keeps it open instead of closing what the rest already showed.
  const hoverOpenRef = useRef(false);
  const openTimerRef = useRef<number | null>(null);
  const cancelPendingOpen = useCallback((): void => {
    if (openTimerRef.current !== null) {
      window.clearTimeout(openTimerRef.current);
      openTimerRef.current = null;
    }
  }, []);
  const close = useCallback((): void => {
    cancelPendingOpen();
    keyboardOpenRef.current = false;
    hoverOpenRef.current = false;
    setOpen(false);
  }, [cancelPendingOpen]);

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
    close();
    const timer = window.setTimeout(() => setLineGone(true), LINE_EXIT_MS);
    return () => window.clearTimeout(timer);
  }, [hasSegments, close]);
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
        // A returning segment owns its key immediately, before the layout effect clears its ghost.
        if (displaySegments.some((segment) => segment.key === ghost.segment.key)) return;
        renderSegments.splice(Math.min(ghost.prevIndex, renderSegments.length), 0, {
          segment: ghost.segment,
          leaving: true
        });
      });
  }
  const panelOpen = open && hasSegments;

  // The panel outlives its close by PANEL_EXIT_MS so the fade can play. Reopening during the
  // fade cancels it (the effect re-runs and clears the timer), and a reduced-motion viewer
  // keeps the instant unmount. When the segments themselves go away the line's own fade-out
  // owns the goodbye, so the panel leaves with it instead of fading twice.
  const prefersReducedMotion = useMediaQuery('(prefers-reduced-motion: reduce)');
  const { present } = useExitPresence(panelOpen, PANEL_EXIT_MS);
  const panelWasOpenRef = useRef(false);
  const panelClosing =
    !panelOpen && !prefersReducedMotion && hasSegments && present && panelWasOpenRef.current;

  const panelVisible = panelOpen || panelClosing;
  useLayoutEffect(() => {
    panelWasOpenRef.current = panelVisible;
  }, [panelVisible]);

  // A hover-open waiting out HOVER_OPEN_DELAY_MS, cancelled by anything that takes the pointer
  // off the line before the delay elapses.
  useEffect(() => cancelPendingOpen, [cancelPendingOpen]);

  // A panel the mouse opened, by resting or by a click, closes once the pointer has left the
  // line and the panel. A keyboard session with focus inside keeps it open.
  const handleMouseLeave = useCallback((): void => {
    if (!canHover) return;
    cancelPendingOpen();
    const el = wrapperRef.current;
    if (keyboardOpenRef.current && el?.contains(document.activeElement)) return;
    close();
  }, [canHover, cancelPendingOpen, close]);

  // Movement inside the strip starts hover intent, regardless of mouseenter ordering. Layout
  // changes alone cannot open it. While open, movement anywhere else closes the panel: a card
  // leaving can shrink the panel out from under a parked pointer, and no mouseleave arrives then.
  useEffect(() => {
    if (!canHover) {
      return;
    }
    const handlePointerMove = (event: PointerEvent): void => {
      if (event.pointerType === 'touch') return;
      const el = wrapperRef.current;
      if (open) {
        if (!(event.target instanceof Node && el?.contains(event.target))) handleMouseLeave();
        return;
      }
      if (!hasSegments || openTimerRef.current !== null) return;
      const under = document.elementFromPoint(event.clientX, event.clientY);
      if (!el?.matches(':hover') || !under || !el.contains(under)) return;
      openTimerRef.current = window.setTimeout(() => {
        openTimerRef.current = null;
        keyboardOpenRef.current = false;
        hoverOpenRef.current = true;
        setOpen(true);
      }, HOVER_OPEN_DELAY_MS);
    };
    document.addEventListener('pointermove', handlePointerMove, { capture: true, passive: true });
    return () => document.removeEventListener('pointermove', handlePointerMove, { capture: true });
  }, [canHover, open, hasSegments, handleMouseLeave]);

  // Ways the pointer can be gone without a final event ever landing where the strip could see
  // it: the window loses focus, the tab hides, or the pointer exits the document in one
  // hardware step. Visibility loss always closes; pointer departure preserves contained
  // keyboard focus. This runs whether or not the panel is open, because the same events
  // can land during a pending hover-open and would otherwise reveal the panel behind a hidden
  // tab, where no later event arrives to close it again.
  useEffect(() => {
    if (!canHover) {
      return;
    }
    const handleVisibility = (): void => {
      if (document.visibilityState === 'hidden') {
        close();
      }
    };
    document.documentElement.addEventListener('mouseleave', handleMouseLeave);
    window.addEventListener('blur', close);
    document.addEventListener('visibilitychange', handleVisibility);
    return () => {
      document.documentElement.removeEventListener('mouseleave', handleMouseLeave);
      window.removeEventListener('blur', close);
      document.removeEventListener('visibilitychange', handleVisibility);
    };
  }, [canHover, close, handleMouseLeave]);

  // A press outside the strip dismisses the disclosure, and Escape returns contained focus
  // to its trigger before the closing panel becomes inert.
  useEffect(() => {
    if (!open) {
      return;
    }
    const handleDocumentPointerDown = (event: PointerEvent): void => {
      keyboardOpenRef.current = false;
      const el = wrapperRef.current;
      if (el && event.target instanceof Node && !el.contains(event.target)) {
        close();
      }
    };
    const handleKeyDown = (event: KeyboardEvent): void => {
      const el = wrapperRef.current;
      if (event.key === 'Escape') {
        if (el?.contains(document.activeElement)) {
          el.querySelector<HTMLButtonElement>('.condensed-strip-line')?.focus({
            preventScroll: true
          });
        }
        close();
        return;
      }
      if (event.key === 'Tab' || (event.target instanceof Node && el?.contains(event.target))) {
        keyboardOpenRef.current = true;
        cancelPendingOpen();
      }
    };
    document.addEventListener('pointerdown', handleDocumentPointerDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('pointerdown', handleDocumentPointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open, close, cancelPendingOpen]);

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
      // A canceled run still ends with status 'completed', which is why the color reads
      // details.cancelled first. Speaking the raw status would say the opposite of the gray
      // beside it.
      const spokenStatus = transitioned.notification.details?.cancelled
        ? 'cancelled'
        : transitioned.notification.status;
      const statusLabel = t(`common.notifications.condensedStatus.${spokenStatus}`, {
        defaultValue: spokenStatus
      });
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
      onMouseLeave={handleMouseLeave}
      onBlur={(event: React.FocusEvent<HTMLDivElement>) => {
        if (
          keyboardOpenRef.current &&
          !(
            event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget)
          )
        ) {
          close();
        }
      }}
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
        onClick={(event: React.MouseEvent<HTMLButtonElement>) => {
          if (!hasSegments) return;
          // A click on a panel the rest revealed keeps it open; from then on it is a panel a
          // click opened, which the next click closes.
          if (open && hoverOpenRef.current) {
            hoverOpenRef.current = false;
            keyboardOpenRef.current = event.detail === 0;
            return;
          }
          if (open) {
            close();
            return;
          }
          cancelPendingOpen();
          keyboardOpenRef.current = event.detail === 0;
          setOpen(true);
        }}
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
              className={`condensed-strip-glow-seg notification-status--${segment.variant}${
                leaving ? ' is-exiting' : ''
              }`}
            />
          ))}
        </span>
      </button>
      {/* Mounted while open AND segments are live, plus the exit fade that follows a close.
          During the line's own fade-out the children the bar passes are already empty, so
          nothing renders there. */}
      {panelVisible && (
        <div
          className={`condensed-strip-panel${panelClosing ? ' is-closing' : ''}`}
          inert={panelClosing}
        >
          {children}
        </div>
      )}
    </div>
  );
};
