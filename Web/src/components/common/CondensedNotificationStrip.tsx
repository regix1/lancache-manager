import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { UnifiedNotification } from '@contexts/notifications';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import './CondensedNotificationStrip.css';

/**
 * Solid status colours map to their theme glow tone, which carries one shared alpha across
 * every status so no segment outshines its neighbour. Anything unmapped falls back to the
 * line colour itself: still the right hue, only brighter than a mapped segment.
 */
const GLOW_COLOR_BY_STATUS_COLOR: Record<string, string> = {
  'var(--theme-success)': 'var(--theme-success-glow)',
  'var(--theme-error)': 'var(--theme-error-glow)',
  'var(--theme-warning)': 'var(--theme-warning-glow)',
  'var(--theme-info)': 'var(--theme-info-glow)',
  'var(--theme-waiting)': 'var(--theme-waiting-glow)'
};

/**
 * While the panel is open on a hover device, how often the browser's hover state is re-asked.
 * A run finishing mid-hover re-renders the strip and can swallow the wrapper's mouseleave, so
 * enter/leave alone could leave the panel open after the pointer had gone. Nothing about the
 * strip's geometry changes under the pointer here (the line's height is constant and the panel
 * is an overlay, not an in-flow expansion), so the browser's own :hover answer is reliable and
 * only needs re-asking while the panel is open. The timer exists only while open.
 */
const OPEN_HOVER_RECHECK_MS = 250;

/**
 * How long the closing panel stays mounted while its exit animation plays. Slightly longer
 * than the CSS exit animation so the faded-out end state (held by `forwards`) is what
 * unmounts, never a mid-fade frame. The unmount rides this plain timeout, NOT an
 * animationend/transitionend event: completion events can be lost when notification churn
 * re-renders mid-animation, and a timeout always fires. Even if the exit animation itself
 * were to freeze, the panel is an overlay, so the worst case is cards lingering for these
 * milliseconds - never residual layout height under the line.
 */
const PANEL_EXIT_MS = 260;

/**
 * How long the strip keeps rendering its final segments after the last compacted notification
 * goes away, while the line's fade-out plays. Slightly longer than the CSS exit animation so
 * the faded end state is what unmounts. A plain timeout for the same reason as PANEL_EXIT_MS.
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
  /** Every compacted UnifiedNotificationItem, revealed together in the overlay panel. */
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
 * an overlay panel anchored under the line, so cancel, dismiss, and progress keep working -
 * the revealed elements ARE the same cards.
 *
 * The panel is an absolutely positioned dropdown, never an in-flow expansion: the strip's
 * layout height equals the line's height at all times, so no animation, churn, or interrupted
 * close can ever leave residual height under the line. Closing is a plain unmount.
 */
export const CondensedNotificationStrip: React.FC<CondensedNotificationStripProps> = ({
  segments,
  canHover,
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
  // The panel outlives `open` by PANEL_EXIT_MS so its exit animation can play. Whenever
  // (closed, still mounted) holds after a commit, this effect has re-armed the unmount timer -
  // both pieces of that pair are dependencies - so the panel can never be left mounted with no
  // teardown pending, no matter how fast hover flickers toggle `open`.
  const [panelMounted, setPanelMounted] = useState(false);
  useEffect(() => {
    if (open) {
      setPanelMounted(true);
      return;
    }
    if (!panelMounted) {
      return;
    }
    const timer = window.setTimeout(() => setPanelMounted(false), PANEL_EXIT_MS);
    return () => window.clearTimeout(timer);
  }, [open, panelMounted]);

  const handleMouseEnter = (): void => {
    if (canHover) {
      setOpen(true);
    }
  };
  const handleMouseLeave = (): void => {
    if (canHover) {
      setOpen(false);
    }
  };

  // The swallowed-mouseleave healer (see OPEN_HOVER_RECHECK_MS).
  useEffect(() => {
    if (!canHover || !open) {
      return;
    }
    const timer = window.setInterval(() => {
      const el = wrapperRef.current;
      if (el && !el.matches(':hover')) {
        setOpen(false);
      }
    }, OPEN_HOVER_RECHECK_MS);
    return () => window.clearInterval(timer);
  }, [canHover, open]);

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
                  '--seg-glow-color': GLOW_COLOR_BY_STATUS_COLOR[segment.color] ?? segment.color
                } as React.CSSProperties
              }
            />
          ))}
        </span>
      </button>
      {/* Hard-gated on live segments: during the line's fade-out the children the bar passes
          are already empty, and letting the panel play its own exit then would fade an empty
          background slab. It unmounts instantly instead. */}
      {panelMounted && hasSegments && (
        <div className={`condensed-strip-panel${open ? '' : ' is-leaving'}`}>{children}</div>
      )}
    </div>
  );
};
