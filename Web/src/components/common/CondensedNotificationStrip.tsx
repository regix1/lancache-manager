import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import type { UnifiedNotification } from '@contexts/notifications';
import { isTerminalNotificationStatus } from '@contexts/notifications/notificationStatus';
import './CondensedNotificationStrip.css';

/**
 * Grace period between the pointer leaving the hover zone and the panel collapsing. Tooltip
 * content inside the expanded cards portals to document.body, so hovering it fires mouseleave
 * here even though the pointer is visually still on a card; the delay absorbs that transit
 * so the panel does not collapse mid-interaction.
 */
const HOVER_COLLAPSE_DELAY_MS = 200;

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
  /** True when the user has explicitly expanded the strip (tap or Enter/Space). */
  tapExpanded: boolean;
  /** Toggles tapExpanded in the bar's ephemeral expanded-id set. */
  onTapToggle: () => void;
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
  tapExpanded,
  onTapToggle,
  children
}) => {
  const { t } = useTranslation();
  const [hovered, setHovered] = useState(false);
  const hoverCollapseTimerRef = useRef<number | null>(null);

  const open = tapExpanded || (canHover && hovered);

  const handleMouseEnter = () => {
    if (hoverCollapseTimerRef.current !== null) {
      window.clearTimeout(hoverCollapseTimerRef.current);
      hoverCollapseTimerRef.current = null;
    }
    setHovered(true);
  };

  const handleMouseLeave = () => {
    if (hoverCollapseTimerRef.current !== null) {
      window.clearTimeout(hoverCollapseTimerRef.current);
    }
    hoverCollapseTimerRef.current = window.setTimeout(() => {
      hoverCollapseTimerRef.current = null;
      setHovered(false);
    }, HOVER_COLLAPSE_DELAY_MS);
  };

  useEffect(() => {
    return () => {
      if (hoverCollapseTimerRef.current !== null) {
        window.clearTimeout(hoverCollapseTimerRef.current);
      }
    };
  }, []);

  // A press inside the revealed panel (cancel, dismiss, a tooltip trigger) pins it open, so the
  // hover zone's mouseleave cannot collapse the panel out from under an interaction in progress.
  const pinExpanded = () => {
    if (open && !tapExpanded) {
      onTapToggle();
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
        onClick={onTapToggle}
      >
        <span className="condensed-notification-segments">
          {segments.map((segment) => (
            <StripSegment key={segment.key} segment={segment} />
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
