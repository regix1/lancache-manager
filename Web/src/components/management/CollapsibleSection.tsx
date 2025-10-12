import React, { useState } from 'react';
import { ChevronDown, ChevronRight, LucideIcon } from 'lucide-react';

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
    <div className="rounded-lg overflow-hidden bg-themed-secondary" style={{
      boxShadow: '0 1px 3px rgba(0, 0, 0, 0.12), 0 1px 2px rgba(0, 0, 0, 0.24)'
    }}>
      <button
        onClick={handleToggle}
        className={`w-full px-6 py-4 flex items-center justify-between text-left transition-all duration-200 ${
          alwaysOpen ? 'cursor-default' : 'hover:bg-themed-tertiary cursor-pointer'
        }`}
        disabled={alwaysOpen}
      >
        <div className="flex items-center gap-3">
          {Icon && <Icon className="w-5 h-5 text-themed-accent flex-shrink-0" />}
          <h2 className="text-xl font-semibold text-themed-primary">{title}</h2>
        </div>
        {!alwaysOpen && (
          isOpen ? (
            <ChevronDown className="w-5 h-5 text-themed-secondary transition-transform duration-200" />
          ) : (
            <ChevronRight className="w-5 h-5 text-themed-secondary transition-transform duration-200" />
          )
        )}
      </button>
      {isOpen && (
        <div
          className="px-6 pb-6 pt-4"
          style={{
            borderTop: '1px solid var(--theme-border-primary)',
            animation: 'expandDown 0.3s ease-out'
          }}
        >
          <div className="space-y-6 section-dividers">
            {children}
          </div>
        </div>
      )}
    </div>
  );
};
