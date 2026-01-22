import React from 'react';
import { useTranslation } from 'react-i18next';

export interface EventFrameTokens {
  offset?: number;
  radius?: number;
  strokeWidth?: number;
  dashArray?: string;
  topMargin?: number;
  badgeOffset?: number;
}

export interface EventFrameProps {
  children: React.ReactNode;
  enabled?: boolean;
  color?: string;
  label?: string;
  badgeText?: string;
  showBadge?: boolean;
  tokens?: EventFrameTokens;
  className?: string;
}

const defaultTokens: Required<EventFrameTokens> = {
  offset: 10,
  radius: 12,
  strokeWidth: 2,
  dashArray: '11 6', // legacy token (unused in CSS shimmer variant)
  topMargin: 16,
  badgeOffset: 12
};

/**
 * Renders a non-intrusive animated frame around content.
 * Uses a solid border with a pulsing glow and live badge.
 */
const EventFrame: React.FC<EventFrameProps> = ({
  children,
  enabled = true,
  color = 'var(--theme-primary)',
  label,
  badgeText,
  showBadge = true,
  tokens,
  className
}) => {
  const { t } = useTranslation();
  const mergedTokens = { ...defaultTokens, ...tokens };

  if (!enabled) {
    return <>{children}</>;
  }

  const shouldShowBadge = showBadge && Boolean(label);
  const resolvedBadgeText = badgeText ?? t('eventFrame.badge');

  // Precompute values for CSS - avoids calc() multiplication which iOS Safari doesn't support
  const wrapperStyle = {
    '--event-frame-color': color,
    '--event-frame-offset': `${mergedTokens.offset}px`,
    '--event-frame-offset-negative': `-${mergedTokens.offset}px`,
    '--event-frame-border-radius': `${mergedTokens.radius + mergedTokens.offset}px`,
    '--event-frame-stroke-width': `${mergedTokens.strokeWidth}px`,
    '--event-frame-top-margin': `${mergedTokens.topMargin}px`,
    '--event-frame-badge-offset': `${mergedTokens.badgeOffset}px`
  } as React.CSSProperties;

  const wrapperClassName = [
    'event-frame-wrapper',
    'event-frame-active',
    className
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <div
      className={wrapperClassName}
      style={wrapperStyle}
    >
      <div className="event-frame-border" aria-hidden="true" />
      <div className="event-frame-content">
        {children}
      </div>

      {/* Badge positioned at top center of frame */}
      {shouldShowBadge && (
        <div className="event-frame-badge-container">
          <div className="event-frame-badge-bg" />
          <div className="event-frame-badge">
            <span className="event-frame-badge-dot event-frame-live-dot" />
            <span className="event-frame-badge-name">{label}</span>
            {resolvedBadgeText && (
              <span className="event-frame-badge-tag">{resolvedBadgeText}</span>
            )}
          </div>
        </div>
      )}
    </div>
  );
};

export default EventFrame;
