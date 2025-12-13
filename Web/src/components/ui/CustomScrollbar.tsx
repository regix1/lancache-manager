import React, { useRef, useState, useEffect } from 'react';

interface CustomScrollbarProps {
  children: React.ReactNode;
  maxHeight?: string;
  className?: string;
  /** Right padding for scrollbar space. Use 'compact' for smaller dropdowns. Default is 12px. */
  paddingMode?: 'default' | 'compact' | 'none';
}

export const CustomScrollbar: React.FC<CustomScrollbarProps> = ({
  children,
  maxHeight = '32rem',
  className = '',
  paddingMode = 'default',
}) => {
  const paddingRight = paddingMode === 'none' ? '0px' : paddingMode === 'compact' ? '6px' : '12px';
  const contentRef = useRef<HTMLDivElement>(null);
  const scrollTrackRef = useRef<HTMLDivElement>(null);
  const scrollThumbRef = useRef<HTMLDivElement>(null);
  const [thumbHeight, setThumbHeight] = useState(0);
  const [thumbTop, setThumbTop] = useState(0);
  const [isDragging, setIsDragging] = useState(false);
  const [showScrollbar, setShowScrollbar] = useState(false);
  const dragStartY = useRef(0);
  const dragStartScrollTop = useRef(0);

  // Calculate thumb size and position
  const updateScrollbar = () => {
    if (!contentRef.current || !scrollTrackRef.current) return;

    const { scrollHeight, clientHeight, scrollTop } = contentRef.current;
    const trackHeight = scrollTrackRef.current.clientHeight;
    const padding = 4; // Top and bottom padding in pixels

    // Check if content is scrollable
    if (scrollHeight <= clientHeight) {
      setShowScrollbar(false);
      return;
    }

    setShowScrollbar(true);

    // Calculate thumb height (proportional to visible content, accounting for padding)
    const availableTrackHeight = trackHeight - (padding * 2);
    const newThumbHeight = Math.max(
      (clientHeight / scrollHeight) * availableTrackHeight,
      30 // Minimum thumb height
    );
    setThumbHeight(newThumbHeight);

    // Calculate thumb position (with top padding offset)
    const scrollPercentage = scrollTop / (scrollHeight - clientHeight);
    const newThumbTop = padding + (scrollPercentage * (availableTrackHeight - newThumbHeight));
    setThumbTop(newThumbTop);
  };

  // Handle scroll event
  const handleScroll = () => {
    updateScrollbar();
  };

  // Handle thumb drag
  const handleMouseDown = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    if (!contentRef.current || !scrollTrackRef.current) return;

    dragStartY.current = e.clientY;
    dragStartScrollTop.current = contentRef.current.scrollTop;
    setIsDragging(true);
  };

  useEffect(() => {
    const handleMouseMove = (e: MouseEvent) => {
      if (!isDragging || !contentRef.current || !scrollTrackRef.current) return;

      const deltaY = e.clientY - dragStartY.current;
      const { scrollHeight, clientHeight } = contentRef.current;
      const trackHeight = scrollTrackRef.current.clientHeight;
      const padding = 4; // Same padding as in updateScrollbar

      // Calculate scroll ratio (accounting for padding)
      const availableTrackHeight = trackHeight - (padding * 2);
      const scrollRatio = (scrollHeight - clientHeight) / (availableTrackHeight - thumbHeight);
      const newScrollTop = dragStartScrollTop.current + (deltaY * scrollRatio);

      contentRef.current.scrollTop = Math.max(
        0,
        Math.min(newScrollTop, scrollHeight - clientHeight)
      );
    };

    const handleMouseUp = () => {
      setIsDragging(false);
    };

    if (isDragging) {
      document.addEventListener('mousemove', handleMouseMove);
      document.addEventListener('mouseup', handleMouseUp);
    }

    return () => {
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
    };
  }, [isDragging, thumbHeight]);

  // Handle track click
  const handleTrackClick = (e: React.MouseEvent) => {
    if (!contentRef.current || !scrollTrackRef.current || e.target !== scrollTrackRef.current) return;

    const trackRect = scrollTrackRef.current.getBoundingClientRect();
    const mouseY = e.clientY - trackRect.top;
    const trackHeight = trackRect.height;
    const padding = 4; // Same padding as in updateScrollbar

    // Account for padding when calculating scroll position
    const availableTrackHeight = trackHeight - (padding * 2);
    const adjustedMouseY = Math.max(padding, Math.min(mouseY, trackHeight - padding));
    const scrollPercentage = (adjustedMouseY - padding) / availableTrackHeight;

    const { scrollHeight, clientHeight } = contentRef.current;
    contentRef.current.scrollTop = scrollPercentage * (scrollHeight - clientHeight);
  };

  // Update scrollbar on mount and resize
  useEffect(() => {
    // Initial update with a small delay to ensure DOM is ready
    const timer = setTimeout(() => {
      updateScrollbar();
    }, 10);

    const resizeObserver = new ResizeObserver(() => {
      updateScrollbar();
    });

    if (contentRef.current) {
      resizeObserver.observe(contentRef.current);
    }

    return () => {
      clearTimeout(timer);
      resizeObserver.disconnect();
    };
  }, [children]);

  // Also update on window resize
  useEffect(() => {
    const handleResize = () => {
      updateScrollbar();
    };

    window.addEventListener('resize', handleResize);
    return () => window.removeEventListener('resize', handleResize);
  }, []);

  return (
    <div
      className={`relative ${className}`}
      style={{
        borderRadius: 'var(--theme-border-radius-lg, 0.75rem)',
        maxHeight,
        isolation: 'isolate',
      }}
    >
      {/* Content area */}
      <div
        ref={contentRef}
        onScroll={handleScroll}
        className="overflow-y-auto overflow-x-hidden"
        style={{
          maxHeight,
          paddingRight,
          scrollbarWidth: 'none', // Firefox
          msOverflowStyle: 'none', // IE/Edge
        }}
      >
        <style>{`
          .overflow-y-auto::-webkit-scrollbar {
            display: none;
          }
        `}</style>
        {children}
      </div>

      {/* Custom scrollbar track - Always render but conditionally show */}
      <div
        ref={scrollTrackRef}
        onClick={handleTrackClick}
        className="absolute"
        style={{
          right: '0px',
          top: '2px',
          bottom: '2px',
          width: '8px',
          background: showScrollbar ? 'var(--theme-scrollbar-track)' : 'transparent',
          borderRadius: 'var(--theme-border-radius-lg, 0.75rem)',
          opacity: showScrollbar ? 1 : 0,
          pointerEvents: showScrollbar ? 'auto' : 'none',
        }}
      >
        {/* Scrollbar thumb */}
        {showScrollbar && (
          <div
            ref={scrollThumbRef}
            onMouseDown={handleMouseDown}
            className="absolute transition-colors cursor-pointer"
            style={{
              left: '2px',
              width: '4px',
              height: `${thumbHeight}px`,
              top: `${thumbTop}px`,
              borderRadius: 'var(--theme-border-radius, 0.5rem)',
              minHeight: '30px',
              backgroundColor: isDragging
                ? 'var(--theme-scrollbar-hover)'
                : 'var(--theme-scrollbar-thumb)',
            }}
            onMouseEnter={(e) => {
              if (!isDragging) {
                e.currentTarget.style.backgroundColor = 'var(--theme-scrollbar-hover)';
              }
            }}
            onMouseLeave={(e) => {
              if (!isDragging) {
                e.currentTarget.style.backgroundColor = 'var(--theme-scrollbar-thumb)';
              }
            }}
          />
        )}
      </div>
    </div>
  );
};
