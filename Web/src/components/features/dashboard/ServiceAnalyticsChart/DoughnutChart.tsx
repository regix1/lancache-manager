import React, { useEffect, useMemo, useState } from 'react';
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
import { formatBytes, formatCount } from '@utils/formatters';
import type { DoughnutChartProps, GameSliceExtra } from './types';

// Register only what we need (tree shaking)
ChartJS.register(ArcElement, Tooltip, Legend);

function getThemeColor(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function useThemeRevision(): number {
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const updateRevision = () => setRevision((current) => current + 1);
    window.addEventListener('themechange', updateRevision);
    return () => window.removeEventListener('themechange', updateRevision);
  }, []);

  return revision;
}

const DoughnutChart: React.FC<DoughnutChartProps> = React.memo(
  ({ labels, datasets, total, centerLabel, gameSliceExtras }) => {
    const themeRevision = useThemeRevision();
    // Prepare chart data with stable reference. The slice border color is read
    // from the new flat `.chart-wrapper` background (var(--theme-bg-secondary))
    // so the donut visually sits in its disc with no halo gradient bleed.
    const chartData: ChartData<'doughnut'> = useMemo(() => {
      void themeRevision;
      const wrapperBg = getThemeColor('--theme-bg-secondary');
      return {
        labels,
        datasets: datasets.map((ds) => ({
          data: ds.data,
          backgroundColor: ds.backgroundColor,
          borderColor: wrapperBg,
          borderWidth: 2,
          borderRadius: ds.borderRadius ?? 4,
          spacing: ds.spacing ?? 2,
          hoverOffset: ds.hoverOffset ?? 8
        }))
      };
    }, [labels, datasets, themeRevision]);

    // Chart options with total baked in for tooltip callback
    const options: ChartOptions<'doughnut'> = useMemo(() => {
      void themeRevision;
      // Resolve tooltip colors from CSS custom properties (re-resolves on theme change)
      const tooltipBg = getThemeColor('--theme-card-bg');
      const tooltipTitle = getThemeColor('--theme-text-primary');
      const tooltipBody = getThemeColor('--theme-text-muted');
      const tooltipBorder = getThemeColor('--theme-border-secondary');

      return {
        responsive: true,
        maintainAspectRatio: false,
        cutout: '72%',
        radius: '98%',
        layout: {
          padding: 4
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
                const dataset = context.dataset as { originalData?: number[]; id?: string };
                const value = dataset.originalData?.[context.dataIndex] ?? (context.raw as number);
                const percentage = total > 0 ? ((value / total) * 100).toFixed(1) : '0';
                const baseLine = `${context.label}: ${formatBytes(value)} (${percentage}%)`;

                if (dataset.id !== 'games-distribution' || !gameSliceExtras) {
                  return baseLine;
                }

                const extra: GameSliceExtra | undefined = gameSliceExtras[context.dataIndex];
                if (!extra) return baseLine;

                const lines = [baseLine];
                lines.push(`Files: ${formatCount(extra.cacheFiles)}`);
                if (extra.service !== 'mixed') {
                  lines.push(`Service: ${extra.service}`);
                }
                return lines;
              }
            }
          }
        }
      };
    }, [total, gameSliceExtras, themeRevision]);

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
