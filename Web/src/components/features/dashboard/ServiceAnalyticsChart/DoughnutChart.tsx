import React, { useMemo } from 'react';
import { Doughnut } from 'react-chartjs-2';
import { Chart as ChartJS, ArcElement, Tooltip, Legend, ChartOptions, ChartData } from 'chart.js';
import { formatBytes } from '@utils/formatters';
import type { DoughnutChartProps } from './types';

// Register only what we need (tree shaking)
ChartJS.register(ArcElement, Tooltip, Legend);

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
          hoverOffset: ds.hoverOffset ?? 8,
        })),
      }),
      [labels, datasets]
    );

    // Chart options with total baked in for tooltip callback
    const options: ChartOptions<'doughnut'> = useMemo(
      () => ({
        responsive: true,
        maintainAspectRatio: true,
        aspectRatio: 1,
        cutout: '70%',
        radius: '90%',
        layout: {
          padding: 10,
        },
        elements: {
          arc: {
            // Ensure tiny slices are still visible.
            // Note: minAngle is a valid Chart.js runtime option but not in TS types
            minAngle: 2,
          } as any,
        },
        animation: {
          animateRotate: true,
          animateScale: false,
          duration: 600,
          easing: 'easeOutQuart',
        },
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: 'rgba(0, 0, 0, 0.9)',
            titleColor: '#ffffff',
            bodyColor: '#a0aec0',
            borderColor: 'rgba(255, 255, 255, 0.1)',
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
              },
            },
          },
        },
      }),
      [total]
    );

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
