import React, { useRef, useEffect, useMemo, memo, useState } from 'react';
import { Chart, ChartConfiguration, ChartDataset, registerables } from 'chart.js';

// Register Chart.js components
Chart.register(...registerables);

interface SparklineProps {
  /** Array of actual data points to display */
  data: number[];
  /** Array of predicted future data points (shown in different color) */
  predictedData?: number[];
  /** Color for the actual data line and fill (CSS color string) */
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
 * Supports showing predicted future values in a faded style
 */
const Sparkline: React.FC<SparklineProps> = memo(({
  data,
  predictedData,
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
  const [themeVersion, setThemeVersion] = useState(0);

  // Listen for theme changes to re-resolve colors
  useEffect(() => {
    const handleThemeChange = () => {
      setThemeVersion(v => v + 1);
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

  // Resolve predicted color (use theme muted/secondary color)
  const resolvedPredictedColor = useMemo(() => {
    return resolveCssVar('var(--theme-text-muted)');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [themeVersion]);

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
        b: parseInt(hex.slice(4, 6), 16),
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
        fill: `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, 0.2)`,
      };
    }
    return {
      solid: resolvedColor,
      transparent: 'transparent',
      fill: resolvedColor,
    };
  }, [resolvedColor]);

  // Create gradient color object for predicted data (more faded)
  const predictedGradientColor = useMemo(() => {
    const rgb = parseColorToRgb(resolvedPredictedColor);
    if (rgb) {
      return {
        solid: `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, 0.6)`,
        transparent: `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, 0)`,
        fill: `rgba(${rgb.r}, ${rgb.g}, ${rgb.b}, 0.1)`,
      };
    }
    return {
      solid: resolvedPredictedColor,
      transparent: 'transparent',
      fill: resolvedPredictedColor,
    };
  }, [resolvedPredictedColor]);

  useEffect(() => {
    if (!canvasRef.current || data.length === 0) return;

    const ctx = canvasRef.current.getContext('2d');
    if (!ctx) return;

    // Destroy existing chart
    if (chartRef.current) {
      chartRef.current.destroy();
      chartRef.current = null;
    }

    // Create gradient for area fill (actual data)
    const gradient = ctx.createLinearGradient(0, 0, 0, height);
    gradient.addColorStop(0, gradientColor.fill);
    gradient.addColorStop(1, gradientColor.transparent);

    // Create gradient for predicted area fill
    const predictedGradient = ctx.createLinearGradient(0, 0, 0, height);
    predictedGradient.addColorStop(0, predictedGradientColor.fill);
    predictedGradient.addColorStop(1, predictedGradientColor.transparent);

    // Combine actual and predicted data for calculating min/max
    const allData = [...data, ...(predictedData || [])];
    const minVal = Math.min(...allData);
    const maxVal = Math.max(...allData);

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

    // Create labels for all data points (actual + predicted)
    const totalPoints = data.length + (predictedData?.length || 0);
    const labels = Array.from({ length: totalPoints }, (_, i) => i.toString());

    // Build datasets
    const datasets: ChartDataset<'line', (number | null)[]>[] = [];

    // Actual data dataset - use null for predicted portion
    const actualDataWithNulls: (number | null)[] = [
      ...data,
      ...(predictedData ? Array(predictedData.length).fill(null) : []),
    ];
    
    datasets.push({
      data: actualDataWithNulls,
      borderColor: gradientColor.solid,
      borderWidth: 2,
      backgroundColor: showArea ? gradient : 'transparent',
      fill: showArea,
      tension: 0.4,
      pointRadius: 0,
      pointHoverRadius: 0,
      spanGaps: false,
    });

    // Predicted data dataset - include last actual point for smooth connection
    if (predictedData && predictedData.length > 0) {
      const predictedDataWithConnection: (number | null)[] = [
        ...Array(data.length - 1).fill(null),
        data[data.length - 1], // Last actual point for connection
        ...predictedData,
      ];

      datasets.push({
        data: predictedDataWithConnection,
        borderColor: predictedGradientColor.solid,
        borderWidth: 2,
        borderDash: [4, 4], // Dashed line for predicted
        backgroundColor: showArea ? predictedGradient : 'transparent',
        fill: showArea,
        tension: 0.4,
        pointRadius: 0,
        pointHoverRadius: 0,
        spanGaps: false,
      });
    }

    const config: ChartConfiguration<'line', (number | null)[], string> = {
      type: 'line',
      data: {
        labels,
        datasets,
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
  }, [data, predictedData, gradientColor, predictedGradientColor, height, showArea, shouldAnimate]);

  // Don't render if no data
  if (data.length === 0) {
    return null;
  }

  return (
    <div
      className={`sparkline-container ${className}`}
      style={{ height }}
      role="img"
      aria-label={ariaLabel || `Sparkline chart showing ${data.length} data points${predictedData ? ` and ${predictedData.length} predicted points` : ''}`}
    >
      <canvas ref={canvasRef} />
    </div>
  );
});

Sparkline.displayName = 'Sparkline';

export default Sparkline;
