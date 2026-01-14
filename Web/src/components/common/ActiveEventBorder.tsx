import React, { useRef, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useEvents } from '@contexts/EventContext';
import { getEventColorVar } from '@utils/eventColors';

interface ActiveEventBorderProps {
  children: React.ReactNode;
  enabled?: boolean;
}

// Document-relative rect (top/left include current scroll offset).
interface ContentRect {
  top: number;
  left: number;
  width: number;
  height: number;
}

/**
 * Wraps content with an animated dashed border when an event is active.
 * 
 * CRITICAL: This component must NOT affect the layout of its children.
 * 
 * Implementation uses a portal overlay:
 * - Measures the content's bounding rect
 * - Renders the border frame as an overlay outside normal flow
 * - Updates position on scroll/resize/layout changes
 * - Zero layout impact on children
 */
const ActiveEventBorder: React.FC<ActiveEventBorderProps> = ({ children, enabled = true }) => {
  const { activeEvents } = useEvents();
  const contentRef = useRef<HTMLDivElement>(null);
  const [rect, setRect] = useState<ContentRect | null>(null);
  const rafIdRef = useRef<number | null>(null);
  const trackingRafIdRef = useRef<number | null>(null);
  const lastRectRef = useRef<ContentRect | null>(null);

  // Get the first active event (if any)
  const activeEvent = enabled && activeEvents.length > 0 ? activeEvents[0] : null;

  // Update border position when content size/position changes
  useEffect(() => {
    if (!activeEvent || !contentRef.current) {
      setRect(null);
      lastRectRef.current = null;
      return;
    }

    const measure = () => {
      if (!contentRef.current) return;

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
    // Also listen for scroll events (capture) so the border stays aligned even if a nested scroll container is used.
    window.addEventListener('scroll', scheduleMeasure, true);
    window.visualViewport?.addEventListener('scroll', scheduleMeasure);

    // Use ResizeObserver to detect content size changes
    const resizeObserver = new ResizeObserver(scheduleMeasure);
    resizeObserver.observe(contentRef.current);

    // Use MutationObserver to detect content changes (e.g., tab switches)
    // This catches when children are replaced even if size doesn't change
    const mutationObserver = new MutationObserver(() => {
      scheduleMeasure();
    });
    mutationObserver.observe(contentRef.current, {
      childList: true,
      subtree: true
    });

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
      window.removeEventListener('scroll', scheduleMeasure, true);
      window.visualViewport?.removeEventListener('scroll', scheduleMeasure);

      resizeObserver.disconnect();
      mutationObserver.disconnect();
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
  }, [activeEvent]);

  // When no active event, render children directly without any wrapper
  if (!activeEvent) {
    return <>{children}</>;
  }

  const eventColor = getEventColorVar(activeEvent.colorIndex);
  const borderOffset = 10; // How far the border extends beyond content
  const topMargin = 16; // Space between nav and the active event frame

  return (
    <>
      {/* Content wrapper - just for measurement, no style impact */}
      <div ref={contentRef} style={{ marginTop: `${topMargin}px` }}>
        {children}
      </div>

      {/* Border overlay rendered via portal - completely outside normal flow */}
      {rect && createPortal(
        <div
          className="active-event-border-overlay"
          style={{
            position: 'absolute',
            top: rect.top - borderOffset,
            left: rect.left - borderOffset,
            width: rect.width + borderOffset * 2,
            height: rect.height + borderOffset * 2,
            borderRadius: '0.75rem',
            pointerEvents: 'none',
            zIndex: 40,
            animation: 'eventBorderBreathing 3s ease-in-out infinite'
          }}
        >
          <svg
            className="active-event-border-svg"
            width="100%"
            height="100%"
            viewBox={`0 0 ${rect.width + borderOffset * 2} ${rect.height + borderOffset * 2}`}
            preserveAspectRatio="none"
            aria-hidden="true"
          >
            <rect
              className="active-event-border-stroke"
              x={1}
              y={1}
              width={rect.width + borderOffset * 2 - 2}
              height={rect.height + borderOffset * 2 - 2}
              rx={12}
              ry={12}
              fill="none"
              stroke={eventColor}
              strokeWidth={2}
              strokeDasharray="10 6"
            />
          </svg>
          {/* Event badge - positioned at TOP-CENTER */}
          <div className="active-event-badge-container">

            {/* Solid background layer - masks the border behind the badge */}
            <div
              style={{
                position: 'absolute',
                inset: '-4px',
                borderRadius: '9999px',
                backgroundColor: 'var(--theme-bg-primary)'
              }}
            />
            {/* Animated border + content layer */}
            <div
              className="active-event-badge"
              style={{
                position: 'relative',
                display: 'flex',
                alignItems: 'center',
                gap: '0.375rem',
                padding: '0.375rem 1rem',
                borderRadius: '9999px',
                fontSize: '0.75rem',
                fontWeight: 600,
                color: eventColor,
                borderWidth: '2px',
                borderStyle: 'dashed',
                borderColor: eventColor,
                animation: 'eventBorderBreathing 3s ease-in-out infinite'
              }}
              >
                {/* Pulsing dot */}
                <span
                  className="animate-pulse"
                style={{
                  width: '0.5rem',
                  height: '0.5rem',
                  borderRadius: '9999px',
                  backgroundColor: eventColor
                }}
              />
              <span className="active-event-badge-name">{activeEvent.name}</span>
              <span style={{ fontSize: '0.625rem', opacity: 0.7 }}>LIVE</span>
            </div>
          </div>
        </div>,
        document.body
      )}
    </>
  );
};

export default ActiveEventBorder;
