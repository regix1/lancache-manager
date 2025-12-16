import React from 'react';
import { ChevronDown, ChevronUp, type LucideIcon } from 'lucide-react';

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
      className="rounded-lg border overflow-hidden"
      style={{
        backgroundColor: 'var(--theme-bg-elevated)',
        borderColor: 'var(--theme-border-secondary)'
      }}
    >
      <button
        onClick={onToggle}
        className="w-full px-4 py-3 flex items-center justify-between text-left transition-colors hover:bg-themed-tertiary"
      >
        <div className="flex items-center gap-2">
          {Icon && (
            <Icon
              className="w-5 h-5 flex-shrink-0"
              style={{ color: iconColor }}
            />
          )}
          <span className="font-medium text-themed-primary">{title}</span>
          {count !== undefined && (
            <span
              className="px-2 py-0.5 text-xs rounded-full font-medium"
              style={{
                backgroundColor: 'var(--theme-accent-muted)',
                color: 'var(--theme-accent)'
              }}
            >
              {count}
            </span>
          )}
          {badge}
        </div>
        <div className="flex items-center gap-2">
          {isExpanded ? (
            <ChevronUp className="w-5 h-5 text-themed-muted transition-transform" />
          ) : (
            <ChevronDown className="w-5 h-5 text-themed-muted transition-transform" />
          )}
        </div>
      </button>

      <div
        className="overflow-hidden transition-all duration-300 ease-in-out"
        style={{
          maxHeight: isExpanded ? '5000px' : '0',
          opacity: isExpanded ? 1 : 0
        }}
      >
        <div
          className="px-4 pb-4 pt-2"
          style={{ borderTop: '1px solid var(--theme-border-secondary)' }}
        >
          {children}
        </div>
      </div>
    </div>
  );
};

export default AccordionSection;
