import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import type { UnifiedNotification } from '@contexts/notifications';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import './CondensedNotificationStrip.css';

/**
 * Each segment casts light in its own status colour, so a glance at the strip reads the same
 * green/red/blue story as the cards it stands in for. Solid colours from getNotificationColor map
 * to their theme -glow tone, which carries one shared alpha across every status so no segment
 * outshines its neighbour. Anything unmapped falls back to the line colour itself: still the right
 * hue, only brighter than a mapped segment.
 */
const GLOW_COLOR_BY_STATUS_COLOR: Record<string, string> = {
  'var(--theme-success)': 'var(--theme-success-glow)',
  'var(--theme-error)': 'var(--theme-error-glow)',
  'var(--theme-warning)': 'var(--theme-warning-glow)',
  'var(--theme-info)': 'var(--theme-info-glow)',
  'var(--theme-waiting)': 'var(--theme-waiting-glow)'
};

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
  /** Every compacted UnifiedNotificationItem, revealed together on expand. */
  children: React.ReactNode;
}

/** One segment of the strip: a mini progress track wearing its notification's status colour. */
const StripSegment: React.FC<{ segment: CondensedStripSegment }> = ({ segment }) => {
  const { notification, color } = segment;
  const isRunning = notification.status === 'running';
  const hasDeterminate =
    isRunning &&
    notification.progressMode !== 'indeterminate' &&
    Number.isFinite(notification.progress);
  // A running op with no numeric progress sweeps instead of showing a misleading full bar.
  const isIndeterminate = isRunning && !hasDeterminate;
  const fillPercent = hasDeterminate ? Math.max(0, Math.min(100, notification.progress ?? 0)) : 100;
  const trackStyle = {
    '--notification-progress-color': color,
    '--condensed-fill': `${fillPercent}%`
  } as React.CSSProperties;

  return (
    <span
      className={`notification-progress-track condensed-notification-track${
        isTerminalNotificationStatus(notification.status) ? '' : ' condensed-notification-pulse'
      }`}
      style={trackStyle}
    >
      {isIndeterminate ? (
        <span className="notification-progress-indeterminate" />
      ) : (
        <span className="notification-progress-fill" />
      )}
    </span>
  );
};

/**
 * A single thin line standing in for every compacted notification at once. Each service keeps
 * its status colour as one segment of the line, so nothing is lost by merging: the whole strip
 * is one disclosure button - hover (fine pointers) or tap/Enter/Space (touch + keyboard)
 * expands all the real UnifiedNotificationItems below it, so cancel, dismiss, and progress all
 * keep working because the revealed elements ARE the same cards.
 */
export const CondensedNotificationStrip: React.FC<CondensedNotificationStripProps> = ({
  segments,
  canHover,
  children
}) => {
  const { t } = useTranslation();
  const [hovered, setHovered] = useState(false);
  // Tap/keyboard expansion lives here, not in the bar: it must survive notification-list churn
  // while the strip is mounted (a finishing run must not snap a tapped-open panel shut), and it
  // resets automatically when the last compacted notification goes away and the strip unmounts.
  const [tapExpanded, setTapExpanded] = useState(false);

  const open = tapExpanded || (canHover && hovered);

  const handleMouseEnter = () => {
    setHovered(true);
  };

  const handleMouseLeave = () => {
    // Leaving the area closes the panel immediately on hover-capable devices: the tap/pin state
    // is a guard against mid-interaction collapse, never a latch. Touch devices never attach
    // these handlers, so a tapped-open panel stays open there until tapped again.
    setHovered(false);
    setTapExpanded(false);
  };

  // A press inside the revealed panel (cancel, dismiss, a tooltip trigger) pins it open, so the
  // hover zone's mouseleave cannot collapse the panel out from under an interaction in progress.
  const pinExpanded = () => {
    if (open && !tapExpanded) {
      setTapExpanded(true);
    }
  };

  // The accessible name states the action the button will actually perform in its current state.
  const ariaLabel = open
    ? t('common.notifications.condensedStripCollapse', { count: segments.length })
    : t('common.notifications.condensedStripToggle', { count: segments.length });

  // While collapsed, the full cards' own live regions are unmounted, so the strip keeps one.
  // It speaks only on a segment's status transition (never progress ticks), and stays silent
  // while the panel is expanded because the revealed cards announce for themselves.
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

  return (
    <div
      className="condensed-notification"
      onMouseEnter={canHover ? handleMouseEnter : undefined}
      onMouseLeave={canHover ? handleMouseLeave : undefined}
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
        className="condensed-notification-line"
        aria-expanded={open}
        aria-label={ariaLabel}
        onClick={() => setTapExpanded((previous) => !previous)}
      >
        <span className="condensed-notification-segments">
          {segments.map((segment) => (
            <StripSegment key={segment.key} segment={segment} />
          ))}
        </span>
        <span className="condensed-notification-glow" aria-hidden="true">
          {segments.map((segment) => (
            <span
              key={segment.key}
              className="condensed-notification-glow-segment"
              style={
                {
                  '--condensed-glow-color':
                    GLOW_COLOR_BY_STATUS_COLOR[segment.color] ?? segment.color
                } as React.CSSProperties
              }
            />
          ))}
        </span>
      </button>
      <div onPointerDown={pinExpanded}>
        <CollapsibleRegion open={open} contentClassName="condensed-notification-expanded">
          {children}
        </CollapsibleRegion>
      </div>
    </div>
  );
};
