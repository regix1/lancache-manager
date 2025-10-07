import React, { useState, useRef, useEffect } from 'react';

interface TooltipProps {
  content: string;
  children: React.ReactNode;
  position?: 'top' | 'bottom' | 'left' | 'right';
  className?: string;
}

const Tooltip: React.FC<TooltipProps> = ({
  content,
  children,
  position = 'bottom',
  className = ''
}) => {
  const [isVisible, setIsVisible] = useState(false);
  const [tooltipPosition, setTooltipPosition] = useState({ top: 0, left: 0 });
  const tooltipRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLDivElement>(null);

  // Check if tooltips are disabled globally
  const tooltipsDisabled = document.documentElement.getAttribute('data-disable-tooltips') === 'true';

  useEffect(() => {
    if (isVisible && tooltipRef.current && triggerRef.current && !tooltipsDisabled) {
      const triggerRect = triggerRef.current.getBoundingClientRect();
      const tooltipRect = tooltipRef.current.getBoundingClientRect();

      let top = 0;
      let left = 0;

      switch (position) {
        case 'top':
          top = triggerRect.top - tooltipRect.height - 8;
          left = triggerRect.left + (triggerRect.width - tooltipRect.width) / 2;
          break;
        case 'bottom':
          top = triggerRect.bottom + 8;
          left = triggerRect.left + (triggerRect.width - tooltipRect.width) / 2;
          break;
        case 'left':
          top = triggerRect.top + (triggerRect.height - tooltipRect.height) / 2;
          left = triggerRect.left - tooltipRect.width - 8;
          break;
        case 'right':
          top = triggerRect.top + (triggerRect.height - tooltipRect.height) / 2;
          left = triggerRect.right + 8;
          break;
      }

      setTooltipPosition({ top, left });
    }
  }, [isVisible, position, tooltipsDisabled]);

  const handleMouseEnter = () => {
    if (!tooltipsDisabled) {
      setIsVisible(true);
    }
  };

  const handleMouseLeave = () => {
    setIsVisible(false);
  };

  return (
    <>
      <div
        ref={triggerRef}
        onMouseEnter={handleMouseEnter}
        onMouseLeave={handleMouseLeave}
        className={`relative inline-block ${className}`}
      >
        {children}
      </div>

      {isVisible && !tooltipsDisabled && (
        <div
          ref={tooltipRef}
          className="fixed z-50 px-2 py-1 text-xs font-medium rounded shadow-lg pointer-events-none transition-opacity duration-200"
          style={{
            top: tooltipPosition.top,
            left: tooltipPosition.left,
            backgroundColor: 'var(--theme-bg-primary)',
            color: 'var(--theme-text-primary)',
            border: '1px solid var(--theme-border-primary)',
            maxWidth: '200px'
          }}
        >
          {content}
        </div>
      )}
    </>
  );
};

export default Tooltip;