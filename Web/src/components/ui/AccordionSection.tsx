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
  return (
    <div
      className="group rounded-lg overflow-hidden transition-all duration-300"
      style={{
        backgroundColor: isExpanded
          ? 'var(--theme-bg-secondary)'
          : 'color-mix(in srgb, var(--theme-bg-secondary) 60%, transparent)',
        border: '1px solid',
        borderColor: isExpanded
          ? 'var(--theme-border-primary)'
          : 'var(--theme-border-secondary)',
        boxShadow: isExpanded
          ? '0 4px 16px color-mix(in srgb, var(--theme-text-primary) 15%, transparent), 0 1px 4px color-mix(in srgb, var(--theme-text-primary) 10%, transparent)'
          : '0 1px 3px color-mix(in srgb, var(--theme-text-primary) 8%, transparent)'
      }}
    >
      {/* Header Button */}
      <button
        onClick={onToggle}
        className="w-full px-4 py-3 flex items-center justify-between text-left transition-all duration-200 group/header"
        style={{
          background: isExpanded
            ? 'linear-gradient(135deg, color-mix(in srgb, var(--theme-accent) 8%, transparent) 0%, transparent 100%)'
            : 'transparent'
        }}
      >
        <div className="flex items-center gap-3">
          {/* Icon with animated background */}
          {Icon && (
            <div
              className="w-8 h-8 rounded-lg flex items-center justify-center transition-all duration-300"
              style={{
                backgroundColor: `color-mix(in srgb, ${iconColor} 15%, transparent)`,
                transform: isExpanded ? 'scale(1.05)' : 'scale(1)',
                boxShadow: isExpanded
                  ? `0 2px 8px color-mix(in srgb, ${iconColor} 25%, transparent)`
                  : 'none'
              }}
            >
              <Icon
                className="w-4 h-4 flex-shrink-0 transition-transform duration-300"
                style={{
                  color: iconColor,
                  transform: isExpanded ? 'scale(1.1)' : 'scale(1)'
                }}
              />
            </div>
          )}

          {/* Title */}
          <span
            className="font-semibold text-themed-primary transition-colors duration-200"
            style={{
              color: isExpanded ? 'var(--theme-text-primary)' : 'var(--theme-text-secondary)'
            }}
          >
            {title}
          </span>

          {/* Count Badge */}
          {count !== undefined && (
            <span
              className="px-2.5 py-1 text-xs rounded-full font-semibold tabular-nums transition-all duration-300"
              style={{
                backgroundColor: isExpanded
                  ? `color-mix(in srgb, ${iconColor} 20%, transparent)`
                  : 'var(--theme-bg-tertiary)',
                color: isExpanded ? iconColor : 'var(--theme-text-muted)',
                transform: isExpanded ? 'scale(1.05)' : 'scale(1)'
              }}
            >
              {count.toLocaleString()}
            </span>
          )}

          {badge}
        </div>

        {/* Chevron with rotation animation */}
        <div
          className="flex items-center justify-center w-8 h-8 rounded-lg transition-all duration-300"
          style={{
            backgroundColor: isExpanded
              ? 'color-mix(in srgb, var(--theme-accent) 10%, transparent)'
              : 'transparent'
          }}
        >
          <ChevronDown
            className="w-5 h-5 transition-all duration-300 ease-out"
            style={{
              color: isExpanded ? 'var(--theme-accent)' : 'var(--theme-text-muted)',
              transform: isExpanded ? 'rotate(180deg)' : 'rotate(0deg)'
            }}
          />
        </div>
      </button>

      {/* Content with smooth animation */}
      <div
        className="overflow-hidden transition-all duration-300 ease-out"
        style={{
          maxHeight: isExpanded ? '5000px' : '0',
          opacity: isExpanded ? 1 : 0,
          transform: isExpanded ? 'translateY(0)' : 'translateY(-8px)'
        }}
      >
        <div
          className="px-4 pb-4 pt-3"
          style={{
            borderTop: '1px solid var(--theme-border-secondary)',
            background: 'linear-gradient(180deg, color-mix(in srgb, var(--theme-bg-tertiary) 30%, transparent) 0%, transparent 100%)'
          }}
        >
          {children}
        </div>
      </div>
    </div>
  );
};

export default AccordionSection;
