import React, { useMemo } from 'react';
import { Doughnut } from 'react-chartjs-2';
import {
  Chart as ChartJS,
  ArcElement,
  Tooltip,
  Legend,
  type ChartOptions,
  type ChartData,
  type ArcOptions
} from 'chart.js';
import { formatBytes } from '@utils/formatters';
import type { DoughnutChartProps } from './types';

// Register only what we need (tree shaking)
ChartJS.register(ArcElement, Tooltip, Legend);

/**
 * Reads a CSS custom property from the document root element.
 * Returns the trimmed value, or the provided fallback if the property is empty.
 */
function getCssVar(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

const DoughnutChart: React.FC<DoughnutChartProps> = React.memo(
  ({ labels, datasets, total, centerLabel }) => {
    // Prepare chart data with stable reference
    const chartData: ChartData<'doughnut'> = useMemo(
      () => ({
        labels,
        datasets: datasets.map((ds) => ({
          data: ds.data,
          backgroundColor: ds.backgroundColor,
          borderColor: ds.borderColor,
          borderWidth: ds.borderWidth,
          borderRadius: ds.borderRadius ?? 4,
          spacing: ds.spacing ?? 2,
          hoverOffset: ds.hoverOffset ?? 8
        }))
      }),
      [labels, datasets]
    );

    // Chart options with total baked in for tooltip callback
    const options: ChartOptions<'doughnut'> = useMemo(() => {
      // Resolve tooltip colors from CSS custom properties (re-resolves on theme change)
      const tooltipBg = getCssVar('--theme-card-bg', '#1e2938');
      const tooltipTitle = getCssVar('--theme-text-primary', '#ffffff');
      const tooltipBody = getCssVar('--theme-text-muted', '#9ca3af');
      const tooltipBorder = getCssVar('--theme-border-secondary', '#374151');

      return {
        responsive: true,
        maintainAspectRatio: true,
        aspectRatio: 1,
        cutout: '70%',
        radius: '90%',
        layout: {
          padding: 10
        },
        elements: {
          arc: {
            // Ensure tiny slices are still visible.
            // Note: minAngle is a valid Chart.js runtime option but not in TS types
            minAngle: 2
          } as ArcOptions & { minAngle?: number }
        },
        animation: {
          animateRotate: true,
          animateScale: false,
          duration: 600,
          easing: 'easeOutQuart'
        },
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: tooltipBg,
            titleColor: tooltipTitle,
            bodyColor: tooltipBody,
            borderColor: tooltipBorder,
            borderWidth: 1,
            cornerRadius: 10,
            padding: 14,
            displayColors: true,
            boxPadding: 6,
            callbacks: {
              label: (context) => {
                const dataset = context.dataset as { originalData?: number[] };
                const value = dataset.originalData?.[context.dataIndex] ?? (context.raw as number);
                const percentage = total > 0 ? ((value / total) * 100).toFixed(1) : '0';
                return `${context.label}: ${formatBytes(value)} (${percentage}%)`;
              }
            }
          }
        }
      };
    }, [total]);

    return (
      <div className="chart-wrapper">
        <Doughnut
          data={chartData}
          options={options}
          datasetIdKey="id" // Critical: tells react-chartjs-2 how to track datasets
        />
        <div className="chart-center">
          <div className="chart-center-value">{formatBytes(total)}</div>
          <div className="chart-center-label">{centerLabel}</div>
        </div>
      </div>
    );
  }
);

DoughnutChart.displayName = 'DoughnutChart';

export default DoughnutChart;
