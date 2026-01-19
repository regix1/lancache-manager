// StatCard.tsx - Enhanced component with glassmorphism, sparklines, and animations
import React, { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { type LucideIcon, TrendingUp, TrendingDown, Minus } from 'lucide-react';
import { HelpPopover } from '@components/ui/HelpPopover';
import Sparkline from '@components/features/dashboard/components/Sparkline';
import AnimatedValue from '@components/features/dashboard/components/AnimatedValue';

export type StatCardColor = 'blue' | 'green' | 'emerald' | 'purple' | 'indigo' | 'orange' | 'yellow' | 'cyan' | 'red';

interface StatCardProps {
  title: string;
  value: string | number;
  subtitle?: string;
  icon: LucideIcon;
  color: StatCardColor;
  // Sparkline props
  sparklineData?: number[];
  predictedSparklineData?: number[]; // Predicted future data points
  sparklineColor?: string;
  // Trend props
  trend?: 'up' | 'down' | 'stable';
  percentChange?: number;
  isAbsoluteChange?: boolean; // When true, show "pts" instead of "%"
  // Trend help content for HelpPopover (shown next to percentage)
  trendHelp?: React.ReactNode;
  // Tooltip shown next to title (help icon)
  tooltip?: React.ReactNode;
  // Animation props
  animateValue?: boolean;
  // Glassmorphism
  glassmorphism?: boolean;
  // Stagger index for entrance animation
  staggerIndex?: number;
}

// Color to sparkline color mapping using theme variables
const colorToSparklineColor: Record<StatCardColor, string> = {
  blue: 'var(--theme-icon-blue)',
  green: 'var(--theme-icon-green)',
  emerald: 'var(--theme-icon-emerald)',
  purple: 'var(--theme-icon-purple)',
  indigo: 'var(--theme-icon-indigo)',
  orange: 'var(--theme-icon-orange)',
  yellow: 'var(--theme-icon-yellow)',
  cyan: 'var(--theme-icon-cyan)',
  red: 'var(--theme-icon-red)',
};

const StatCard: React.FC<StatCardProps> = ({
  title,
  value,
  subtitle,
  icon: Icon,
  color,
  sparklineData,
  predictedSparklineData,
  sparklineColor,
  trend,
  percentChange,
  isAbsoluteChange,
  trendHelp,
  tooltip,
  animateValue = false,
  glassmorphism = false,
  staggerIndex,
}) => {
  const { t } = useTranslation();
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

  // Determine sparkline color
  const resolvedSparklineColor = sparklineColor || colorToSparklineColor[color];

  // Get trend icon and class
  const TrendIcon = useMemo(() => {
    if (!trend) return null;
    switch (trend) {
      case 'up':
        return TrendingUp;
      case 'down':
        return TrendingDown;
      default:
        return Minus;
    }
  }, [trend]);

  const trendClass = trend ? `trend-${trend}` : '';

  // Build class names - animation classes only added when staggerIndex is provided
  const cardClasses = useMemo(() => {
    const classes = ['rounded-lg', 'p-4', 'border', 'transition-all', 'relative', 'group', 'h-full', 'flex', 'flex-col'];

    if (glassmorphism) {
      classes.push('glass-card');
    } else {
      classes.push('hover:shadow-lg');
    }

    // Only add animation classes when staggerIndex is provided
    // Parent component controls when to stop providing staggerIndex (after initial animation)
    if (staggerIndex !== undefined) {
      classes.push('animate-card-entrance');
      classes.push(`stagger-${Math.min(staggerIndex + 1, 12)}`);
    }

    return classes.join(' ');
  }, [glassmorphism, staggerIndex]);

  const cardContent = (
    <div
      className={`${cardClasses} ${!glassmorphism ? 'bg-[var(--theme-card-bg)] border-[var(--theme-card-border)]' : ''}`}
      data-stat-card={title.toLowerCase().replace(/\s+/g, '')}
    >
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-1">
            <p
              className="text-sm font-medium inline-block transition-colors text-[var(--theme-text-muted)]"
            >
              {title}
            </p>
            {(tooltip || trendHelp) && (
              <HelpPopover width={260}>
                <div className="space-y-2">
                  {tooltip}
                  {tooltip && trendHelp && (
                    <div className="border-t border-themed-primary pt-2 mt-2" />
                  )}
                  {trendHelp}
                </div>
              </HelpPopover>
            )}
          </div>

          {/* Main value with optional animation */}
          <div className="flex items-baseline gap-2 mt-1">
            {animateValue ? (
              <AnimatedValue
                value={value}
                className="text-2xl font-bold transition-all duration-300"
                animate={true}
              />
            ) : (
              <p
                className="text-2xl font-bold transition-all duration-300 text-[var(--theme-text-primary)]"
              >
                {value}
              </p>
            )}

            {/* Trend indicator - only show when backend determines trend is up/down */}
            {trend && trend !== 'stable' && TrendIcon && percentChange !== undefined && (
              <div className={`flex items-center gap-0.5 text-xs font-medium ${trendClass}`}>
                <TrendIcon className="w-3 h-3" />
                <span>{Math.abs(percentChange).toFixed(1)}{isAbsoluteChange ? 'pts' : '%'}</span>
              </div>
            )}
          </div>

          {subtitle && (
            <p className="text-xs mt-1 text-[var(--theme-text-secondary)]">
              {subtitle}
            </p>
          )}
        </div>
        <div
          className="p-3 rounded-lg flex-shrink-0"
          style={{
            backgroundColor: getIconBackground(color)
          }}
        >
          <Icon className="w-6 h-6 text-[var(--theme-button-text)]" />
        </div>
      </div>

      {/* Sparkline or placeholder for consistent card height - mt-auto pushes to bottom */}
      <div className="mt-auto">
        {sparklineData && sparklineData.length >= 1 ? (
          <Sparkline
            data={sparklineData.length === 1 ? [sparklineData[0], sparklineData[0]] : sparklineData}
            predictedData={predictedSparklineData}
            color={resolvedSparklineColor}
            height={32}
            showArea={true}
            animated={true}
            ariaLabel={t('common.statCard.sparklineAria', { title, count: sparklineData.length })}
          />
        ) : (
          /* Empty spacer to maintain consistent card height when no sparkline */
          <div className="sparkline-placeholder h-8 mt-2" />
        )}
      </div>
    </div>
  );

  return cardContent;
};

export default StatCard;
