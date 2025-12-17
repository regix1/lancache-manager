import React, { useRef, useEffect, useMemo, memo, useState } from 'react';
import { Chart, ChartConfiguration, registerables } from 'chart.js';

// Register Chart.js components
Chart.register(...registerables);

// Helper to compare arrays for equality (avoids unnecessary re-renders)
const arraysEqual = (a: number[], b: number[]): boolean => {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) return false;
  }
  return true;
};

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
 * A minimal sparkline chart component using Chart.js
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
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);
  const prevDataRef = useRef<number[]>([]);
  const [hasAnimated, setHasAnimated] = useState(false);

  // Check for reduced motion preference
  const prefersReducedMotion = useMemo(() => {
    if (typeof window === 'undefined') return true;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  // Only animate on first render, not on data updates
  const shouldAnimate = animated && !prefersReducedMotion && !hasAnimated;

  // Resolve CSS variable to actual color value
  const resolvedColor = useMemo(() => {
    if (color.startsWith('var(')) {
      // Extract the CSS variable name
      const varMatch = color.match(/var\((--[^)]+)\)/);
      if (varMatch && typeof document !== 'undefined') {
        const computedStyle = getComputedStyle(document.documentElement);
        const resolved = computedStyle.getPropertyValue(varMatch[1]).trim();
        if (resolved) return resolved;
        // Try to get the primary theme color as fallback
        return computedStyle.getPropertyValue('--theme-primary').trim() || color;
      }
    }
    return color;
  }, [color]);

  // Parse the color to create gradient
  const gradientColor = useMemo(() => {
    // Extract RGB values from the color
    if (resolvedColor.startsWith('rgba') || resolvedColor.startsWith('rgb')) {
      const match = resolvedColor.match(/[\d.]+/g);
      if (match && match.length >= 3) {
        const [r, g, b] = match;
        return {
          solid: `rgba(${r}, ${g}, ${b}, 1)`,
          transparent: `rgba(${r}, ${g}, ${b}, 0)`,
          fill: `rgba(${r}, ${g}, ${b}, 0.2)`,
        };
      }
    }
    // For hex colors, convert to RGB
    if (resolvedColor.startsWith('#')) {
      const hex = resolvedColor.slice(1);
      const r = parseInt(hex.slice(0, 2), 16);
      const g = parseInt(hex.slice(2, 4), 16);
      const b = parseInt(hex.slice(4, 6), 16);
      return {
        solid: `rgba(${r}, ${g}, ${b}, 1)`,
        transparent: `rgba(${r}, ${g}, ${b}, 0)`,
        fill: `rgba(${r}, ${g}, ${b}, 0.2)`,
      };
    }
    // Fallback - try to parse as named color or use as-is
    // For CSS variables that resolved to named colors, we'll use a generic approach
    return {
      solid: resolvedColor,
      transparent: 'transparent',
      fill: resolvedColor, // Will use opacity via CSS
    };
  }, [resolvedColor]);

  useEffect(() => {
    if (!canvasRef.current || data.length === 0) return;

    // Skip update if data hasn't actually changed (prevents jumping)
    if (chartRef.current && arraysEqual(prevDataRef.current, data)) {
      return;
    }
    prevDataRef.current = [...data];

    const ctx = canvasRef.current.getContext('2d');
    if (!ctx) return;

    // Destroy existing chart
    if (chartRef.current) {
      chartRef.current.destroy();
    }

    // Create gradient for area fill
    const gradient = ctx.createLinearGradient(0, 0, 0, height);
    gradient.addColorStop(0, gradientColor.fill);
    gradient.addColorStop(1, gradientColor.transparent);

    // Calculate stable min/max that handles edge cases
    const minVal = Math.min(...data);
    const maxVal = Math.max(...data);

    // Handle flat line case (all values equal) or all zeros
    let yMin: number;
    let yMax: number;

    if (minVal === maxVal) {
      // Flat line - create artificial range around the value
      if (minVal === 0) {
        // All zeros - show line in middle
        yMin = -1;
        yMax = 1;
      } else {
        // Non-zero flat line - 10% padding
        yMin = minVal * 0.9;
        yMax = maxVal * 1.1;
      }
    } else {
      // Normal case - add 10% padding
      const range = maxVal - minVal;
      yMin = minVal - range * 0.1;
      yMax = maxVal + range * 0.1;
    }

    const config: ChartConfiguration = {
      type: 'line',
      data: {
        labels: data.map((_, i) => i.toString()),
        datasets: [
          {
            data: data,
            borderColor: gradientColor.solid,
            borderWidth: 2,
            backgroundColor: showArea ? gradient : 'transparent',
            fill: showArea,
            tension: 0.4, // Smooth curves
            pointRadius: 0, // No data points
            pointHoverRadius: 0,
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: shouldAnimate
          ? {
              duration: 800,
              easing: 'easeOutQuart',
              onComplete: () => setHasAnimated(true),
            }
          : false,
        plugins: {
          legend: { display: false },
          tooltip: { enabled: false },
        },
        scales: {
          x: { display: false },
          y: {
            display: false,
            min: yMin,
            max: yMax,
          },
        },
        elements: {
          line: {
            borderCapStyle: 'round',
            borderJoinStyle: 'round',
          },
        },
        interaction: {
          mode: 'index',
          intersect: false,
        },
      },
    };

    chartRef.current = new Chart(ctx, config);

    return () => {
      if (chartRef.current) {
        chartRef.current.destroy();
        chartRef.current = null;
      }
    };
  }, [data, gradientColor, height, showArea, shouldAnimate]);

  // Don't render if no data
  if (data.length === 0) {
    return null;
  }

  return (
    <div
      className={`sparkline-container ${className}`}
      style={{ height }}
      role="img"
      aria-label={ariaLabel || `Sparkline chart showing ${data.length} data points`}
    >
      <canvas ref={canvasRef} />
    </div>
  );
});

Sparkline.displayName = 'Sparkline';

export default Sparkline;
