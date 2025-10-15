// StatCard.tsx - Component without gradient backgrounds
import React from 'react';
import { type LucideIcon } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';

interface StatCardProps {
  title: string;
  value: string | number;
  subtitle?: string;
  icon: LucideIcon;
  color: 'blue' | 'green' | 'emerald' | 'purple' | 'indigo' | 'orange' | 'yellow' | 'cyan' | 'red';
  tooltip?: React.ReactNode;
}

const StatCard: React.FC<StatCardProps> = ({
  title,
  value,
  subtitle,
  icon: Icon,
  color,
  tooltip
}) => {
  // Check if tooltips are disabled globally
  const tooltipsDisabled = document.documentElement.getAttribute('data-disable-tooltips') === 'true';
  const showTooltipIndicators = tooltip && !tooltipsDisabled;

  // Map color names to CSS variables
  const getIconBackground = (color: string): string => {
    const colorMap: Record<string, string> = {
      blue: 'var(--theme-icon-blue)',
      green: 'var(--theme-icon-green)',
      emerald: 'var(--theme-icon-emerald)',
      purple: 'var(--theme-icon-purple)',
      indigo: 'var(--theme-icon-indigo)',
      orange: 'var(--theme-icon-orange)',
      yellow: 'var(--theme-icon-yellow)',
      cyan: 'var(--theme-icon-cyan)',
      red: 'var(--theme-icon-red)'
    };
    return colorMap[color] || colorMap.blue;
  };

  const cardContent = (
    <div
      className="rounded-lg p-4 border transition-all hover:shadow-lg relative group"
      style={{
        backgroundColor: 'var(--theme-card-bg)',
        borderColor: 'var(--theme-card-border)',
        cursor: showTooltipIndicators ? 'help' : 'default'
      }}
      data-stat-card={title.toLowerCase().replace(/\s+/g, '')}
    >
      <div className="flex items-start justify-between">
        <div className="flex-1">
          <p
            className="text-sm font-medium inline-block transition-colors"
            style={{
              color: showTooltipIndicators ? 'var(--theme-text-secondary)' : 'var(--theme-text-muted)',
              borderBottom: showTooltipIndicators ? '1px dotted currentColor' : 'none',
              paddingBottom: showTooltipIndicators ? '1px' : '0',
              opacity: showTooltipIndicators ? '0.9' : '1'
            }}
          >
            {title}
          </p>
          <p className="text-2xl font-bold mt-1 transition-all duration-300" style={{ color: 'var(--theme-text-primary)' }}>
            {value}
          </p>
          {subtitle && (
            <p className="text-xs mt-1" style={{ color: 'var(--theme-text-secondary)' }}>
              {subtitle}
            </p>
          )}
        </div>
        <div
          className="p-3 rounded-lg"
          style={{
            backgroundColor: getIconBackground(color)
          }}
        >
          <Icon className="w-6 h-6" style={{ color: 'var(--theme-button-text)' }} />
        </div>
      </div>
    </div>
  );

  if (tooltip) {
    return (
      <Tooltip content={tooltip} className="block w-full" position="top" strategy="overlay">
        {cardContent}
      </Tooltip>
    );
  }

  return cardContent;
};

export default StatCard;
