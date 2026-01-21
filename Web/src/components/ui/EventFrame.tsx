import React, { useEffect, useState } from 'react';
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
  dashArray: '10 6',
  topMargin: 16,
  badgeOffset: 12
};

/**
 * Renders a non-intrusive animated frame around content.
 * Uses CSS-based positioning (no portal) - the frame follows the element naturally.
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
  const [isAnimating, setIsAnimating] = useState(false);
  const mergedTokens = { ...defaultTokens, ...tokens };

  useEffect(() => {
    if (!enabled) {
      setIsAnimating(false);
      return;
    }

    setIsAnimating(true);
  }, [enabled]);

  if (!enabled) {
    return <>{children}</>;
  }

  const shouldShowBadge = showBadge && Boolean(label);
  const resolvedBadgeText = badgeText ?? t('eventFrame.badge');

  const wrapperStyle = {
    '--event-frame-color': color,
    '--event-frame-offset': `${mergedTokens.offset}px`,
    '--event-frame-radius': `${mergedTokens.radius}px`,
    '--event-frame-stroke-width': `${mergedTokens.strokeWidth}px`,
    '--event-frame-dash-array': mergedTokens.dashArray,
    '--event-frame-top-margin': `${mergedTokens.topMargin}px`,
    '--event-frame-badge-offset': `${mergedTokens.badgeOffset}px`
  } as React.CSSProperties;

  return (
    <div
      className={`event-frame-wrapper ${isAnimating ? 'event-frame-active' : ''} ${className || ''}`}
      style={wrapperStyle}
    >
      <div className="event-frame-content">
        {children}
      </div>

      {/* SVG border - positioned absolutely within wrapper */}
      <svg
        className="event-frame-svg"
        aria-hidden="true"
        preserveAspectRatio="none"
      >
        <rect
          className="event-frame-stroke"
          x={mergedTokens.strokeWidth / 2}
          y={mergedTokens.strokeWidth / 2}
          rx={mergedTokens.radius}
          ry={mergedTokens.radius}
          fill="none"
        />
      </svg>

      {/* Badge positioned at top center of frame */}
      {shouldShowBadge && (
        <div className="event-frame-badge-container">
          <div className="event-frame-badge-bg" />
          <div className="event-frame-badge">
            <span className="event-frame-badge-dot animate-pulse" />
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
