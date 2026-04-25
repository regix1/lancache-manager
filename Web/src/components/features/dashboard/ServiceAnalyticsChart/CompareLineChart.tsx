import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Bar } from 'react-chartjs-2';
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Tooltip,
  Legend,
  type ChartData,
  type ChartOptions
} from 'chart.js';
import { formatBytes, formatPercent } from '@utils/formatters';
import type { ServiceStat } from '@/types';

ChartJS.register(CategoryScale, LinearScale, BarElement, Tooltip, Legend);

declare module 'chart.js' {
  interface TooltipPositionerMap {
    divergingBarEdge: (
      items: readonly { element: { x: number; y: number; base: number } }[]
    ) => { x: number; y: number; xAlign: 'left' | 'right'; yAlign: 'center' } | false;
  }
}

Tooltip.positioners.divergingBarEdge = function (items) {
  if (!items.length) return false;
  const el = items[0].element;
  const isRightSide = el.x >= el.base;
  return {
    x: el.x,
    y: el.y,
    xAlign: isRightSide ? 'left' : 'right',
    yAlign: 'center'
  };
};

interface CompareLineChartProps {
  serviceStats: ServiceStat[];
}

interface TooltipRow {
  background: string;
  border: string;
  text: string;
}

interface TooltipContent {
  title: string;
  rows: TooltipRow[];
}

const TOOLTIP_GAP = 12;

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

const CompareLineChart: React.FC<CompareLineChartProps> = React.memo(({ serviceStats }) => {
  const themeRevision = useThemeRevision();
  const tooltipElRef = useRef<HTMLDivElement | null>(null);
  const lastDataKeyRef = useRef<string>('');
  const [tooltipContent, setTooltipContent] = useState<TooltipContent>({ title: '', rows: [] });

  const services = useMemo(
    () =>
      [...serviceStats]
        .filter((service) => service.totalBytes > 0)
        .sort((a, b) => b.totalBytes - a.totalBytes)
        .slice(0, 10),
    [serviceStats]
  );

  const labels = useMemo(() => services.map((service) => service.service), [services]);

  const chartData: ChartData<'bar'> = useMemo(() => {
    void themeRevision;
    const hitColor = getThemeColor('--theme-chart-cache-hit');
    const missColor = getThemeColor('--theme-chart-cache-miss');
    const hitColorSoft = getThemeColor('--theme-success-subtle');
    const missColorSoft = getThemeColor('--theme-error-subtle');

    return {
      labels,
      datasets: [
        {
          label: 'Cache Hits',
          data: services.map((service) => service.totalCacheHitBytes),
          backgroundColor: hitColorSoft,
          hoverBackgroundColor: hitColor,
          borderColor: hitColor,
          borderWidth: 1.5,
          borderRadius: 6,
          borderSkipped: false,
          barPercentage: 0.78,
          categoryPercentage: 0.74
        },
        {
          label: 'Cache Misses',
          data: services.map((service) => -service.totalCacheMissBytes),
          backgroundColor: missColorSoft,
          hoverBackgroundColor: missColor,
          borderColor: missColor,
          borderWidth: 1.5,
          borderRadius: 6,
          borderSkipped: false,
          barPercentage: 0.78,
          categoryPercentage: 0.74
        }
      ]
    };
  }, [labels, services, themeRevision]);

  const options: ChartOptions<'bar'> = useMemo(() => {
    void themeRevision;
    const textColor = getThemeColor('--theme-chart-text');
    const mutedColor = getThemeColor('--theme-text-muted');
    const gridColor = getThemeColor('--theme-chart-grid');
    const zeroLineColor = getThemeColor('--theme-border-primary');
    const maxMagnitude = services.reduce(
      (max, service) => Math.max(max, service.totalCacheHitBytes, service.totalCacheMissBytes),
      0
    );

    return {
      responsive: true,
      maintainAspectRatio: false,
      indexAxis: 'y',
      interaction: {
        mode: 'nearest',
        intersect: true
      },
      hover: {
        mode: 'nearest',
        intersect: true
      },
      layout: {
        padding: {
          top: 4,
          right: 12,
          bottom: 4,
          left: 4
        }
      },
      scales: {
        x: {
          stacked: false,
          suggestedMin: -maxMagnitude,
          suggestedMax: maxMagnitude,
          grid: {
            color: gridColor,
            lineWidth: (ctx) => (ctx.tick.value === 0 ? 0 : 1)
          },
          border: {
            color: zeroLineColor,
            width: 1
          },
          ticks: {
            color: mutedColor,
            callback: (value) => formatBytes(Math.abs(Number(value)))
          }
        },
        y: {
          stacked: false,
          grid: {
            display: false
          },
          ticks: {
            color: mutedColor,
            autoSkip: false,
            callback: (_value, index) => {
              const label = labels[index] ?? '';
              return label.length > 14 ? `${label.slice(0, 14)}...` : label;
            }
          }
        }
      },
      plugins: {
        legend: {
          position: 'top',
          align: 'end',
          labels: {
            color: textColor,
            boxWidth: 10,
            boxHeight: 10,
            usePointStyle: true,
            padding: 14
          }
        },
        tooltip: {
          enabled: false,
          position: 'divergingBarEdge',
          animation: {
            duration: 280,
            easing: 'easeOutQuart'
          },
          external: ({ chart, tooltip }) => {
            const el = tooltipElRef.current;
            if (!el) return;

            if (tooltip.opacity === 0) {
              el.classList.remove('is-visible');
              lastDataKeyRef.current = '';
              return;
            }

            const canvasRect = chart.canvas.getBoundingClientRect();
            const anchorX = canvasRect.left + tooltip.caretX;
            const anchorY = canvasRect.top + tooltip.caretY;
            const isPositive = (tooltip.dataPoints?.[0]?.parsed?.x ?? 0) >= 0;

            el.style.transform = isPositive
              ? `translate3d(${anchorX + TOOLTIP_GAP}px, ${anchorY}px, 0) translateY(-50%)`
              : `translate3d(${anchorX - TOOLTIP_GAP}px, ${anchorY}px, 0) translate(-100%, -50%)`;
            el.classList.toggle('compare-chart-tooltip--arrow-left', isPositive);
            el.classList.toggle('compare-chart-tooltip--arrow-right', !isPositive);
            el.classList.add('is-visible');

            const dp = tooltip.dataPoints?.[0];
            const key = dp ? `${dp.datasetIndex}-${dp.dataIndex}` : '';
            if (key === lastDataKeyRef.current) return;
            lastDataKeyRef.current = key;

            const titleText = (tooltip.title ?? []).join(' ');
            const rows: TooltipRow[] = [];
            tooltip.body.forEach((entry, i) => {
              const colors = tooltip.labelColors[i];
              entry.lines.forEach((line: string) => {
                rows.push({
                  background: String(colors?.backgroundColor ?? 'transparent'),
                  border: String(colors?.borderColor ?? 'transparent'),
                  text: line
                });
              });
            });
            setTooltipContent({ title: titleText, rows });
          },
          callbacks: {
            title: (items) => {
              const item = items[0];
              if (!item) return '';
              return services[item.dataIndex]?.service ?? item.label ?? '';
            },
            label: (context) => {
              const service = services[context.dataIndex];
              if (!service) return '';
              const hitRate = service.totalBytes
                ? (service.totalCacheHitBytes / service.totalBytes) * 100
                : 0;
              const value = Math.abs(Number(context.raw));

              if (context.dataset.label === 'Cache Misses') {
                return `${context.dataset.label}: ${formatBytes(value)} (misses can be normal for first-time downloads)`;
              }

              return `${context.dataset.label}: ${formatBytes(value)} (${formatPercent(hitRate)} hit rate)`;
            }
          }
        }
      }
    };
  }, [labels, services, themeRevision]);

  return (
    <>
      <div className="compare-line-chart">
        <Bar data={chartData} options={options} />
      </div>
      {createPortal(
        <div ref={tooltipElRef} className="themed-card tooltip-edge compare-chart-tooltip">
          {tooltipContent.title && (
            <div className="compare-chart-tooltip__title">{tooltipContent.title}</div>
          )}
          {tooltipContent.rows.map((row, i) => (
            <div key={i} className="compare-chart-tooltip__row">
              <span
                className="compare-chart-tooltip__swatch"
                style={{ backgroundColor: row.background, borderColor: row.border }}
              />
              <span className="compare-chart-tooltip__text">{row.text}</span>
            </div>
          ))}
        </div>,
        document.body
      )}
    </>
  );
});

CompareLineChart.displayName = 'CompareLineChart';

export default CompareLineChart;
