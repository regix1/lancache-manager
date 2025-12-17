import React, { useRef, useEffect, useMemo, memo } from 'react';
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
  color = 'rgba(59, 130, 246, 1)', // Default blue
  height = 32,
  showArea = true,
  animated = true,
  className = '',
  ariaLabel,
}) => {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const chartRef = useRef<Chart | null>(null);

  // Check for reduced motion preference
  const prefersReducedMotion = useMemo(() => {
    if (typeof window === 'undefined') return true;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  // Resolve CSS variable to actual color value
  const resolvedColor = useMemo(() => {
    if (color.startsWith('var(')) {
      // Extract the CSS variable name
      const varMatch = color.match(/var\((--[^)]+)\)/);
      if (varMatch && typeof document !== 'undefined') {
        const computedStyle = getComputedStyle(document.documentElement);
        return computedStyle.getPropertyValue(varMatch[1]).trim() || '#3b82f6';
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
    // Fallback - use the resolved color directly
    return {
      solid: resolvedColor,
      transparent: 'rgba(59, 130, 246, 0)',
      fill: 'rgba(59, 130, 246, 0.2)',
    };
  }, [resolvedColor]);

  useEffect(() => {
    if (!canvasRef.current || data.length === 0) return;

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
        animation: prefersReducedMotion || !animated
          ? false
          : {
              duration: 1000,
              easing: 'easeOutQuart',
            },
        plugins: {
          legend: { display: false },
          tooltip: { enabled: false },
        },
        scales: {
          x: { display: false },
          y: {
            display: false,
            // Add a little padding to prevent clipping
            min: Math.min(...data) * 0.9,
            max: Math.max(...data) * 1.1,
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
  }, [data, gradientColor, height, showArea, animated, prefersReducedMotion]);

  // Don't render if no data
  if (data.length === 0) {
    return null;
  }

  return (
    <div
      className={`sparkline-container ${animated && !prefersReducedMotion ? 'animate-sparkline' : ''} ${className}`}
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
