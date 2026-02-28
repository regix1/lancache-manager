import React, { useRef, useEffect, useMemo, memo, useState } from 'react';
import { Chart, type ChartConfiguration, registerables } from 'chart.js';

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
const Sparkline: React.FC<SparklineProps> = memo(
  ({
    data,
    color = 'var(--theme-primary)',
    height = 32,
    showArea = true,
    animated = true,
    className = '',
    ariaLabel
  }) => {
    const canvasRef = useRef<HTMLCanvasElement>(null);
    const chartRef = useRef<Chart | null>(null);
    const [hasAnimated, setHasAnimated] = useState(false);
    const [themeVersion, setThemeVersion] = useState(0);

    // Listen for theme changes to re-resolve colors
    useEffect(() => {
      const handleThemeChange = () => {
        setThemeVersion((v) => v + 1);
      };

      window.addEventListener('themechange', handleThemeChange);
      return () => window.removeEventListener('themechange', handleThemeChange);
    }, []);

    // Check for reduced motion preference
    const prefersReducedMotion = useMemo(() => {
      if (typeof window === 'undefined') return true;
      return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    }, []);

    // Only animate on first render, not on data updates
    const shouldAnimate = animated && !prefersReducedMotion && !hasAnimated;

    // Helper to resolve CSS variable
    const resolveCssVar = (cssVar: string): string => {
      if (cssVar.startsWith('var(')) {
        const varMatch = cssVar.match(/var\((--[^)]+)\)/);
        if (varMatch && typeof document !== 'undefined') {
          const computedStyle = getComputedStyle(document.documentElement);
          const resolved = computedStyle.getPropertyValue(varMatch[1]).trim();
          if (resolved) return resolved;
          return computedStyle.getPropertyValue('--theme-primary').trim() || cssVar;
        }
      }
      return cssVar;
    };

    // Resolve CSS variable to actual color value (re-resolves when theme changes)
    const resolvedColor = useMemo(() => {
      return resolveCssVar(color);
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [color, themeVersion]);

    // Parse a color string to RGB components
    const parseColorToRgb = (colorStr: string): { r: number; g: number; b: number } | null => {
      if (colorStr.startsWith('rgba') || colorStr.startsWith('rgb')) {
        const match = colorStr.match(/[\d.]+/g);
        if (match && match.length >= 3) {
          return { r: parseInt(match[0]), g: parseInt(match[1]), b: parseInt(match[2]) };
        }
      }
      if (colorStr.startsWith('#')) {
        const hex = colorStr.slice(1);
        return {
          r: parseInt(hex.slice(0, 2), 16),
          g: parseInt(hex.slice(2, 4), 16),
          b: parseInt(hex.slice(4, 6), 16)
        };
      }
      return null;
    };

    // Create gradient color object from resolved color
    const gradientColor = useMemo(() => {
      const rgb = parseColorToRgb(resolvedColor);
      if (rgb) {
        return {
          solid: `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, 1)`,
          transparent: `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, 0)`,
          fill: `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, 0.2)`
        };
      }
      return {
        solid: resolvedColor,
        transparent: 'transparent',
        fill: resolvedColor
      };
    }, [resolvedColor]);

    useEffect(() => {
      if (!canvasRef.current || data.length === 0) return;

      const ctx = canvasRef.current.getContext('2d');
      if (!ctx) return;

      // Calculate min/max for Y axis
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

      // Create labels for data points
      const labels = Array.from({ length: data.length }, (_, i) => i.toString());

      // If chart exists, update it in-place instead of destroying
      if (chartRef.current) {
        const chart = chartRef.current;

        // Update data in-place
        chart.data.labels = labels;
        chart.data.datasets[0].data = data;

        // Update Y axis bounds
        if (chart.options.scales?.y) {
          chart.options.scales.y.min = yMin;
          chart.options.scales.y.max = yMax;
        }

        // Update without animation for smooth transition
        chart.update('none');
        return;
      }

      // Only create new chart if one doesn't exist
      // Create gradient for area fill
      const gradient = ctx.createLinearGradient(0, 0, 0, height);
      gradient.addColorStop(0, gradientColor.fill);
      gradient.addColorStop(1, gradientColor.transparent);

      const config: ChartConfiguration<'line', number[], string> = {
        type: 'line',
        data: {
          labels,
          datasets: [
            {
              data,
              borderColor: gradientColor.solid,
              borderWidth: 2,
              backgroundColor: showArea ? gradient : 'transparent',
              fill: showArea,
              tension: 0.4,
              pointRadius: 0,
              pointHoverRadius: 0
            }
          ]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          animation: shouldAnimate
            ? {
                duration: 800,
                easing: 'easeOutQuart',
                onComplete: () => setHasAnimated(true)
              }
            : false,
          plugins: {
            legend: { display: false },
            tooltip: { enabled: false }
          },
          scales: {
            x: { display: false },
            y: {
              display: false,
              min: yMin,
              max: yMax
            }
          },
          elements: {
            line: {
              borderCapStyle: 'round',
              borderJoinStyle: 'round'
            }
          },
          interaction: {
            mode: 'index',
            intersect: false
          }
        }
      };

      chartRef.current = new Chart(ctx, config);
    }, [data, gradientColor, height, showArea, shouldAnimate]);

    // Separate cleanup effect that only runs on unmount
    useEffect(() => {
      return () => {
        if (chartRef.current) {
          chartRef.current.destroy();
          chartRef.current = null;
        }
      };
    }, []);

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
  }
);

Sparkline.displayName = 'Sparkline';

export default Sparkline;
