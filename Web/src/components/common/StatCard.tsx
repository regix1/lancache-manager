// StatCard.tsx - Enhanced component with glassmorphism, sparklines, and animations
import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { type LucideIcon } from 'lucide-react';
import { HelpPopover } from '@components/ui/HelpPopover';
import Sparkline from '@components/features/dashboard/components/Sparkline';
import AnimatedValue from '@components/features/dashboard/components/AnimatedValue';

type StatCardColor =
  | 'blue'
  | 'green'
  | 'emerald'
  | 'purple'
  | 'indigo'
  | 'orange'
  | 'yellow'
  | 'cyan'
  | 'red';

interface StatCardProps {
  title: string;
  value: string | number;
  subtitle?: string;
  badge?: React.ReactNode;
  icon: LucideIcon;
  color: StatCardColor;
  // Sparkline props
  sparklineData?: number[];
  sparklineColor?: string;
  // Tooltip shown next to title (help icon)
  tooltip?: React.ReactNode;
  // Animation props
  animateValue?: boolean;
  // Glassmorphism
  glassmorphism?: boolean;
  // Loading skeleton
  loading?: boolean;
}

// Color → CSS variable mapping used for sparkline colors (icon backgrounds use CSS data-color selectors)
const statCardColorMap: Record<StatCardColor, string> = {
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

const StatCard: React.FC<StatCardProps> = ({
  title,
  value,
  subtitle,
  badge,
  icon: Icon,
  color,
  sparklineData,
  sparklineColor,
  tooltip,
  animateValue = false,
  glassmorphism = false,
  loading = false
}) => {
  const { t } = useTranslation();

  // Determine sparkline color
  const resolvedSparklineColor = sparklineColor || statCardColorMap[color];

  const cardClasses = useMemo(() => {
    const classes = [
      'rounded-lg',
      'p-4',
      'border',
      'transition-shadow',
      'duration-300',
      'relative',
      'group',
      'h-full',
      'flex',
      'flex-col'
    ];

    if (glassmorphism) {
      classes.push('glass-card');
    } else {
      classes.push('hover:shadow-lg');
    }

    return classes.join(' ');
  }, [glassmorphism]);

  const cardContent = (
    <div
      className={`${cardClasses} ${!glassmorphism ? 'bg-[var(--theme-card-bg)] border-[var(--theme-card-border)]' : ''}`}
      data-stat-card={title.toLowerCase().replace(/\s+/g, '')}
    >
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-1">
            <p className="text-sm font-medium inline-block transition-colors text-[var(--theme-text-muted)]">
              {title}
            </p>
            {tooltip && <HelpPopover width={260}>{tooltip}</HelpPopover>}
          </div>

          {/* Main value with optional animation */}
          <div className="flex items-baseline gap-2 mt-1">
            {loading ? (
              <div className="stat-card-skeleton-value" />
            ) : animateValue ? (
              <AnimatedValue
                value={value}
                className="text-2xl font-bold transition-all duration-300"
              />
            ) : (
              <p className="text-2xl font-bold transition-all duration-300 text-[var(--theme-text-primary)]">
                {value}
              </p>
            )}
          </div>

          {loading ? (
            <div className="stat-card-skeleton-subtitle mt-1" />
          ) : subtitle ? (
            <div className="flex items-center gap-1.5 mt-1">
              <p className="text-xs text-[var(--theme-text-secondary)]">{subtitle}</p>
              {badge}
            </div>
          ) : null}
        </div>
        <div className="stat-card-icon p-3 rounded-lg flex-shrink-0" data-color={color}>
          <Icon className="w-6 h-6 text-[var(--theme-button-text)]" />
        </div>
      </div>

      {/* Sparkline or placeholder for consistent card height - mt-auto pushes to bottom */}
      <div className="mt-auto">
        {loading ? (
          <div className="stat-card-skeleton-sparkline h-8 mt-2" />
        ) : Array.isArray(sparklineData) ? (
          <Sparkline
            data={
              sparklineData.length === 0
                ? [0, 0]
                : sparklineData.length === 1
                  ? [sparklineData[0], sparklineData[0]]
                  : sparklineData
            }
            color={resolvedSparklineColor}
            height={32}
            showArea={true}
            animated={true}
            ariaLabel={t('common.statCard.sparklineAria', { title, count: sparklineData.length })}
          />
        ) : (
          <>
            {/* Empty spacer to maintain consistent card height when no sparkline */}
            <div className="sparkline-placeholder h-8 mt-2" />
          </>
        )}
      </div>
    </div>
  );

  return cardContent;
};

export default React.memo(StatCard);
