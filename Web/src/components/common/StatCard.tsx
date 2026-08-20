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
  | 'teal'
  | 'red';

interface StatCardProps {
  title: string;
  value: string | number;
  subtitle?: string;
  badge?: React.ReactNode;
  tone?: 'warning';
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
  teal: 'var(--theme-icon-teal)',
  red: 'var(--theme-icon-red)'
};

const StatCard: React.FC<StatCardProps> = ({
  title,
  value,
  subtitle,
  badge,
  tone,
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
      // The container corner, same as the widget cards. The radius utilities are mapped to the
      // theme tokens in styles/utilities/responsive.css, not to Tailwind's pixel scale.
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
      classes.push('themed-card', 'hover:shadow-lg');
      if (tone === 'warning') {
        classes.push('themed-card--warning');
      }
    }

    return classes.join(' ');
  }, [glassmorphism, tone]);

  const cardContent = (
    <div className={cardClasses} data-stat-card={title.toLowerCase().replace(/\s+/g, '')}>
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          {/* min-h-6 (1.5rem) reserves the HelpPopover trigger's height (p-1 + w-4 h-4 icon)
              whether or not THIS card has a tooltip, so cards with and without one still line
              their value/sparkline up in shared grids like .stat-cards-3col. */}
          <div className="flex items-center gap-1 min-h-6">
            <p className="dash-card-title inline-block transition-colors">{title}</p>
            {tooltip && <HelpPopover width={260}>{tooltip}</HelpPopover>}
          </div>

          {/* Main value with optional animation */}
          <div className="flex items-baseline gap-2 mt-1">
            {loading ? (
              <div className="stat-card-skeleton-value skeleton-shimmer" />
            ) : animateValue ? (
              <AnimatedValue value={value} className="text-2xl font-bold transition duration-300" />
            ) : (
              <p className="text-2xl font-bold transition duration-300 text-[var(--theme-text-primary)]">
                {value}
              </p>
            )}
          </div>
        </div>
        <div className="stat-card-icon p-2.5 rounded-lg flex-shrink-0" data-color={color}>
          <Icon className="w-5 h-5 text-[var(--theme-button-text)]" />
        </div>
      </div>

      {loading ? (
        <div className="stat-card-skeleton-subtitle skeleton-shimmer mt-1" />
      ) : (
        <>
          {subtitle ? <p className="text-xs text-themed-secondary mt-1">{subtitle}</p> : null}
          {badge ? <div className="mt-1.5">{badge}</div> : null}
        </>
      )}

      {/* Sparkline or placeholder for consistent card height - mt-auto pushes to bottom */}
      <div className="mt-auto">
        {loading ? (
          <div className="stat-card-skeleton-sparkline skeleton-shimmer h-8 mt-2" />
        ) : !Array.isArray(sparklineData) ? (
          // Empty spacer to maintain consistent card height when the card has no sparkline at all
          <div className="sparkline-placeholder" />
        ) : sparklineData.length > 1 ? (
          <Sparkline
            data={sparklineData}
            color={resolvedSparklineColor}
            showArea={true}
            animated={true}
            ariaLabel={t('common.statCard.sparklineAria', { title, count: sparklineData.length })}
          />
        ) : (
          // A line needs two points and pointRadius is 0, so a single point paints nothing. Say
          // which case it is rather than padding out a flat line that reads as measured.
          <div className="sparkline-placeholder">
            {sparklineData.length === 0
              ? t('common.statCard.noDataInRange')
              : t('common.statCard.notEnoughToChart')}
          </div>
        )}
      </div>
    </div>
  );

  return cardContent;
};

export default React.memo(StatCard);
