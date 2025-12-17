// StatCard.tsx - Enhanced component with glassmorphism, sparklines, and animations
import React, { useMemo } from 'react';
import { type LucideIcon, TrendingUp, TrendingDown, Minus } from 'lucide-react';
import { Tooltip } from '@components/ui/Tooltip';
import Sparkline from '@components/features/dashboard/components/Sparkline';
import AnimatedValue from '@components/features/dashboard/components/AnimatedValue';

export type StatCardColor = 'blue' | 'green' | 'emerald' | 'purple' | 'indigo' | 'orange' | 'yellow' | 'cyan' | 'red';

interface StatCardProps {
  title: string;
  value: string | number;
  subtitle?: string;
  icon: LucideIcon;
  color: StatCardColor;
  tooltip?: React.ReactNode;
  // NEW: Sparkline props
  sparklineData?: number[];
  sparklineColor?: string;
  // NEW: Trend props
  trend?: 'up' | 'down' | 'stable';
  percentChange?: number;
  // NEW: Animation props
  animateValue?: boolean;
  // NEW: Glassmorphism
  glassmorphism?: boolean;
  // NEW: Stagger index for entrance animation
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
  tooltip,
  sparklineData,
  sparklineColor,
  trend,
  percentChange,
  animateValue = false,
  glassmorphism = false,
  staggerIndex,
}) => {
  // Check if tooltips are disabled globally
  const tooltipsDisabled =
    document.documentElement.getAttribute('data-disable-tooltips') === 'true';
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
    const classes = ['rounded-lg', 'p-4', 'border', 'transition-all', 'relative', 'group'];

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
      className={cardClasses}
      style={{
        backgroundColor: glassmorphism ? undefined : 'var(--theme-card-bg)',
        borderColor: glassmorphism ? undefined : 'var(--theme-card-border)',
        cursor: showTooltipIndicators ? 'help' : 'default'
      }}
      data-stat-card={title.toLowerCase().replace(/\s+/g, '')}
    >
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <p
            className="text-sm font-medium inline-block transition-colors"
            style={{
              color: 'var(--theme-text-muted)'
            }}
          >
            {title}
          </p>

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
                className="text-2xl font-bold transition-all duration-300"
                style={{ color: 'var(--theme-text-primary)' }}
              >
                {value}
              </p>
            )}

            {/* Trend indicator - only show when there's a meaningful change (>0.05%) and not extreme (<=500%) */}
            {TrendIcon && percentChange !== undefined && Math.abs(percentChange) > 0.05 && Math.abs(percentChange) <= 500 && (
              <div className={`flex items-center gap-0.5 text-xs font-medium ${trendClass}`}>
                <TrendIcon className="w-3 h-3" />
                <span>{Math.abs(percentChange).toFixed(1)}%</span>
              </div>
            )}
          </div>

          {subtitle && (
            <p className="text-xs mt-1" style={{ color: 'var(--theme-text-secondary)' }}>
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
          <Icon className="w-6 h-6" style={{ color: 'var(--theme-button-text)' }} />
        </div>
      </div>

      {/* Sparkline or placeholder for consistent card height */}
      {sparklineData && sparklineData.length >= 1 ? (
        <Sparkline
          data={sparklineData.length === 1 ? [sparklineData[0], sparklineData[0]] : sparklineData}
          color={resolvedSparklineColor}
          height={32}
          showArea={true}
          animated={true}
          ariaLabel={`${title} trend over the last ${sparklineData.length} data points`}
        />
      ) : (
        /* Empty spacer to maintain consistent card height when no sparkline */
        <div className="sparkline-placeholder" style={{ height: '32px', marginTop: '8px' }} />
      )}
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
