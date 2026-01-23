import React from 'react';
import { ChevronDown, type LucideIcon } from 'lucide-react';

interface AccordionSectionProps {
  title: string;
  count?: number;
  icon?: LucideIcon;
  iconColor?: string;
  children: React.ReactNode;
  isExpanded: boolean;
  onToggle: () => void;
  badge?: React.ReactNode;
}

export const AccordionSection: React.FC<AccordionSectionProps> = ({
  title,
  count,
  icon: Icon,
  iconColor = 'var(--theme-accent)',
  children,
  isExpanded,
  onToggle,
  badge
}) => {
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onToggle();
    }
  };

  const handleHeaderClick = (e: React.MouseEvent) => {
    // Don't toggle if clicking on an interactive element inside the header
    const target = e.target as HTMLElement;
    if (target.closest('button, input, select, [role="button"], [role="listbox"], .ed-trigger')) {
      return;
    }
    onToggle();
  };

  return (
    <div
      className={`group rounded-lg overflow-hidden transition-all duration-300 border ${
        isExpanded
          ? 'bg-themed-secondary border-themed-primary shadow-[0_4px_16px_rgba(0,0,0,0.2),0_1px_4px_rgba(0,0,0,0.12)]'
          : 'bg-[color-mix(in_srgb,var(--theme-bg-secondary)_60%,transparent)] border-themed-secondary shadow-[0_1px_3px_rgba(0,0,0,0.1)]'
      }`}
    >
      {/* Header - using div with role="button" to allow nested interactive elements */}
      <div
        role="button"
        tabIndex={0}
        onClick={handleHeaderClick}
        onKeyDown={handleKeyDown}
        className="w-full px-4 py-3 flex items-center justify-between text-left transition-all duration-200 group/header bg-transparent cursor-pointer"
      >
        <div className="flex items-center gap-3">
          {/* Icon with animated background */}
          {Icon && (
            <div
              className={`w-8 h-8 rounded-lg flex items-center justify-center transition-all duration-300 ${
                isExpanded ? 'scale-105' : 'scale-100'
              }`}
              style={{
                backgroundColor: `color-mix(in srgb, ${iconColor} 15%, transparent)`,
                boxShadow: isExpanded
                  ? `0 2px 8px color-mix(in srgb, ${iconColor} 25%, transparent)`
                  : 'none'
              }}
            >
              <Icon
                className={`w-4 h-4 flex-shrink-0 transition-transform duration-300 ${
                  isExpanded ? 'scale-110' : 'scale-100'
                }`}
                style={{ color: iconColor }}
              />
            </div>
          )}

          {/* Title */}
          <span
            className={`font-semibold transition-colors duration-200 ${
              isExpanded ? 'text-themed-primary' : 'text-themed-secondary'
            }`}
          >
            {title}
          </span>

          {/* Count Badge */}
          {count !== undefined && (
            <span
              className={`px-2.5 py-1 text-xs rounded-full font-semibold tabular-nums transition-all duration-300 ${
                isExpanded ? 'scale-105' : 'scale-100 bg-themed-tertiary text-themed-muted'
              }`}
              style={isExpanded ? {
                backgroundColor: `color-mix(in srgb, ${iconColor} 20%, transparent)`,
                color: iconColor
              } : undefined}
            >
              {count.toLocaleString()}
            </span>
          )}

          {badge}
        </div>

        {/* Chevron with rotation animation */}
        <div
          className={`flex items-center justify-center w-8 h-8 rounded-lg transition-all duration-300 ${
            isExpanded
              ? 'bg-[color-mix(in_srgb,var(--theme-accent)_10%,transparent)]'
              : 'bg-transparent'
          }`}
        >
          <ChevronDown
            className={`w-5 h-5 transition-all duration-300 ease-out ${
              isExpanded ? 'rotate-180 text-themed-accent' : 'rotate-0 text-themed-muted'
            }`}
          />
        </div>
      </div>

      {/* Content with smooth animation */}
      <div
        className={`overflow-hidden transition-all duration-300 ease-out ${
          isExpanded
            ? 'max-h-[5000px] opacity-100 translate-y-0'
            : 'max-h-0 opacity-0 -translate-y-2'
        }`}
      >
        <div
          className="px-4 pb-4 pt-3 border-t border-themed-secondary"
        >
          {children}
        </div>
      </div>
    </div>
  );
};

export default AccordionSection;
