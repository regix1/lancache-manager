import React, { useState, useRef, useEffect, useLayoutEffect } from 'react';
import { createPortal } from 'react-dom';
import { HelpCircle, AlertTriangle, Info, CheckCircle2 } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';

interface HelpPopoverSection {
  title: string;
  items: {
    label: string;
    description: string;
    color?: string;
  }[];
}

interface HelpPopoverProps {
  /** Simple sections with label-description pairs */
  sections?: HelpPopoverSection[];
  /** Rich content as children (alternative to sections) */
  children?: React.ReactNode;
  /** Popover alignment */
  position?: 'left' | 'right';
  /** Popover width in pixels */
  width?: number;
  /** Max height with scroll */
  maxHeight?: string;
}

// Internal component for popover content
const PopoverContent: React.FC<{
  sections?: HelpPopoverSection[];
  children?: React.ReactNode;
}> = ({ sections, children }) => {
  if (children) {
    return (
      <div className="space-y-3 text-xs leading-relaxed text-themed-secondary">
        {children}
      </div>
    );
  }

  if (sections) {
    return (
      <div className="space-y-4">
        {sections.map((section, sectionIndex) => (
          <div
            key={section.title}
            className={sectionIndex > 0 ? 'border-t border-[var(--theme-border)] pt-4' : ''}
          >
            <div className="text-xs font-semibold mb-2 text-themed-primary">
              {section.title}
            </div>
            <div className="space-y-1.5">
              {section.items.map((item) => (
                <div key={item.label} className="flex gap-2 text-xs">
                  <span
                    className="font-medium flex-shrink-0"
                    style={{ color: item.color || 'var(--theme-text-primary)' }}
                  >
                    {item.label}
                  </span>
                  <span className="text-themed-secondary">
                    {item.description}
                  </span>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    );
  }

  return null;
};

export const HelpPopover: React.FC<HelpPopoverProps> = ({
  sections,
  children,
  position = 'left',
  width = 320,
  maxHeight
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const [popoverPos, setPopoverPos] = useState<{ x: number; y: number }>({ x: 0, y: 0 });
  const [isReady, setIsReady] = useState(false);
  const [effectiveWidth, setEffectiveWidth] = useState(width);

  // Calculate effective width based on viewport
  useEffect(() => {
    const calculateWidth = () => {
      const viewportWidth = window.innerWidth;
      // On mobile (<640px), use smaller width with more margin
      if (viewportWidth < 640) {
        setEffectiveWidth(Math.min(width, viewportWidth - 32));
      } else {
        setEffectiveWidth(width);
      }
    };

    calculateWidth();
    window.addEventListener('resize', calculateWidth);
    return () => window.removeEventListener('resize', calculateWidth);
  }, [width]);

  // Reset position when closing so stale position doesn't flash on reopen
  useEffect(() => {
    if (!isOpen) {
      setIsReady(false);
    }
  }, [isOpen]);

  // Close on click outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (
        popoverRef.current &&
        !popoverRef.current.contains(target) &&
        triggerRef.current &&
        !triggerRef.current.contains(target)
      ) {
        setIsOpen(false);
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
    }
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  // Close on background scroll (but not when scrolling inside the popover)
  useEffect(() => {
    if (!isOpen) return;

    const handleScroll = (e: Event) => {
      // Don't close if scrolling inside the popover
      if (popoverRef.current?.contains(e.target as Node)) {
        return;
      }
      setIsOpen(false);
    };

    window.addEventListener('scroll', handleScroll, { passive: true, capture: true });
    return () => window.removeEventListener('scroll', handleScroll, { capture: true });
  }, [isOpen]);

  // Calculate position when open
  useLayoutEffect(() => {
    if (!isOpen || !triggerRef.current || !popoverRef.current) return;

    // Small delay to ensure popover is rendered with content
    const timer = setTimeout(() => {
      if (!triggerRef.current || !popoverRef.current) return;

      const triggerRect = triggerRef.current.getBoundingClientRect();
      const popoverRect = popoverRef.current.getBoundingClientRect();
      const viewportPadding = 12;

      // Start position: below trigger, aligned based on position prop
      let x = position === 'left'
        ? triggerRect.left
        : triggerRect.right - effectiveWidth;
      let y = triggerRect.bottom + 8;

      // Clamp X to viewport bounds
      if (x + effectiveWidth > window.innerWidth - viewportPadding) {
        x = window.innerWidth - effectiveWidth - viewportPadding;
      }
      if (x < viewportPadding) {
        x = viewportPadding;
      }

      // If would go off bottom, show above
      const popoverHeight = popoverRect.height || 200; // fallback height
      if (y + popoverHeight > window.innerHeight - viewportPadding) {
        y = triggerRect.top - popoverHeight - 8;
      }

      // Clamp Y to viewport
      y = Math.max(viewportPadding, y);

      setPopoverPos({ x, y });
      setIsReady(true);
    }, 10);

    return () => clearTimeout(timer);
  }, [isOpen, position, effectiveWidth]);

  return (
    <>
      <button
        ref={triggerRef}
        onClick={() => setIsOpen(!isOpen)}
        className={`p-1 rounded-md transition-colors ${
          isOpen
            ? 'text-[var(--theme-primary)] bg-[var(--theme-primary-subtle)]'
            : 'text-themed-secondary bg-transparent hover:bg-themed-hover'
        }`}
      >
        <HelpCircle className="w-4 h-4" />
      </button>

      {isOpen && createPortal(
        <div
          ref={popoverRef}
          className="fixed themed-border-radius border shadow-[0_10px_40px_rgba(0,0,0,0.4)] themed-card max-w-[calc(100vw-24px)] z-[90]"
          style={{
            left: popoverPos.x,
            top: popoverPos.y,
            width: effectiveWidth,
            maxHeight: maxHeight || `calc(100vh - 100px)`,
            // Use opacity for instant appear/disappear without animation
            opacity: isReady ? 1 : 0,
            // Ensure no transitions that could cause flying effect
            transition: 'none',
            // Prevent interaction during measurement
            pointerEvents: isReady ? 'auto' : 'none'
          }}
        >
          {maxHeight ? (
            <CustomScrollbar maxHeight={maxHeight}>
              <div className="p-3 sm:p-4">
                <PopoverContent sections={sections} children={children} />
              </div>
            </CustomScrollbar>
          ) : (
            <div className="p-3 sm:p-4">
              <PopoverContent sections={sections} children={children} />
            </div>
          )}
        </div>,
        document.body
      )}
    </>
  );
};

/** Helper component for section titles in HelpPopover - now with subtle background */
export const HelpSection: React.FC<{
  title: string;
  children: React.ReactNode;
  variant?: 'default' | 'subtle';
}> = ({
  title,
  children,
  variant = 'default'
}) => (
  <div
    className={`rounded-md ${
      variant === 'subtle' ? 'bg-themed-secondary p-2.5 -mx-1' : ''
    }`}
  >
    <div className="text-[11px] font-semibold mb-1.5 uppercase tracking-wide text-themed-muted">
      {title}
    </div>
    <div className="text-xs leading-relaxed text-themed-secondary">
      {children}
    </div>
  </div>
);

/** Important note/callout with colored left border */
export const HelpNote: React.FC<{
  children: React.ReactNode;
  type?: 'info' | 'warning' | 'success' | 'tip';
}> = ({
  children,
  type = 'info'
}) => {
  const config = {
    info: {
      border: 'var(--theme-info)',
      bg: 'var(--theme-info-bg)',
      icon: Info,
      iconColor: 'var(--theme-info-text)'
    },
    warning: {
      border: 'var(--theme-warning)',
      bg: 'var(--theme-warning-bg)',
      icon: AlertTriangle,
      iconColor: 'var(--theme-warning-text)'
    },
    success: {
      border: 'var(--theme-success)',
      bg: 'var(--theme-success-bg)',
      icon: CheckCircle2,
      iconColor: 'var(--theme-success-text)'
    },
    tip: {
      border: 'var(--theme-icon-purple)',
      bg: 'color-mix(in srgb, var(--theme-icon-purple) 10%, transparent)',
      icon: Info,
      iconColor: 'var(--theme-icon-purple)'
    }
  };

  const Icon = config[type].icon;

  return (
    <div
      className="flex gap-2 p-2 rounded-r text-[11px] leading-relaxed border-l-[3px]"
      style={{
        backgroundColor: config[type].bg,
        borderLeftColor: config[type].border
      }}
    >
      <Icon
        className="w-3.5 h-3.5 flex-shrink-0 mt-0.5"
        style={{ color: config[type].iconColor }}
      />
      <div className="text-themed-primary">
        {children}
      </div>
    </div>
  );
};
