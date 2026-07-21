import React, { useRef, useState, useEffect } from 'react';

interface CustomScrollbarProps {
  children: React.ReactNode;
  maxHeight?: string;
  className?: string;
  /** Right padding for scrollbar space. Use 'compact' for smaller dropdowns. Default is 12px. */
  paddingMode?: 'default' | 'compact' | 'none';
  /**
   * Corner radius of the scroll viewport. Defaults to 'xl' (rounded). Set 'none' when the scrolled
   * content has bordered boxes sitting flush against the edges: the viewport clips overflow at this
   * radius, and a rounded clip shaves the corner border of a box flush against it (the scrollbar's
   * own corners are invisible against a matching background anyway). Setting it here — where the
   * component owns its className — is the reusable fix; a consumer-side CSS override can't win
   * against Tailwind's important-mode `rounded-xl` on this same element.
   */
  radius?: 'xl' | 'none';
  /**
   * Visual treatment of the scrollbar.
   * 'rail' (default): a full-height tinted track at the edge, with content inset by
   * paddingMode so it stops at the track.
   * 'float': for menus whose rows highlight edge-to-edge - no gutter is reserved
   * (paddingMode is ignored), the track is invisible, and a slim pill thumb floats
   * above the content, inset from the edge and the panel's rounded corners. A
   * hairline ring in the panel background keeps it legible over row highlights.
   */
  variant?: 'rail' | 'float';
}

export const CustomScrollbar: React.FC<CustomScrollbarProps> = ({
  children,
  maxHeight = '32rem',
  className = '',
  paddingMode = 'default',
  radius = 'xl',
  variant = 'rail'
}) => {
  const basePaddingRight =
    variant === 'float' || paddingMode === 'none'
      ? '0px'
      : paddingMode === 'compact'
        ? '6px'
        : '12px';
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
    const availableTrackHeight = trackHeight - padding * 2;
    const newThumbHeight = Math.max(
      (clientHeight / scrollHeight) * availableTrackHeight,
      30 // Minimum thumb height
    );
    setThumbHeight(newThumbHeight);

    // Calculate thumb position (with top padding offset)
    const scrollPercentage = scrollTop / (scrollHeight - clientHeight);
    const newThumbTop = padding + scrollPercentage * (availableTrackHeight - newThumbHeight);
    setThumbTop(newThumbTop);
  };

  // Handle scroll event with RAF throttling
  const rafRef = useRef<number | null>(null);
  const handleScroll = () => {
    if (rafRef.current) return;
    rafRef.current = requestAnimationFrame(() => {
      updateScrollbar();
      rafRef.current = null;
    });
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
      const availableTrackHeight = trackHeight - padding * 2;
      const scrollRatio = (scrollHeight - clientHeight) / (availableTrackHeight - thumbHeight);
      const newScrollTop = dragStartScrollTop.current + deltaY * scrollRatio;

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
    if (!contentRef.current || !scrollTrackRef.current || e.target !== scrollTrackRef.current)
      return;

    const trackRect = scrollTrackRef.current.getBoundingClientRect();
    const mouseY = e.clientY - trackRect.top;
    const trackHeight = trackRect.height;
    const padding = 4; // Same padding as in updateScrollbar

    // Account for padding when calculating scroll position
    const availableTrackHeight = trackHeight - padding * 2;
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
      if (rafRef.current) cancelAnimationFrame(rafRef.current);
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
      className={`relative isolate ${radius === 'none' ? 'rounded-none' : 'rounded-xl'} ${className}`}
      style={{ maxHeight }}
    >
      {/* Content area */}
      <div
        ref={contentRef}
        onScroll={handleScroll}
        className="overflow-y-auto overflow-x-hidden rounded-[inherit]"
        style={{
          maxHeight,
          paddingRight: showScrollbar ? basePaddingRight : '0px',
          scrollbarWidth: 'none',
          msOverflowStyle: 'none',
          WebkitOverflowScrolling: 'touch',
          willChange: 'scroll-position'
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
        className={`absolute right-0 w-2 transition-opacity ${
          variant === 'float'
            ? 'csb-track--float top-1 bottom-1'
            : 'csb-track--rail top-0.5 bottom-0.5 rounded-xl'
        } ${showScrollbar ? 'opacity-100 pointer-events-auto' : 'opacity-0 pointer-events-none'}`}
      >
        {/* Scrollbar thumb - measured geometry stays inline; colors live in
            custom-scrollbar.css */}
        {showScrollbar && (
          <div
            ref={scrollThumbRef}
            onMouseDown={handleMouseDown}
            className={`csb-thumb absolute w-1 min-h-[30px] transition-colors cursor-pointer ${
              variant === 'float'
                ? 'csb-thumb--float right-[3px] rounded-full'
                : 'left-0.5 rounded-lg'
            } ${isDragging ? 'csb-thumb--active' : ''}`}
            style={{
              height: `${thumbHeight}px`,
              top: `${thumbTop}px`
            }}
          />
        )}
      </div>
    </div>
  );
};
