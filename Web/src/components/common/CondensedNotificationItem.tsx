import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import type { UnifiedNotification } from '@contexts/notifications';
import './CondensedNotificationItem.css';

/**
 * Grace period between the pointer leaving the hover zone and the card collapsing. Tooltip
 * content inside the expanded card portals to document.body, so hovering it fires mouseleave
 * here even though the pointer is visually still on the card; the delay absorbs that transit
 * so the card does not collapse mid-interaction.
 */
const HOVER_COLLAPSE_DELAY_MS = 200;

interface CondensedNotificationItemProps {
  /**
   * Representative notification for the line's aria-label, colour, and fill. When the line
   * discloses a service's whole group, this is the live run if one exists.
   */
  notification: UnifiedNotification;
  /** Total notifications this line discloses; >1 folds a service's toast and run into one line. */
  groupCount?: number;
  /** Line colour resolved by the bar: theme accent while work is live, status colour terminal. */
  color: string;
  /** Fine hover-capable pointers reveal on hover; touch and keyboard reveal via the tap toggle. */
  canHover: boolean;
  /** True when the user has explicitly expanded this line (tap or Enter/Space). */
  tapExpanded: boolean;
  /** Toggles tapExpanded in the bar's ephemeral expanded-id set. */
  onTapToggle: () => void;
  /** The full UnifiedNotificationItem revealed on expand (cancel/dismiss stay wired). */
  children: React.ReactNode;
}

/**
 * A thin status-coloured line standing in for a full notification card. It is a disclosure
 * button: hover (fine pointers) or tap/Enter/Space (touch + keyboard) expands the real
 * UnifiedNotificationItem below it via the house CollapsibleRegion primitive, so cancel,
 * dismiss, and progress all keep working because the revealed element IS the same card.
 */
export const CondensedNotificationItem: React.FC<CondensedNotificationItemProps> = ({
  notification,
  groupCount = 1,
  color,
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

  // A press inside the revealed card (cancel, dismiss, a tooltip trigger) pins it open, so the
  // hover zone's mouseleave cannot collapse the card out from under an interaction in progress.
  const pinExpanded = () => {
    if (open && !tapExpanded) {
      onTapToggle();
    }
  };

  const isRunning = notification.status === 'running';
  const hasDeterminate =
    isRunning &&
    notification.progressMode !== 'indeterminate' &&
    Number.isFinite(notification.progress);
  // A running op with no numeric progress sweeps instead of showing a misleading full bar.
  const isIndeterminate = isRunning && !hasDeterminate;
  const fillPercent = hasDeterminate ? Math.max(0, Math.min(100, notification.progress ?? 0)) : 100;

  const statusLabel = t(`common.notifications.condensedStatus.${notification.status}`, {
    defaultValue: notification.status
  });
  const percentText = hasDeterminate ? fillPercent.toFixed(0) : null;
  const labelParams = {
    title: notification.message,
    status: statusLabel,
    ...(percentText !== null ? { percent: percentText } : {})
  };
  // The accessible name states the action the button will actually perform in its current state.
  const baseAriaLabel = open
    ? percentText !== null
      ? t('common.notifications.condensedCollapseWithPercent', labelParams)
      : t('common.notifications.condensedCollapse', labelParams)
    : percentText !== null
      ? t('common.notifications.condensedToggleWithPercent', labelParams)
      : t('common.notifications.condensedToggle', labelParams);
  // Expanding a grouped line reveals more than the representative card, so the name says so.
  const extraCount = Math.max(0, groupCount - 1);
  const ariaLabel =
    extraCount > 0
      ? `${baseAriaLabel} ${t('common.notifications.condensedGroupSuffix', { count: extraCount })}`
      : baseAriaLabel;

  // While collapsed, the full card's own live region is unmounted, so the line keeps its own.
  // It speaks only on status transitions (never progress ticks), and stays silent while the
  // card is expanded because the revealed card announces for itself.
  const [liveStatusText, setLiveStatusText] = useState('');
  const lastAnnouncedStatusRef = useRef(notification.status);
  useEffect(() => {
    if (notification.status === lastAnnouncedStatusRef.current) {
      return;
    }
    lastAnnouncedStatusRef.current = notification.status;
    if (!open) {
      setLiveStatusText(
        t('common.notifications.condensedLive', {
          title: notification.message,
          status: statusLabel
        })
      );
    }
  }, [notification.status, notification.message, statusLabel, open, t]);

  const trackStyle = {
    '--notification-progress-color': color,
    '--condensed-fill': `${fillPercent}%`
  } as React.CSSProperties;

  return (
    <div
      className="condensed-notification"
      onMouseEnter={canHover ? handleMouseEnter : undefined}
      onMouseLeave={canHover ? handleMouseLeave : undefined}
    >
      <span
        className="sr-only"
        role={notification.status === 'failed' ? 'alert' : 'status'}
        aria-live={notification.status === 'failed' ? 'assertive' : 'polite'}
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
        <span
          className="notification-progress-track condensed-notification-track"
          style={trackStyle}
        >
          {isIndeterminate ? (
            <span className="notification-progress-indeterminate" />
          ) : (
            <span className="notification-progress-fill" />
          )}
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
