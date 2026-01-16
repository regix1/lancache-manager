import React, { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { createPortal } from 'react-dom';

// Document-relative rect (top/left include current scroll offset).
interface ContentRect {
  top: number;
  left: number;
  width: number;
  height: number;
}

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
  trackScroll?: boolean;
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
 * Uses a portal overlay to avoid affecting layout.
 */
const EventFrame: React.FC<EventFrameProps> = ({
  children,
  enabled = true,
  color = 'var(--theme-primary)',
  label,
  badgeText,
  showBadge = true,
  tokens,
  trackScroll = false,
  className
}) => {
  const { t } = useTranslation();
  const contentRef = useRef<HTMLDivElement>(null);
  const [rect, setRect] = useState<ContentRect | null>(null);
  const rafIdRef = useRef<number | null>(null);
  const trackingRafIdRef = useRef<number | null>(null);
  const lastRectRef = useRef<ContentRect | null>(null);
  const mergedTokens = { ...defaultTokens, ...tokens };

  // Update frame position when content size/position changes
  useEffect(() => {
    if (!enabled || !contentRef.current) {
      setRect(null);
      lastRectRef.current = null;
      return;
    }

    const measure = () => {
      if (!contentRef.current) return false;

      const domRect = contentRef.current.getBoundingClientRect();
      const nextRect: ContentRect = {
        top: domRect.top + window.scrollY,
        left: domRect.left + window.scrollX,
        width: domRect.width,
        height: domRect.height
      };

      const prevRect = lastRectRef.current;
      const epsilon = 0.5; // Avoid excessive updates from sub-pixel changes
      const changed = !prevRect ||
        Math.abs(prevRect.top - nextRect.top) > epsilon ||
        Math.abs(prevRect.left - nextRect.left) > epsilon ||
        Math.abs(prevRect.width - nextRect.width) > epsilon ||
        Math.abs(prevRect.height - nextRect.height) > epsilon;

      if (changed) {
        lastRectRef.current = nextRect;
        setRect(nextRect);
      }

      return changed;
    };

    const scheduleMeasure = () => {
      if (rafIdRef.current !== null) return;
      rafIdRef.current = requestAnimationFrame(() => {
        rafIdRef.current = null;
        measure();
      });
    };

    // Track measurements for short periods to follow layout transitions (e.g., mobile nav expanding).
    const startTracking = (maxDurationMs: number = 500) => {
      if (trackingRafIdRef.current !== null) return;

      const startAt = performance.now();
      let stableFrames = 0;

      const tick = () => {
        const didChange = measure() === true;
        stableFrames = didChange ? 0 : stableFrames + 1;

        const elapsed = performance.now() - startAt;
        const shouldContinue = elapsed < maxDurationMs && stableFrames < 8;

        if (shouldContinue) {
          trackingRafIdRef.current = requestAnimationFrame(tick);
        } else {
          trackingRafIdRef.current = null;
        }
      };

      trackingRafIdRef.current = requestAnimationFrame(tick);
    };

    // Initial measurement (after layout settles)
    scheduleMeasure();

    // Update on resize
    window.addEventListener('resize', scheduleMeasure);
    window.addEventListener('orientationchange', scheduleMeasure);

    // Mobile browsers can change layout viewport metrics without a classic resize
    window.visualViewport?.addEventListener('resize', scheduleMeasure);

    const scrollOptions: AddEventListenerOptions = { passive: true, capture: true };
    const viewportScrollOptions: AddEventListenerOptions = { passive: true };
    if (trackScroll) {
      // Optional: listen for scroll events when a nested scroll container is used
      window.addEventListener('scroll', scheduleMeasure, scrollOptions);
      window.visualViewport?.addEventListener('scroll', scheduleMeasure, viewportScrollOptions);
    }

    // Use ResizeObserver to detect content size changes
    const resizeObserver = new ResizeObserver(scheduleMeasure);
    resizeObserver.observe(contentRef.current);

    // Watch for layout changes above the content (e.g., mobile nav menu expanding and pushing the page down).
    const mainElement = contentRef.current.closest('main');
    const layoutContainer = mainElement?.parentElement;
    const layoutMutationObserver = layoutContainer ? new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        const targetNode = mutation.target;
        if (mainElement && targetNode instanceof Node && mainElement.contains(targetNode)) continue;
        scheduleMeasure();
        startTracking();
        break;
      }
    }) : null;

    layoutMutationObserver?.observe(layoutContainer as Element, {
      attributes: true,
      attributeFilter: ['class', 'style'],
      childList: true,
      subtree: true
    });

    // ResizeObserver for elements above the main content (header/nav/notification bar).
    // This reliably fires during height animations (mobile nav expand) even when style mutations don't.
    const layoutResizeObserver = layoutContainer ? new ResizeObserver(() => {
      scheduleMeasure();
      startTracking();
    }) : null;

    if (layoutResizeObserver && layoutContainer && mainElement) {
      Array.from(layoutContainer.children)
        .filter((child) => child !== mainElement)
        .forEach((child) => layoutResizeObserver.observe(child));
    }

    return () => {
      window.removeEventListener('resize', scheduleMeasure);
      window.removeEventListener('orientationchange', scheduleMeasure);

      window.visualViewport?.removeEventListener('resize', scheduleMeasure);
      if (trackScroll) {
        window.removeEventListener('scroll', scheduleMeasure, scrollOptions);
        window.visualViewport?.removeEventListener('scroll', scheduleMeasure, viewportScrollOptions);
      }

      resizeObserver.disconnect();
      layoutMutationObserver?.disconnect();
      layoutResizeObserver?.disconnect();
      if (rafIdRef.current !== null) {
        cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = null;
      }
      if (trackingRafIdRef.current !== null) {
        cancelAnimationFrame(trackingRafIdRef.current);
        trackingRafIdRef.current = null;
      }
    };
  }, [enabled, trackScroll]);

  if (!enabled) {
    return <>{children}</>;
  }

  const shouldShowBadge = showBadge && Boolean(label);
  const resolvedBadgeText = badgeText ?? t('eventFrame.badge');
  const contentClassName = `event-frame-content${className ? ` ${className}` : ''}`;
  const contentStyle = {
    '--event-frame-top-margin': `${mergedTokens.topMargin}px`
  } as React.CSSProperties;

  if (!rect) {
    return (
      <div ref={contentRef} className={contentClassName} style={contentStyle}>
        {children}
      </div>
    );
  }

  const frameWidth = rect.width + mergedTokens.offset * 2;
  const frameHeight = rect.height + mergedTokens.offset * 2;
  const overlayStyle = {
    '--event-frame-color': color,
    '--event-frame-top': `${rect.top - mergedTokens.offset}px`,
    '--event-frame-left': `${rect.left - mergedTokens.offset}px`,
    '--event-frame-width': `${frameWidth}px`,
    '--event-frame-height': `${frameHeight}px`,
    '--event-frame-radius': `${mergedTokens.radius}px`,
    '--event-frame-stroke-width': `${mergedTokens.strokeWidth}px`,
    '--event-frame-dash-array': mergedTokens.dashArray,
    '--event-frame-badge-offset': `${mergedTokens.badgeOffset}px`
  } as React.CSSProperties;

  return (
    <>
      <div ref={contentRef} className={contentClassName} style={contentStyle}>
        {children}
      </div>

      {createPortal(
        <div className="event-frame-overlay" style={overlayStyle} aria-hidden="true">
          <svg
            className="event-frame-svg"
            width="100%"
            height="100%"
            viewBox={`0 0 ${frameWidth} ${frameHeight}`}
            preserveAspectRatio="none"
            aria-hidden="true"
          >
            <rect
              className="event-frame-stroke"
              x={1}
              y={1}
              width={frameWidth - 2}
              height={frameHeight - 2}
              rx={mergedTokens.radius}
              ry={mergedTokens.radius}
              fill="none"
            />
          </svg>
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
        </div>,
        document.body
      )}
    </>
  );
};

export default EventFrame;
