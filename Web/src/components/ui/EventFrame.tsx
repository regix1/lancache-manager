import React, { useCallback, useEffect, useRef, useState } from 'react';
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

const DEFAULT_DASH_LENGTH = 11;
const DEFAULT_DASH_GAP = 6;

const defaultTokens: Required<EventFrameTokens> = {
  offset: 10,
  radius: 12,
  strokeWidth: 2,
  dashArray: `${DEFAULT_DASH_LENGTH} ${DEFAULT_DASH_GAP}`, // 11px dash, 6px gap
  topMargin: 16,
  badgeOffset: 12
};

const parseDashArray = (dashArray: string, fallbackDash: number, fallbackGap: number) => {
  if (!dashArray) {
    return {
      dashLength: fallbackDash,
      gapLength: fallbackGap,
      dashTotal: fallbackDash + fallbackGap
    };
  }

  const parts = dashArray.split(/[\s,]+/).filter(Boolean);
  const dashLength = Number.parseFloat(parts[0] ?? '');
  const gapLength = Number.parseFloat(parts[1] ?? '');

  const resolvedDash = Number.isFinite(dashLength) ? dashLength : fallbackDash;
  const resolvedGap = Number.isFinite(gapLength) ? gapLength : fallbackGap;

  return {
    dashLength: resolvedDash,
    gapLength: resolvedGap,
    dashTotal: resolvedDash + resolvedGap
  };
};

/**
 * Renders a non-intrusive animated frame around content.
 * Uses an inline SVG border sized via ResizeObserver for precise dashes.
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
  const dashValues = parseDashArray(
    mergedTokens.dashArray,
    DEFAULT_DASH_LENGTH,
    DEFAULT_DASH_GAP
  );
  const wrapperRef = useRef<HTMLDivElement>(null);
  const [frameSize, setFrameSize] = useState({ width: 0, height: 0 });

  const updateFrameSize = useCallback(() => {
    const element = wrapperRef.current;
    if (!element) return;

    const rect = element.getBoundingClientRect();
    const nextWidth = Math.max(0, Math.round(rect.width + (mergedTokens.offset * 2)));
    const nextHeight = Math.max(0, Math.round(rect.height + (mergedTokens.offset * 2)));

    setFrameSize((prev) => (
      prev.width === nextWidth && prev.height === nextHeight
        ? prev
        : { width: nextWidth, height: nextHeight }
    ));
  }, [mergedTokens.offset]);

  useEffect(() => {
    if (!enabled) {
      setIsAnimating(false);
      return;
    }

    setIsAnimating(true);
  }, [enabled]);

  useEffect(() => {
    if (!enabled) return;

    updateFrameSize();

    const element = wrapperRef.current;
    if (!element) return;

    let resizeObserver: ResizeObserver | null = null;
    if (typeof ResizeObserver !== 'undefined') {
      resizeObserver = new ResizeObserver(() => {
        updateFrameSize();
      });
      resizeObserver.observe(element);
    }

    window.addEventListener('resize', updateFrameSize);

    return () => {
      window.removeEventListener('resize', updateFrameSize);
      if (resizeObserver) resizeObserver.disconnect();
    };
  }, [enabled, updateFrameSize]);

  if (!enabled) {
    return <>{children}</>;
  }

  const shouldShowBadge = showBadge && Boolean(label);
  const resolvedBadgeText = badgeText ?? t('eventFrame.badge');
  const hasFrameSize = frameSize.width > 0 && frameSize.height > 0;
  const strokeWidth = mergedTokens.strokeWidth;
  const halfStroke = strokeWidth / 2;
  const rectWidth = Math.max(0, frameSize.width - strokeWidth);
  const rectHeight = Math.max(0, frameSize.height - strokeWidth);
  const outerRadius = mergedTokens.radius + mergedTokens.offset;
  const rectRadius = Math.max(
    0,
    Math.min(outerRadius - halfStroke, rectWidth / 2, rectHeight / 2)
  );

  // Precompute values for CSS - avoids calc() multiplication which iOS Safari doesn't support
  const wrapperStyle = {
    '--event-frame-color': color,
    '--event-frame-offset': `${mergedTokens.offset}px`,
    '--event-frame-offset-negative': `-${mergedTokens.offset}px`,
    '--event-frame-border-radius': `${mergedTokens.radius + mergedTokens.offset}px`,
    '--event-frame-stroke-width': `${mergedTokens.strokeWidth}px`,
    '--event-frame-dash-length': `${dashValues.dashLength}px`,
    '--event-frame-dash-gap': `${dashValues.gapLength}px`,
    '--event-frame-dash-total': `${dashValues.dashTotal}px`,
    '--event-frame-top-margin': `${mergedTokens.topMargin}px`,
    '--event-frame-badge-offset': `${mergedTokens.badgeOffset}px`
  } as React.CSSProperties;

  return (
    <div
      className={`event-frame-wrapper ${isAnimating ? 'event-frame-active' : ''} ${className || ''}`}
      style={wrapperStyle}
      ref={wrapperRef}
    >
      {isAnimating && hasFrameSize && (
        <svg
          className="event-frame-border"
          width={frameSize.width}
          height={frameSize.height}
          viewBox={`0 0 ${frameSize.width} ${frameSize.height}`}
          aria-hidden="true"
          focusable="false"
        >
          <rect
            className="event-frame-border-rect"
            x={halfStroke}
            y={halfStroke}
            width={rectWidth}
            height={rectHeight}
            rx={rectRadius}
            ry={rectRadius}
          />
        </svg>
      )}
      <div className="event-frame-content">
        {children}
      </div>

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
