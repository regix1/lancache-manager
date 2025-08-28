import React, { useState, useRef, memo } from 'react';
import ReactDOM from 'react-dom';
import { Info } from 'lucide-react';

// Base tooltip component that handles positioning and portal rendering
const BaseTooltip = memo(({ children, content, width = 'w-72' }) => {
  const [isVisible, setIsVisible] = useState(false);
  const [position, setPosition] = useState({ top: 0, left: 0 });
  const triggerRef = useRef(null);

  const handleMouseEnter = () => {
    if (triggerRef.current) {
      const rect = triggerRef.current.getBoundingClientRect();
      const tooltipWidth = width === 'w-80' ? 320 : 288; // Approximate widths
      
      // Position tooltip to the top-right of the icon, close to it
      let top = rect.top - 10; // Start near the top of the icon
      let left = rect.right + 10; // Start 10px to the right of the icon
      
      // Check if tooltip would go off the right edge of screen
      if (left + tooltipWidth > window.innerWidth - 10) {
        // Position to the left of the icon instead
        left = rect.left - tooltipWidth - 10;
      }
      
      // Check if tooltip would go off the top of screen
      if (top < 10) {
        top = rect.bottom + 10; // Position below the icon
      }
      
      // Ensure tooltip doesn't go off the left edge
      if (left < 10) {
        left = 10;
      }
      
      setPosition({ top, left });
      setIsVisible(true);
    }
  };

  const handleMouseLeave = () => {
    setIsVisible(false);
  };

  return (
    <>
      <span 
        ref={triggerRef}
        className="relative inline-flex items-center"
        onMouseEnter={handleMouseEnter}
        onMouseLeave={handleMouseLeave}
      >
        {children || <Info className="w-4 h-4 text-gray-400 cursor-help" />}
      </span>
      {isVisible && ReactDOM.createPortal(
        <div 
          className={`fixed px-3 py-2 text-xs text-white bg-gray-900 rounded-lg ${width} border border-gray-700 shadow-lg pointer-events-none`}
          style={{ 
            top: `${position.top}px`,
            left: `${position.left}px`,
            zIndex: 99999
          }}
        >
          {content}
        </div>,
        document.body
      )}
    </>
  );
});

// Cache performance tooltip for downloads page
export const CachePerformanceTooltip = memo(() => (
  <BaseTooltip
    width="w-80"
    content={
      <div className="space-y-2">
        <div className="font-semibold mb-2">Cache Performance:</div>
        <div><strong className="text-green-400">Green (75-100%):</strong> Excellent - Most data from cache</div>
        <div><strong className="text-blue-400">Blue (50-75%):</strong> Good - Moderate cache usage</div>
        <div><strong className="text-yellow-400">Yellow (25-50%):</strong> Fair - Some cache usage</div>
        <div><strong className="text-orange-400">Orange (0-25%):</strong> Low - Mostly new downloads</div>
        <div className="mt-2 pt-2 border-t border-gray-700">
          <strong>Cache Hits:</strong> Data served locally (fast)<br/>
          <strong>Cache Misses:</strong> Downloaded from internet (normal for first time)
        </div>
      </div>
    }
  />
));

// Simple cache tooltip for clients and dashboard
export const CacheInfoTooltip = memo(() => (
  <BaseTooltip
    width="w-72"
    content={
      <div className="space-y-2">
        <div>
          <strong className="text-green-400">Cache Hits:</strong> Data served from local cache - fast and saves bandwidth
        </div>
        <div>
          <strong className="text-yellow-400">Cache Misses:</strong> Data downloaded from internet - normal for first-time downloads
        </div>
      </div>
    }
  />
));

// Generic tooltip for custom content
export const Tooltip = memo(({ children, content, width = 'w-72' }) => (
  <BaseTooltip width={width} content={content}>
    {children}
  </BaseTooltip>
));

export default Tooltip;