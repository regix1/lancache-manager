import React, { useRef, useEffect, useMemo, memo, useState } from 'react';
import { Chart, ChartConfiguration, registerables } from 'chart.js';

// Register Chart.js components
Chart.register(...registerables);

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
      const varMatch = color.match(/var\((--[^)]+)\)/);
      if (varMatch && typeof document !== 'undefined') {
        const computedStyle = getComputedStyle(document.documentElement);
        const resolved = computedStyle.getPropertyValue(varMatch[1]).trim();
        if (resolved) return resolved;
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
    return {
      solid: resolvedColor,
      transparent: 'transparent',
      fill: resolvedColor,
    };
  }, [resolvedColor]);

  useEffect(() => {
    if (!canvasRef.current || data.length === 0) return;

    const ctx = canvasRef.current.getContext('2d');
    if (!ctx) return;

    // Destroy existing chart
    if (chartRef.current) {
      chartRef.current.destroy();
      chartRef.current = null;
    }

    // Create gradient for area fill
    const gradient = ctx.createLinearGradient(0, 0, 0, height);
    gradient.addColorStop(0, gradientColor.fill);
    gradient.addColorStop(1, gradientColor.transparent);

    // Calculate stable min/max
    const minVal = Math.min(...data);
    const maxVal = Math.max(...data);

    let yMin: number;
    let yMax: number;

    if (minVal === maxVal) {
      if (minVal === 0) {
        yMin = -1;
        yMax = 1;
      } else {
        yMin = minVal * 0.9;
        yMax = maxVal * 1.1;
      }
    } else {
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
            tension: 0.4,
            pointRadius: 0,
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
