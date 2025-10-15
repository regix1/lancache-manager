import React, { useState, useRef, useEffect } from 'react';
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
}

const DEFAULT_OFFSET = 8;

export const Tooltip: React.FC<TooltipProps> = ({
  children,
  content,
  position = 'top',
  offset = DEFAULT_OFFSET,
  className,
  contentClassName = '',
  strategy = 'edge'
}) => {
  const [show, setShow] = useState(false);
  const [x, setX] = useState(0);
  const [y, setY] = useState(0);
  const triggerRef = useRef<HTMLDivElement>(null);

  // Check if tooltips are disabled globally
  const tooltipsDisabled = document.documentElement.getAttribute('data-disable-tooltips') === 'true';

  // Default children with conditional cursor style
  const defaultChildren = <Info className={`w-5 h-5 text-themed-muted p-1.5 -m-1.5 ${tooltipsDisabled ? '' : 'cursor-help'}`} />;
  const childContent = children ?? defaultChildren;

  return (
    <>
      <div
        ref={triggerRef}
        className={className || 'inline-flex'}
        onMouseEnter={(e) => {
          if (!tooltipsDisabled) {
            setShow(true);
            setX(e.clientX);
            setY(e.clientY);
          }
        }}
        onMouseMove={(e) => {
          if (!tooltipsDisabled && strategy === 'overlay') {
            setX(e.clientX);
            setY(e.clientY);
          }
        }}
        onMouseLeave={() => setShow(false)}
      >
        {childContent}
      </div>

      {show && !tooltipsDisabled && strategy === 'overlay' && createPortal(
        <div
          className={`fixed z-[9999] max-w-md px-2.5 py-1.5 text-xs themed-card text-themed-secondary rounded-md shadow-2xl pointer-events-none ${contentClassName}`}
          style={{
            left: x + 10,
            top: y + 10,
            backdropFilter: 'blur(8px)',
            border: '1px solid var(--theme-card-border)',
            boxShadow: '0 10px 40px rgba(0, 0, 0, 0.3)'
          }}
        >
          {content}
        </div>,
        document.body
      )}

      {show && !tooltipsDisabled && strategy === 'edge' && triggerRef.current && createPortal(
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
  const rect = trigger.getBoundingClientRect();
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ x: 0, y: 0 });

  useEffect(() => {
    if (!ref.current) return;

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
    x = Math.max(viewportPadding, Math.min(x, window.innerWidth - tooltipRect.width - viewportPadding));
    y = Math.max(viewportPadding, Math.min(y, window.innerHeight - tooltipRect.height - viewportPadding));

    setPos({ x, y });
  }, [rect, position, offset]);

  return (
    <div
      ref={ref}
      className={`fixed z-[9999] max-w-md px-2.5 py-1.5 text-xs themed-card text-themed-secondary rounded-md shadow-2xl pointer-events-none ${contentClassName}`}
      style={{
        left: pos.x,
        top: pos.y,
        backdropFilter: 'blur(8px)',
        border: '1px solid var(--theme-card-border)',
        boxShadow: '0 10px 40px rgba(0, 0, 0, 0.3)'
      }}
    >
      {content}
    </div>
  );
};

export const CacheInfoTooltip: React.FC = () => {
  const tooltipsDisabled = document.documentElement.getAttribute('data-disable-tooltips') === 'true';

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

export const CachePerformanceTooltip: React.FC = () => (
  <Tooltip
    content="Higher cache hit rates mean better performance and bandwidth savings"
    className="inline-flex p-1"
  />
);

export const TimestampTooltip: React.FC<{
  startTime: string;
  endTime: string | null;
  isActive: boolean;
  children: React.ReactNode;
}> = ({ startTime, endTime, isActive, children }) => (
  <Tooltip
    content={
      <div className="space-y-1">
        <div>Started: {startTime}</div>
        {endTime && <div>Ended: {endTime}</div>}
        <div>Status: {isActive ? 'Active' : 'Completed'}</div>
      </div>
    }
  >
    {children}
  </Tooltip>
);
