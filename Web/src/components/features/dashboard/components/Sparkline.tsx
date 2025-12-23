import React, { useMemo, memo, useId } from 'react';

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
 * A minimal sparkline chart component using pure SVG
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
  const gradientId = useId();

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

  // Calculate SVG path
  const { linePath, areaPath, viewBox } = useMemo(() => {
    if (data.length === 0) {
      return { linePath: '', areaPath: '', viewBox: '0 0 100 100' };
    }

    const width = 100;
    const padding = 2;
    const effectiveHeight = height - padding * 2;

    const minVal = Math.min(...data);
    const maxVal = Math.max(...data);
    const range = maxVal - minVal || 1;

    // Normalize data to SVG coordinates
    const points = data.map((value, index) => {
      const x = (index / (data.length - 1)) * width;
      const y = padding + effectiveHeight - ((value - minVal) / range) * effectiveHeight;
      return { x, y };
    });

    // Create smooth curve using quadratic bezier
    let linePath = `M ${points[0].x},${points[0].y}`;

    for (let i = 1; i < points.length; i++) {
      const prev = points[i - 1];
      const curr = points[i];
      const cpx = (prev.x + curr.x) / 2;
      linePath += ` Q ${prev.x + (curr.x - prev.x) * 0.5},${prev.y} ${cpx},${(prev.y + curr.y) / 2}`;
    }

    // Final segment
    if (points.length > 1) {
      const last = points[points.length - 1];
      linePath += ` T ${last.x},${last.y}`;
    }

    // Create area path (same as line but closed at bottom)
    const areaPath = linePath +
      ` L ${points[points.length - 1].x},${height} L ${points[0].x},${height} Z`;

    return {
      linePath,
      areaPath,
      viewBox: `0 0 ${width} ${height}`
    };
  }, [data, height]);

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
      <svg
        viewBox={viewBox}
        preserveAspectRatio="none"
        style={{ width: '100%', height: '100%', overflow: 'visible' }}
      >
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={resolvedColor} stopOpacity={0.3} />
            <stop offset="100%" stopColor={resolvedColor} stopOpacity={0} />
          </linearGradient>
        </defs>

        {showArea && (
          <path
            d={areaPath}
            fill={`url(#${gradientId})`}
            style={{
              opacity: shouldAnimate ? 0 : 1,
              animation: shouldAnimate ? 'sparklineFadeIn 0.8s ease-out forwards' : 'none'
            }}
          />
        )}

        <path
          d={linePath}
          fill="none"
          stroke={resolvedColor}
          strokeWidth={2}
          strokeLinecap="round"
          strokeLinejoin="round"
          style={{
            strokeDasharray: shouldAnimate ? 1000 : 0,
            strokeDashoffset: shouldAnimate ? 1000 : 0,
            animation: shouldAnimate ? 'sparklineDrawIn 0.8s ease-out forwards' : 'none'
          }}
        />
      </svg>

      <style>{`
        @keyframes sparklineDrawIn {
          to {
            stroke-dashoffset: 0;
          }
        }
        @keyframes sparklineFadeIn {
          to {
            opacity: 1;
          }
        }
      `}</style>
    </div>
  );
});

Sparkline.displayName = 'Sparkline';

export default Sparkline;
