import React, { useState, useRef, useEffect, useLayoutEffect } from 'react';
import { createPortal } from 'react-dom';
import { Info } from 'lucide-react';

type TooltipPosition = 'top' | 'bottom' | 'left' | 'right';
type TooltipStrategy = 'edge' | 'overlay';

interface TooltipProps {
  children?: React.ReactNode;
  content: React.ReactNode;
  position?: TooltipPosition;
  offset?: number;
  className?: string;
  contentClassName?: string;
  strategy?: TooltipStrategy;
  style?: React.CSSProperties;
}

const DEFAULT_OFFSET = 8;

export const Tooltip: React.FC<TooltipProps> = ({
  children,
  content,
  position = 'top',
  offset = DEFAULT_OFFSET,
  className,
  contentClassName = '',
  strategy = 'edge',
  style
}) => {
  const [show, setShow] = useState(false);
  const [x, setX] = useState(0);
  const [y, setY] = useState(0);
  const [isMobile, setIsMobile] = useState(false);
  const triggerRef = useRef<HTMLDivElement>(null);
  const showTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const hideTimeoutRef = useRef<NodeJS.Timeout | null>(null);

  // Check if tooltips are disabled globally
  const globallyDisabled =
    document.documentElement.getAttribute('data-disable-tooltips') === 'true';

  // Detect mobile viewport - use touch behavior instead of hover
  useEffect(() => {
    const checkMobile = () => {
      const mobile = window.innerWidth < 768;
      setIsMobile(prev => prev === mobile ? prev : mobile);
    };
    checkMobile();
    window.addEventListener('resize', checkMobile);
    return () => window.removeEventListener('resize', checkMobile);
  }, []);

  // Only disable if globally disabled (not on mobile - we use touch there)
  const tooltipsDisabled = globallyDisabled;

  // Add scroll listener and click-outside handler to hide tooltip
  useEffect(() => {
    if (!show) return;

    const handleScroll = () => {
      setShow(false);
    };

    // Close tooltip when clicking outside (for mobile tap-to-toggle)
    const handleClickOutside = (e: MouseEvent | TouchEvent) => {
      if (triggerRef.current && !triggerRef.current.contains(e.target as Node)) {
        setShow(false);
      }
    };

    // Listen for scroll events on window and any scrollable parents
    window.addEventListener('scroll', handleScroll, { passive: true, capture: true });
    window.addEventListener('touchmove', handleScroll, { passive: true, capture: true });

    // Add click-outside listener with a small delay to avoid immediate close
    const timeoutId = setTimeout(() => {
      document.addEventListener('click', handleClickOutside, { capture: true });
      document.addEventListener('touchstart', handleClickOutside, { capture: true });
    }, 10);

    return () => {
      clearTimeout(timeoutId);
      window.removeEventListener('scroll', handleScroll, { capture: true });
      window.removeEventListener('touchmove', handleScroll, { capture: true });
      document.removeEventListener('click', handleClickOutside, { capture: true });
      document.removeEventListener('touchstart', handleClickOutside, { capture: true });
    };
  }, [show]);

  // Default children with conditional cursor style
  const defaultChildren = (
    <Info
      className={`w-5 h-5 text-themed-muted p-1.5 -m-1.5 ${tooltipsDisabled ? '' : 'cursor-help'}`}
    />
  );
  const childContent = children ?? defaultChildren;

  // Cleanup timeouts on unmount
  useEffect(() => {
    return () => {
      if (showTimeoutRef.current) clearTimeout(showTimeoutRef.current);
      if (hideTimeoutRef.current) clearTimeout(hideTimeoutRef.current);
    };
  }, []);

  const handleMouseEnter = (e: React.MouseEvent) => {
    if (tooltipsDisabled) return;

    // Clear any pending hide
    if (hideTimeoutRef.current) {
      clearTimeout(hideTimeoutRef.current);
      hideTimeoutRef.current = null;
    }

    setX(e.clientX);
    setY(e.clientY);

    // Small delay before showing (150ms)
    showTimeoutRef.current = setTimeout(() => {
      setShow(true);
    }, 150);
  };

  const handleMouseMove = (e: React.MouseEvent) => {
    if (!tooltipsDisabled && strategy === 'overlay') {
      setX(e.clientX);
      setY(e.clientY);
    }
  };

  const handleMouseLeave = () => {
    // Clear any pending show
    if (showTimeoutRef.current) {
      clearTimeout(showTimeoutRef.current);
      showTimeoutRef.current = null;
    }

    // Hide immediately
    setShow(false);
  };

  return (
    <>
      <div
        ref={triggerRef}
        className={className || 'inline-flex'}
        style={style}
        onMouseEnter={handleMouseEnter}
        onMouseMove={handleMouseMove}
        onMouseLeave={handleMouseLeave}
        onClick={(e) => {
          // On mobile, toggle tooltip on tap
          if (isMobile && !tooltipsDisabled) {
            e.preventDefault();
            e.stopPropagation();
            if (!show) {
              const rect = triggerRef.current?.getBoundingClientRect();
              if (rect) {
                setX(rect.left + rect.width / 2);
                setY(rect.top);
              }
            }
            setShow(!show);
          }
        }}
      >
        {childContent}
      </div>

      {show &&
        !tooltipsDisabled &&
        strategy === 'overlay' &&
        createPortal(
          <div
            style={{
              position: 'fixed',
              zIndex: 99999,
              left: x + 10,
              top: y + 10,
              maxWidth: '320px',
              padding: '6px 10px',
              fontSize: '12px',
              borderRadius: '6px',
              pointerEvents: 'none',
              backgroundColor: 'var(--theme-bg-tertiary)',
              color: 'var(--theme-text-primary)',
              border: '1px solid var(--theme-card-border)',
              boxShadow: '0 4px 12px rgba(0, 0, 0, 0.5)'
            }}
          >
            {content}
          </div>,
          document.body
        )}

      {show &&
        !tooltipsDisabled &&
        strategy === 'edge' &&
        triggerRef.current &&
        createPortal(
          <EdgeTooltip
            trigger={triggerRef.current}
            content={content}
            position={position}
            offset={offset}
            contentClassName={contentClassName}
          />,
          document.body
        )}
    </>
  );
};

// Edge-positioned tooltips for info icons
const EdgeTooltip: React.FC<{
  trigger: HTMLElement;
  content: React.ReactNode;
  position: TooltipPosition;
  offset: number;
  contentClassName: string;
}> = ({ trigger, content, position, offset, contentClassName }) => {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null);

  // Use useLayoutEffect to calculate position before browser paint
  useLayoutEffect(() => {
    if (!ref.current) return;

    const rect = trigger.getBoundingClientRect();
    const tooltipRect = ref.current.getBoundingClientRect();
    const viewportPadding = 12;
    let x = 0;
    let y = 0;

    // Calculate initial position
    switch (position) {
      case 'top':
        x = rect.left + rect.width / 2 - tooltipRect.width / 2;
        y = rect.top - tooltipRect.height - offset;
        // Flip to bottom if would go off top
        if (y < viewportPadding) {
          y = rect.bottom + offset;
        }
        break;
      case 'bottom':
        x = rect.left + rect.width / 2 - tooltipRect.width / 2;
        y = rect.bottom + offset;
        // Flip to top if would go off bottom
        if (y + tooltipRect.height > window.innerHeight - viewportPadding) {
          y = rect.top - tooltipRect.height - offset;
        }
        break;
      case 'left':
        x = rect.left - tooltipRect.width - offset;
        y = rect.top + rect.height / 2 - tooltipRect.height / 2;
        // Flip to right if would go off left
        if (x < viewportPadding) {
          x = rect.right + offset;
        }
        break;
      case 'right':
        x = rect.right + offset;
        y = rect.top + rect.height / 2 - tooltipRect.height / 2;
        // Flip to left if would go off right
        if (x + tooltipRect.width > window.innerWidth - viewportPadding) {
          x = rect.left - tooltipRect.width - offset;
        }
        break;
    }

    // Clamp to viewport bounds
    x = Math.max(
      viewportPadding,
      Math.min(x, window.innerWidth - tooltipRect.width - viewportPadding)
    );
    y = Math.max(
      viewportPadding,
      Math.min(y, window.innerHeight - tooltipRect.height - viewportPadding)
    );

    // Only update state if position actually changed to prevent infinite loops
    setPos(prev => (prev?.x === x && prev?.y === y) ? prev : { x, y });
  }, [trigger, position, offset]);

  return (
    <div
      ref={ref}
      className={`fixed z-[9999] max-w-md px-2.5 py-1.5 text-xs themed-card text-themed-secondary rounded-md shadow-2xl pointer-events-none ${contentClassName}`}
      style={{
        left: pos?.x ?? -9999,
        top: pos?.y ?? -9999,
        visibility: pos ? 'visible' : 'hidden',
        transition: 'none',
        backdropFilter: 'blur(8px)',
        border: '1px solid var(--theme-card-border)',
        boxShadow: '0 10px 40px rgba(0, 0, 0, 0.4)'
      }}
    >
      {content}
    </div>
  );
};

export const CacheInfoTooltip: React.FC = () => {
  const [isMobile, setIsMobile] = useState(false);
  const globallyDisabled =
    document.documentElement.getAttribute('data-disable-tooltips') === 'true';

  useEffect(() => {
    const checkMobile = () => {
      const mobile = window.innerWidth < 768;
      setIsMobile(prev => prev === mobile ? prev : mobile);
    };
    checkMobile();
    window.addEventListener('resize', checkMobile);
    return () => window.removeEventListener('resize', checkMobile);
  }, []);

  const tooltipsDisabled = globallyDisabled || isMobile;

  return (
    <Tooltip
      content={
        <div className="whitespace-nowrap">
          <span className="cache-hit font-medium">Cache Hits:</span>
          <span className="text-themed-secondary"> Data served from local cache</span>
          <span className="text-themed-muted mx-2">|</span>
          <span className="cache-miss font-medium">Cache Misses:</span>
          <span className="text-themed-secondary"> Data downloaded from internet</span>
        </div>
      }
      contentClassName="!max-w-none"
    >
      <Info className={`w-5 h-5 text-themed-muted ${tooltipsDisabled ? '' : 'cursor-help'}`} />
    </Tooltip>
  );
};
