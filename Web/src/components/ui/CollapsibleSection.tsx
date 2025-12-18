import React, { useState } from 'react';
import { ChevronDown, type LucideIcon } from 'lucide-react';

interface CollapsibleSectionProps {
  title: string;
  icon?: LucideIcon;
  children: React.ReactNode;
  defaultOpen?: boolean;
  alwaysOpen?: boolean;
}

export const CollapsibleSection: React.FC<CollapsibleSectionProps> = ({
  title,
  icon: Icon,
  children,
  defaultOpen = false,
  alwaysOpen = false
}) => {
  const [isOpen, setIsOpen] = useState(defaultOpen || alwaysOpen);

  const handleToggle = () => {
    if (!alwaysOpen) {
      setIsOpen(!isOpen);
    }
  };

  return (
    <div
      className="rounded-lg overflow-hidden transition-all duration-300"
      style={{
        backgroundColor: isOpen
          ? 'var(--theme-bg-secondary)'
          : 'color-mix(in srgb, var(--theme-bg-secondary) 60%, transparent)',
        boxShadow: isOpen
          ? '0 4px 16px rgba(0, 0, 0, 0.15), 0 1px 4px rgba(0, 0, 0, 0.1)'
          : '0 1px 3px rgba(0, 0, 0, 0.08)',
        border: '1px solid',
        borderColor: isOpen
          ? 'var(--theme-border-primary)'
          : 'var(--theme-border-secondary)'
      }}
    >
      <button
        onClick={handleToggle}
        className={`w-full px-4 sm:px-6 py-3 sm:py-4 flex items-center justify-between text-left transition-all duration-200 ${
          alwaysOpen ? 'cursor-default' : 'cursor-pointer'
        }`}
        disabled={alwaysOpen}
        style={{
          background: isOpen
            ? 'linear-gradient(135deg, color-mix(in srgb, var(--theme-accent) 8%, transparent) 0%, transparent 100%)'
            : 'transparent'
        }}
      >
        <div className="flex items-center gap-2 sm:gap-3">
          {Icon && (
            <div
              className="w-8 h-8 sm:w-9 sm:h-9 rounded-lg flex items-center justify-center transition-all duration-300"
              style={{
                backgroundColor: 'color-mix(in srgb, var(--theme-accent) 15%, transparent)',
                transform: isOpen ? 'scale(1.05)' : 'scale(1)',
                boxShadow: isOpen
                  ? '0 2px 8px color-mix(in srgb, var(--theme-accent) 25%, transparent)'
                  : 'none'
              }}
            >
              <Icon
                className="w-4 h-4 sm:w-5 sm:h-5 flex-shrink-0 transition-transform duration-300"
                style={{
                  color: 'var(--theme-accent)',
                  transform: isOpen ? 'scale(1.1)' : 'scale(1)'
                }}
              />
            </div>
          )}
          <h2
            className="text-lg sm:text-xl font-semibold transition-colors duration-200"
            style={{
              color: isOpen ? 'var(--theme-text-primary)' : 'var(--theme-text-secondary)'
            }}
          >
            {title}
          </h2>
        </div>
        {!alwaysOpen && (
          <div
            className="flex items-center justify-center w-8 h-8 rounded-lg transition-all duration-300"
            style={{
              backgroundColor: isOpen
                ? 'color-mix(in srgb, var(--theme-accent) 10%, transparent)'
                : 'transparent'
            }}
          >
            <ChevronDown
              className="w-5 h-5 transition-all duration-300 ease-out"
              style={{
                color: isOpen ? 'var(--theme-accent)' : 'var(--theme-text-muted)',
                transform: isOpen ? 'rotate(180deg)' : 'rotate(0deg)'
              }}
            />
          </div>
        )}
      </button>

      {/* Content with smooth animation */}
      <div
        className="overflow-hidden transition-all duration-300 ease-out"
        style={{
          maxHeight: isOpen ? '5000px' : '0',
          opacity: isOpen ? 1 : 0,
          transform: isOpen ? 'translateY(0)' : 'translateY(-8px)'
        }}
      >
        <div
          className="px-4 sm:px-6 pb-4 sm:pb-6 pt-3 sm:pt-4"
          style={{
            borderTop: '1px solid var(--theme-border-secondary)',
            background: 'linear-gradient(180deg, color-mix(in srgb, var(--theme-bg-tertiary) 30%, transparent) 0%, transparent 100%)'
          }}
        >
          <div className="space-y-4 sm:space-y-6 section-dividers">
            {children}
          </div>
        </div>
      </div>
    </div>
  );
};
