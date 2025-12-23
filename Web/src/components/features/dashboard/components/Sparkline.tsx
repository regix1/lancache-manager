import React, { useMemo, memo } from 'react';
import { AreaChart, Area, ResponsiveContainer, YAxis } from 'recharts';

interface SparklineProps {
  /** Array of data points to display */
  data: number[];
  /** Color for the line and fill (CSS color string) */
  color?: string;
  /** Height of the sparkline in pixels (default: 32) */
  height?: number;
  /** Whether to show area fill under the line (default: true) */
  showArea?: boolean;
  /** Whether to animate the sparkline on mount (default: true) */
  animated?: boolean;
  /** Additional CSS classes */
  className?: string;
  /** ARIA label for accessibility */
  ariaLabel?: string;
}

/**
 * A minimal sparkline chart component using Recharts
 * Displays a small inline chart with no axes, labels, or grid
 */
const Sparkline: React.FC<SparklineProps> = memo(({
  data,
  color = 'var(--theme-primary)',
  height = 32,
  showArea = true,
  animated = true,
  className = '',
  ariaLabel,
}) => {
  // Check for reduced motion preference
  const prefersReducedMotion = useMemo(() => {
    if (typeof window === 'undefined') return true;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  const shouldAnimate = animated && !prefersReducedMotion;

  // Resolve CSS variable to actual color value
  const resolvedColor = useMemo(() => {
    if (color.startsWith('var(')) {
      const varMatch = color.match(/var\((--[^)]+)\)/);
      if (varMatch && typeof document !== 'undefined') {
        const computedStyle = getComputedStyle(document.documentElement);
        const resolved = computedStyle.getPropertyValue(varMatch[1]).trim();
        if (resolved) return resolved;
        return computedStyle.getPropertyValue('--theme-primary').trim() || '#6366f1';
      }
    }
    return color;
  }, [color]);

  // Convert data array to Recharts format
  const chartData = useMemo(() => {
    return data.map((value, index) => ({
      index,
      value
    }));
  }, [data]);

  // Calculate domain for Y axis
  const yDomain = useMemo(() => {
    if (data.length === 0) return [0, 1];

    const minVal = Math.min(...data);
    const maxVal = Math.max(...data);

    if (minVal === maxVal) {
      if (minVal === 0) {
        return [-1, 1];
      }
      return [minVal * 0.9, maxVal * 1.1];
    }

    const range = maxVal - minVal;
    return [minVal - range * 0.1, maxVal + range * 0.1];
  }, [data]);

  // Don't render if no data
  if (data.length === 0) {
    return null;
  }

  return (
    <div
      className={`sparkline-container ${className}`}
      style={{ height, width: '100%' }}
      role="img"
      aria-label={ariaLabel || `Sparkline chart showing ${data.length} data points`}
    >
      <ResponsiveContainer width="100%" height="100%">
        <AreaChart data={chartData} margin={{ top: 2, right: 2, bottom: 2, left: 2 }}>
          <defs>
            <linearGradient id={`sparklineGradient-${resolvedColor.replace(/[^a-zA-Z0-9]/g, '')}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={resolvedColor} stopOpacity={0.3} />
              <stop offset="100%" stopColor={resolvedColor} stopOpacity={0} />
            </linearGradient>
          </defs>
          <YAxis domain={yDomain} hide />
          <Area
            type="monotone"
            dataKey="value"
            stroke={resolvedColor}
            strokeWidth={2}
            fill={showArea ? `url(#sparklineGradient-${resolvedColor.replace(/[^a-zA-Z0-9]/g, '')})` : 'transparent'}
            isAnimationActive={shouldAnimate}
            animationDuration={800}
            animationEasing="ease-out"
          />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
});

Sparkline.displayName = 'Sparkline';

export default Sparkline;
