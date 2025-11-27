import React, { useState, useRef, useEffect, useLayoutEffect } from 'react';
import { createPortal } from 'react-dom';
import { HelpCircle, AlertTriangle, Info, CheckCircle2 } from 'lucide-react';
import { CustomScrollbar } from './CustomScrollbar';

export interface HelpPopoverSection {
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
      <div className="space-y-3 text-xs leading-relaxed" style={{ color: 'var(--theme-text-secondary)' }}>
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
            className={sectionIndex > 0 ? 'border-t pt-4' : ''}
            style={sectionIndex > 0 ? { borderColor: 'var(--theme-border)' } : undefined}
          >
            <div
              className="text-xs font-semibold mb-2"
              style={{ color: 'var(--theme-text-primary)' }}
            >
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
                  <span style={{ color: 'var(--theme-text-secondary)' }}>
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
  const [popoverPos, setPopoverPos] = useState<{ x: number; y: number } | null>(null);

  // Reset position when closing so stale position doesn't flash on reopen
  useEffect(() => {
    if (!isOpen) {
      setPopoverPos(null);
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
        : triggerRect.right - width;
      let y = triggerRect.bottom + 8;

      // Clamp X to viewport bounds
      if (x + width > window.innerWidth - viewportPadding) {
        x = window.innerWidth - width - viewportPadding;
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
    }, 10);

    return () => clearTimeout(timer);
  }, [isOpen, position, width]);

  return (
    <>
      <button
        ref={triggerRef}
        onClick={() => setIsOpen(!isOpen)}
        className="p-1 rounded-md transition-colors"
        style={{
          color: isOpen ? 'var(--theme-primary)' : 'var(--theme-text-secondary)',
          backgroundColor: isOpen ? 'var(--theme-primary-subtle)' : 'transparent'
        }}
        onMouseEnter={(e) => {
          if (!isOpen) {
            e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)';
          }
        }}
        onMouseLeave={(e) => {
          if (!isOpen) {
            e.currentTarget.style.backgroundColor = 'transparent';
          }
        }}
      >
        <HelpCircle className="w-4 h-4" />
      </button>

      {isOpen && createPortal(
        <div
          ref={popoverRef}
          className="fixed rounded-lg border shadow-2xl"
          style={{
            left: popoverPos?.x ?? -9999,
            top: popoverPos?.y ?? -9999,
            width: width,
            maxWidth: `calc(100vw - 24px)`,
            maxHeight: maxHeight || `calc(100vh - 100px)`,
            visibility: popoverPos ? 'visible' : 'hidden',
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)',
            boxShadow: '0 10px 40px rgba(0, 0, 0, 0.3)'
          }}
        >
          {maxHeight ? (
            <CustomScrollbar maxHeight={maxHeight}>
              <div className="p-4">
                <PopoverContent sections={sections} children={children} />
              </div>
            </CustomScrollbar>
          ) : (
            <div className="p-4">
              <PopoverContent sections={sections} children={children} />
            </div>
          )}
        </div>,
        document.body
      )}
    </>
  );
};

/** Helper component for code blocks in HelpPopover */
export const HelpCode: React.FC<{ children: React.ReactNode; block?: boolean }> = ({
  children,
  block = false
}) => {
  if (block) {
    return (
      <div
        className="p-2.5 rounded font-mono text-[10px] leading-relaxed"
        style={{
          backgroundColor: 'var(--theme-bg-tertiary)',
          color: 'var(--theme-text-secondary)',
          border: '1px solid var(--theme-border-secondary)'
        }}
      >
        {children}
      </div>
    );
  }
  return (
    <code
      className="px-1.5 py-0.5 rounded font-mono text-[10px]"
      style={{
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-primary)'
      }}
    >
      {children}
    </code>
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
    className="rounded-md"
    style={variant === 'subtle' ? {
      backgroundColor: 'var(--theme-bg-secondary)',
      padding: '0.625rem',
      marginLeft: '-0.25rem',
      marginRight: '-0.25rem'
    } : undefined}
  >
    <div
      className="text-[11px] font-semibold mb-1.5 uppercase tracking-wide"
      style={{ color: 'var(--theme-text-muted)' }}
    >
      {title}
    </div>
    <div className="text-xs leading-relaxed" style={{ color: 'var(--theme-text-secondary)' }}>
      {children}
    </div>
  </div>
);

/** Highlighted keyword/term pill */
export const HelpKeyword: React.FC<{
  children: React.ReactNode;
  color?: 'blue' | 'green' | 'orange' | 'purple' | 'cyan' | 'red';
}> = ({
  children,
  color = 'blue'
}) => {
  const colorMap = {
    blue: { bg: 'var(--theme-info-bg)', text: 'var(--theme-info-text)' },
    green: { bg: 'var(--theme-success-bg)', text: 'var(--theme-success-text)' },
    orange: { bg: 'var(--theme-warning-bg)', text: 'var(--theme-warning-text)' },
    purple: { bg: 'color-mix(in srgb, var(--theme-icon-purple) 15%, transparent)', text: 'var(--theme-icon-purple)' },
    cyan: { bg: 'color-mix(in srgb, var(--theme-icon-cyan) 15%, transparent)', text: 'var(--theme-icon-cyan)' },
    red: { bg: 'var(--theme-error-bg)', text: 'var(--theme-error-text)' }
  };

  return (
    <span
      className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium"
      style={{
        backgroundColor: colorMap[color].bg,
        color: colorMap[color].text
      }}
    >
      {children}
    </span>
  );
};

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
      className="flex gap-2 p-2 rounded-r text-[11px] leading-relaxed"
      style={{
        backgroundColor: config[type].bg,
        borderLeft: `3px solid ${config[type].border}`
      }}
    >
      <Icon
        className="w-3.5 h-3.5 flex-shrink-0 mt-0.5"
        style={{ color: config[type].iconColor }}
      />
      <div style={{ color: 'var(--theme-text-primary)' }}>
        {children}
      </div>
    </div>
  );
};

/** Bullet list item with colored bullet */
export const HelpListItem: React.FC<{
  children: React.ReactNode;
  color?: 'default' | 'blue' | 'green' | 'orange';
}> = ({
  children,
  color = 'default'
}) => {
  const bulletColors = {
    default: 'var(--theme-text-muted)',
    blue: 'var(--theme-info)',
    green: 'var(--theme-success)',
    orange: 'var(--theme-warning)'
  };

  return (
    <div className="flex gap-2 text-xs leading-relaxed">
      <span
        className="flex-shrink-0 mt-1.5 w-1 h-1 rounded-full"
        style={{ backgroundColor: bulletColors[color] }}
      />
      <span style={{ color: 'var(--theme-text-secondary)' }}>
        {children}
      </span>
    </div>
  );
};

/** Definition list for term-description pairs */
export const HelpDefinition: React.FC<{
  term: string;
  children: React.ReactNode;
  termColor?: 'default' | 'blue' | 'green' | 'orange' | 'purple';
}> = ({
  term,
  children,
  termColor = 'default'
}) => {
  const colors = {
    default: 'var(--theme-text-primary)',
    blue: 'var(--theme-info-text)',
    green: 'var(--theme-success-text)',
    orange: 'var(--theme-warning-text)',
    purple: 'var(--theme-icon-purple)'
  };

  return (
    <div className="text-xs leading-relaxed">
      <span
        className="font-medium"
        style={{ color: colors[termColor] }}
      >
        {term}
      </span>
      <span style={{ color: 'var(--theme-text-muted)' }}> — </span>
      <span style={{ color: 'var(--theme-text-secondary)' }}>
        {children}
      </span>
    </div>
  );
};

/** Compact step indicator */
export const HelpStep: React.FC<{
  number: number;
  children: React.ReactNode;
}> = ({
  number,
  children
}) => (
  <div className="flex gap-2 text-xs leading-relaxed">
    <span
      className="flex-shrink-0 w-4 h-4 rounded-full flex items-center justify-center text-[10px] font-bold"
      style={{
        backgroundColor: 'var(--theme-primary)',
        color: 'var(--theme-button-text)'
      }}
    >
      {number}
    </span>
    <span style={{ color: 'var(--theme-text-secondary)' }}>
      {children}
    </span>
  </div>
);
