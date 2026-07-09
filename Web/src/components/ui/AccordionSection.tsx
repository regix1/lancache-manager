import React from 'react';
import { ChevronDown, type LucideIcon } from 'lucide-react';
import { formatCount } from '@utils/formatters';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';

interface AccordionSectionProps {
  title: string;
  titleAccessory?: React.ReactNode;
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
  titleAccessory,
  count,
  icon: Icon,
  iconColor = 'var(--theme-accent)',
  children,
  isExpanded,
  onToggle,
  badge
}) => {
  const handleKeyDown = (e: React.KeyboardEvent) => {
    const target = e.target as HTMLElement;
    if (
      target.closest(
        'button, input, select, a, [role="listbox"], [role="combobox"], .ed-trigger, .ed-dropdown'
      )
    ) {
      return;
    }

    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onToggle();
    }
  };

  const handleHeaderClick = (e: React.MouseEvent) => {
    // Don't toggle if clicking on an interactive element inside the header
    const target = e.target as HTMLElement;
    if (
      target.closest(
        'button, input, select, a, [role="listbox"], [role="combobox"], .ed-trigger, .ed-dropdown'
      )
    ) {
      return;
    }
    onToggle();
  };

  const handleTouchEnd = (e: React.TouchEvent) => {
    // Don't toggle if touching an interactive element inside the header
    const target = e.target as HTMLElement;
    if (
      target.closest(
        'button, input, select, a, [role="listbox"], [role="combobox"], .ed-trigger, .ed-dropdown'
      )
    ) {
      return;
    }
  };

  const chevronButton = (
    <button
      type="button"
      onClick={(e) => {
        e.stopPropagation();
        onToggle();
      }}
      className={`flex items-center justify-center w-10 h-10 themed-border-radius transition duration-300 flex-shrink-0 ${
        isExpanded ? 'bg-[var(--theme-accent-subtle)]' : 'bg-transparent hover:bg-themed-tertiary'
      }`}
      aria-label={isExpanded ? 'Collapse section' : 'Expand section'}
    >
      <ChevronDown
        className={`w-5 h-5 transition duration-300 ease-out ${
          isExpanded ? 'rotate-180 text-themed-accent' : 'rotate-0 text-themed-muted'
        }`}
      />
    </button>
  );

  return (
    <div
      className={`group themed-border-radius overflow-hidden transition duration-300 border ${
        isExpanded
          ? 'bg-themed-secondary border-themed-primary shadow-[0_4px_16px_rgba(0,0,0,0.2),0_1px_4px_rgba(0,0,0,0.12)]'
          : 'bg-[var(--theme-bg-secondary-emphasis)] border-themed-secondary shadow-[0_1px_3px_rgba(0,0,0,0.1)]'
      }`}
    >
      {/* Header - using div with role="button" to allow nested interactive elements */}
      <div
        role="button"
        tabIndex={0}
        onClick={handleHeaderClick}
        onKeyDown={handleKeyDown}
        onTouchEnd={handleTouchEnd}
        className="w-full px-4 py-3 flex flex-wrap items-center gap-2 justify-between sm:gap-3 text-left transition duration-200 group/header bg-transparent cursor-pointer"
      >
        <div className="flex items-center gap-3 min-w-0 flex-1">
          {/* Icon with animated background */}
          {Icon && (
            <div
              className={`w-8 h-8 themed-border-radius flex items-center justify-center transition duration-300 flex-shrink-0 ${
                isExpanded ? 'scale-105' : 'scale-100'
              }`}
              style={{
                backgroundColor: `${iconColor.replace(')', '-subtle)')}`,
                boxShadow: isExpanded ? `0 2px 8px ${iconColor.replace(')', '-muted)')}` : 'none'
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

          {/* Title — wraps to two lines before ellipsizing so narrow screens
              keep the meaningful trailing words instead of cutting them off */}
          <span
            className={`font-semibold transition-colors duration-200 min-w-0 line-clamp-2 ${
              isExpanded ? 'text-themed-primary' : 'text-themed-secondary'
            }`}
          >
            {title}
          </span>

          {titleAccessory && (
            <span className="inline-flex flex-shrink-0 items-center">{titleAccessory}</span>
          )}

          {/* Count Badge */}
          {count !== undefined && (
            <span
              className={`themed-badge badge-count ml-1.5 font-semibold transition duration-300 flex-shrink-0 ${
                isExpanded ? 'scale-105' : 'scale-100 bg-themed-tertiary text-themed-muted'
              }`}
              style={
                isExpanded
                  ? {
                      backgroundColor: `${iconColor.replace(')', '-muted)')}`,
                      color: iconColor
                    }
                  : undefined
              }
            >
              {formatCount(count)}
            </span>
          )}
        </div>

        {/* Action badge + chevron cluster — stays inline on the title row at every
            width so the count/actions never wrap to a full-width second row. */}
        <div className="flex items-center gap-2 flex-shrink-0 sm:gap-3 sm:justify-end">
          {badge}

          <span className="flex flex-shrink-0">{chevronButton}</span>
        </div>
      </div>

      {/* Content with real height animation; children unmount once collapsed */}
      <CollapsibleRegion
        open={isExpanded}
        contentClassName="px-4 pb-4 pt-3 border-t border-themed-secondary"
      >
        {children}
      </CollapsibleRegion>
    </div>
  );
};
