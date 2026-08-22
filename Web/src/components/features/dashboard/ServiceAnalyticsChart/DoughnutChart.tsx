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
import { useTranslation } from 'react-i18next';
import { formatBytes, formatCount } from '@utils/formatters';
import type { DoughnutChartProps, GameSliceExtra } from './types';
import { getThemeColor, getThemeRadius, useThemeRevision } from './chartTheme';

// Register only what we need (tree shaking)
ChartJS.register(ArcElement, Tooltip, Legend);

const DoughnutChart: React.FC<DoughnutChartProps> = React.memo(
  ({ labels, datasets, total, centerLabel, gameSliceExtras, ariaLabel }) => {
    const { t } = useTranslation();
    const themeRevision = useThemeRevision();
    // Prepare chart data with stable reference. The slice border keeps the color the
    // dataset already carries (--theme-chart-border), which every theme pins to its own
    // card background, so the separators disappear into the card the donut sits on.
    const chartData: ChartData<'doughnut'> = useMemo(() => {
      void themeRevision;
      const sliceRadius = getThemeRadius('--theme-border-radius-sm');
      return {
        labels,
        datasets: datasets.map((ds) => ({
          data: ds.data,
          backgroundColor: ds.backgroundColor,
          borderColor: ds.borderColor,
          borderWidth: 2,
          borderRadius: sliceRadius,
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
      // Same edge token the compare tab's DOM tooltip takes through `.tooltip-edge`,
      // so both tooltips in this panel keep one border color on every theme.
      const tooltipBorder = getThemeColor('--theme-card-border');
      const swatchRadius = getThemeRadius('--theme-border-radius-sm');

      return {
        responsive: true,
        maintainAspectRatio: false,
        cutout: '64%',
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
            boxWidth: 12,
            boxHeight: 12,
            boxPadding: 6,
            callbacks: {
              labelColor(item) {
                const style = item.chart
                  .getDatasetMeta(item.datasetIndex)
                  .controller.getStyle(item.dataIndex, false);
                return {
                  borderColor: style.borderColor,
                  backgroundColor: style.backgroundColor,
                  borderWidth: style.borderWidth,
                  borderDash: style.borderDash,
                  borderDashOffset: style.borderDashOffset,
                  borderRadius: swatchRadius
                };
              },
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
                lines.push(
                  t('dashboard.serviceAnalytics.games.files', {
                    count: formatCount(extra.cacheFiles)
                  })
                );
                if (extra.service !== 'mixed') {
                  lines.push(
                    t('dashboard.serviceAnalytics.games.service', { service: extra.service })
                  );
                }
                return lines;
              }
            }
          }
        }
      };
    }, [total, gameSliceExtras, t, themeRevision]);

    return (
      <div className="chart-wrapper" role="img" aria-label={ariaLabel}>
        <Doughnut
          data={chartData}
          options={options}
          datasetIdKey="id" // Critical: tells react-chartjs-2 how to track datasets
        />
        <div className="chart-center">
          <div className="chart-center-value">{formatBytes(total)}</div>
          <div className="chart-center-label caps-label caps-label--wide">{centerLabel}</div>
        </div>
      </div>
    );
  }
);

DoughnutChart.displayName = 'DoughnutChart';

export default DoughnutChart;
