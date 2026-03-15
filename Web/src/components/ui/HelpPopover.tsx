import React, { useState, useRef, useEffect, useLayoutEffect } from 'react';
import { createPortal } from 'react-dom';
import { HelpCircle, AlertTriangle, Info, CheckCircle2 } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';

interface HelpPopoverProps {
  /** Rich content as children */
  children?: React.ReactNode;
  /** Popover alignment */
  position?: 'left' | 'right';
  /** Popover width in pixels */
  width?: number;
  /** Max height with scroll */
  maxHeight?: string;
}

export const HelpPopover: React.FC<HelpPopoverProps> = ({
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

    const timer = setTimeout(() => {
      if (!triggerRef.current || !popoverRef.current) return;

      const triggerRect = triggerRef.current.getBoundingClientRect();
      const popoverRect = popoverRef.current.getBoundingClientRect();
      const viewportPadding = 12;

      let x = position === 'left' ? triggerRect.left : triggerRect.right - effectiveWidth;
      let y = triggerRect.bottom + 8;

      if (x + effectiveWidth > window.innerWidth - viewportPadding) {
        x = window.innerWidth - effectiveWidth - viewportPadding;
      }
      if (x < viewportPadding) {
        x = viewportPadding;
      }

      const popoverHeight = popoverRect.height || 200;
      if (y + popoverHeight > window.innerHeight - viewportPadding) {
        y = triggerRect.top - popoverHeight - 8;
      }

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

      {isOpen &&
        createPortal(
          <div
            ref={popoverRef}
            className="fixed themed-border-radius border help-popover themed-card max-w-[calc(100vw-24px)] z-[90]"
            style={{
              left: popoverPos.x,
              top: popoverPos.y,
              width: effectiveWidth,
              maxHeight: maxHeight || `calc(100vh - 100px)`,
              opacity: isReady ? 1 : 0,
              transition: 'none',
              pointerEvents: isReady ? 'auto' : 'none'
            }}
          >
            {maxHeight ? (
              <CustomScrollbar maxHeight={maxHeight}>
                <div className="p-4 sm:p-5">
                  <div className="help-popover-sections text-xs leading-relaxed text-themed-secondary">
                    {children}
                  </div>
                </div>
              </CustomScrollbar>
            ) : (
              <div className="p-4 sm:p-5">
                <div className="help-popover-sections text-xs leading-relaxed text-themed-secondary">
                  {children}
                </div>
              </div>
            )}
          </div>,
          document.body
        )}
    </>
  );
};

/** Section with title in HelpPopover */
export const HelpSection: React.FC<{
  title: string;
  children: React.ReactNode;
  variant?: 'default' | 'subtle';
}> = ({ title, children, variant = 'default' }) => (
  <div className={variant === 'subtle' ? 'help-section-subtle' : ''}>
    <div className="help-section-title">{title}</div>
    <div className="text-xs leading-relaxed text-themed-secondary">{children}</div>
  </div>
);

/** Note/callout box in HelpPopover */
export const HelpNote: React.FC<{
  children: React.ReactNode;
  type?: 'info' | 'warning' | 'success' | 'tip';
}> = ({ children, type = 'info' }) => {
  const iconMap = {
    info: Info,
    warning: AlertTriangle,
    success: CheckCircle2,
    tip: Info
  };

  const iconColorMap = {
    info: 'text-themed-info',
    warning: 'text-themed-warning',
    success: 'text-themed-success',
    tip: 'icon-purple'
  };

  const Icon = iconMap[type];

  return (
    <div className={`help-note help-note-${type}`}>
      <Icon className={`w-3.5 h-3.5 flex-shrink-0 mt-0.5 ${iconColorMap[type]}`} />
      <div className="text-themed-primary">{children}</div>
    </div>
  );
};

interface HelpDefinitionItem {
  term: string;
  description: string;
}

/** Definition list replacing the divide-y pattern */
export const HelpDefinition: React.FC<{
  items: HelpDefinitionItem[];
}> = ({ items }) => (
  <div className="help-definition-list">
    {items.map((item: HelpDefinitionItem) => (
      <div key={item.term}>
        <div className="help-definition-term">{item.term}</div>
        <div className="help-definition-desc">{item.description}</div>
      </div>
    ))}
  </div>
);
