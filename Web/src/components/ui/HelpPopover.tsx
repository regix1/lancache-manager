import React, { useState, useRef, useEffect } from 'react';
import { HelpCircle } from 'lucide-react';

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
  /** Popover width class */
  width?: string;
  /** Max height with scroll */
  maxHeight?: string;
}

export const HelpPopover: React.FC<HelpPopoverProps> = ({
  sections,
  children,
  position = 'left',
  width = 'w-72',
  maxHeight
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const popoverRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (popoverRef.current && !popoverRef.current.contains(event.target as Node)) {
        setIsOpen(false);
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleClickOutside);
    }
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [isOpen]);

  return (
    <div className="relative" ref={popoverRef}>
      <button
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

      {isOpen && (
        <div
          className={`absolute ${position === 'left' ? 'left-0' : 'right-0'} top-full mt-2 ${width} rounded-lg border shadow-lg z-50 p-4 ${maxHeight ? 'overflow-y-auto' : ''}`}
          style={{
            backgroundColor: 'var(--theme-card-bg)',
            borderColor: 'var(--theme-card-border)',
            maxHeight: maxHeight || undefined
          }}
        >
          {children ? (
            // Rich content mode
            <div className="text-xs text-themed-secondary space-y-3 leading-relaxed">
              {children}
            </div>
          ) : sections ? (
            // Simple sections mode
            <div className="space-y-3">
              {sections.map((section, sectionIndex) => (
                <div
                  key={section.title}
                  className={sectionIndex > 0 ? 'border-t pt-3' : ''}
                  style={sectionIndex > 0 ? { borderColor: 'var(--theme-border)' } : undefined}
                >
                  <h4
                    className="text-sm font-medium mb-2"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    {section.title}
                  </h4>
                  <div
                    className="space-y-1.5 text-xs"
                    style={{ color: 'var(--theme-text-secondary)' }}
                  >
                    {section.items.map((item) => (
                      <div key={item.label} className="flex gap-2">
                        <span
                          className="font-medium flex-shrink-0"
                          style={{ color: item.color || 'var(--theme-text-primary)' }}
                        >
                          {item.label}
                        </span>
                        <span>{item.description}</span>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      )}
    </div>
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
        className="p-2 rounded font-mono text-xs"
        style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
      >
        {children}
      </div>
    );
  }
  return (
    <code
      className="px-1 py-0.5 rounded font-mono"
      style={{ backgroundColor: 'var(--theme-bg-secondary)' }}
    >
      {children}
    </code>
  );
};

/** Helper component for section titles in HelpPopover */
export const HelpSection: React.FC<{ title: string; children: React.ReactNode }> = ({
  title,
  children
}) => (
  <div>
    <strong className="text-themed-primary">{title}</strong>
    <div className="mt-1">{children}</div>
  </div>
);
