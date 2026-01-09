import React, { useRef, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useEvents } from '@contexts/EventContext';
import { getEventColorVar } from '@utils/eventColors';

interface ActiveEventBorderProps {
  children: React.ReactNode;
  enabled?: boolean;
}

/**
 * Wraps content with an animated dashed border when an event is active.
 * 
 * CRITICAL: This component must NOT affect the layout of its children.
 * 
 * Implementation uses a fixed-position portal overlay:
 * - Measures the content's bounding rect
 * - Renders the border frame as a fixed overlay
 * - Updates position on scroll/resize
 * - Zero layout impact on children
 */
const ActiveEventBorder: React.FC<ActiveEventBorderProps> = ({ children, enabled = true }) => {
  const { activeEvents } = useEvents();
  const contentRef = useRef<HTMLDivElement>(null);
  const [rect, setRect] = useState<DOMRect | null>(null);

  // Get the first active event (if any)
  const activeEvent = enabled && activeEvents.length > 0 ? activeEvents[0] : null;

  // Update border position when content size/position changes
  useEffect(() => {
    if (!activeEvent || !contentRef.current) {
      setRect(null);
      return;
    }

    const updateRect = () => {
      if (contentRef.current) {
        setRect(contentRef.current.getBoundingClientRect());
      }
    };

    // Initial measurement
    updateRect();

    // Update on scroll and resize
    window.addEventListener('scroll', updateRect, true);
    window.addEventListener('resize', updateRect);

    // Use ResizeObserver to detect content size changes
    const resizeObserver = new ResizeObserver(updateRect);
    resizeObserver.observe(contentRef.current);

    return () => {
      window.removeEventListener('scroll', updateRect, true);
      window.removeEventListener('resize', updateRect);
      resizeObserver.disconnect();
    };
  }, [activeEvent]);

  // When no active event, render children directly without any wrapper
  if (!activeEvent) {
    return <>{children}</>;
  }

  const eventColor = getEventColorVar(activeEvent.colorIndex);
  const borderOffset = 10; // How far the border extends beyond content

  return (
    <>
      {/* Content wrapper - just for measurement, no style impact */}
      <div ref={contentRef}>
        {children}
      </div>

      {/* Border overlay rendered via portal - completely outside normal flow */}
      {rect && createPortal(
        <div
          className="active-event-border-overlay"
          style={{
            position: 'fixed',
            top: rect.top - borderOffset,
            left: rect.left - borderOffset,
            width: rect.width + borderOffset * 2,
            height: rect.height + borderOffset * 2,
            border: `2px dashed ${eventColor}`,
            borderRadius: '0.75rem',
            pointerEvents: 'none',
            zIndex: 50,
            animation: 'eventBorderBreathing 3s ease-in-out infinite'
          }}
        >
          {/* Event badge - positioned at TOP-CENTER */}
          <div
            style={{
              position: 'absolute',
              top: '-12px',
              left: '50%',
              transform: 'translateX(-50%)',
              pointerEvents: 'none',
              whiteSpace: 'nowrap'
            }}
          >
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
              <span>{activeEvent.name}</span>
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
