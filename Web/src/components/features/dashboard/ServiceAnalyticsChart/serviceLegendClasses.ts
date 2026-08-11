import type { TFunction } from 'i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import { getServiceLegendClass } from '@utils/serviceColors';
import type { ChartData, TabId } from './types';

/**
 * Resolve which CSS color class to apply to a legend row, based on the active tab
 * (games tab uses indexed game palette, hit-ratio uses hit/miss colors,
 * everything else uses the per-service brand swatch).
 */
export function getLegendColorClass(label: string, index: number, activeTab: TabId): string {
  if (activeTab === 'games') {
    return `legend-color-game-${(index % 20) + 1}`;
  }

  if (activeTab === 'hit-ratio') {
    return label.toLowerCase().includes('miss')
      ? 'legend-color-cache-miss'
      : 'legend-color-cache-hit';
  }

  const normalizedLabel = label.toLowerCase().replace(/[^a-z0-9.]/g, '');
  return getServiceLegendClass(normalizedLabel);
}

/**
 * Strongly-typed footer-tile spec used by the bottom 3-up grid below the donut.
 * Local-only; consumers iterate the array returned from `getInsightCards`.
 */
interface InsightCardSpec {
  label: string;
  value: string;
  tone?: 'primary' | 'default';
}

/**
 * Pre-computed period-scoped totals fed into per-tab footer logic.
 * Mirrors the shape returned from `ServiceAnalyticsChart`'s `footerStats` selector.
 */
export interface FooterStats {
  totalBytes: number;
  hitRatio: number;
  missBytes: number;
  serviceCount: number;
  gameCount: number;
  largestGame: string;
  largestGameBytes: number;
  topServiceName: string;
  topServiceBytes: number;
  totalHitBytes: number;
}

/**
 * Build the 3 footer tiles for the active tab.
 *
 * Per-tab content:
 * - service:    Total Data | Services | Top Service (name + bytes)
 * - hit-ratio:  Total Data | Cache Hits (bytes) | Hit Rate
 * - bandwidth:  Bandwidth Saved (chartData.total) | Services | Avg Saved / Service
 * - misses:     Origin Pulls (miss bytes) | From Cache (hit bytes) | Miss %
 * - games:      Total on Disk | Games | Largest (game name + bytes)
 */
export function getInsightCards(
  activeTab: TabId,
  footerStats: FooterStats,
  chartData: ChartData,
  t: TFunction
): InsightCardSpec[] {
  if (activeTab === 'games') {
    const largestValue =
      footerStats.largestGame.length > 0
        ? `${footerStats.largestGame} - ${formatBytes(footerStats.largestGameBytes)}`
        : '-';
    return [
      {
        label: t('dashboard.serviceAnalytics.footer.totalDisk'),
        value: formatBytes(footerStats.totalBytes),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.gamesDetected'),
        value: String(footerStats.gameCount)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.largestGameValue'),
        value: largestValue
      }
    ];
  }

  if (activeTab === 'hit-ratio') {
    return [
      {
        label: t('dashboard.serviceAnalytics.footer.totalData'),
        value: formatBytes(footerStats.totalBytes),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.cacheHits'),
        value: formatBytes(footerStats.totalHitBytes)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.hitRate'),
        value: formatPercent(footerStats.hitRatio)
      }
    ];
  }

  if (activeTab === 'bandwidth') {
    const topName = chartData.labels[0];
    const topBytes = chartData.datasets[0]?.originalData?.[0] ?? 0;
    const topServiceValue = topName ? `${topName} - ${formatBytes(topBytes)}` : '-';
    return [
      {
        label: t('dashboard.serviceAnalytics.footer.bandwidthSaved'),
        value: formatBytes(chartData.total),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.hitRate'),
        value: formatPercent(footerStats.hitRatio)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.topService'),
        value: topServiceValue
      }
    ];
  }

  if (activeTab === 'misses') {
    const missRate = footerStats.totalBytes > 0 ? 100 - footerStats.hitRatio : 0;
    const topName = chartData.labels[0];
    const topBytes = chartData.datasets[0]?.originalData?.[0] ?? 0;
    const topSourceValue = topName ? `${topName} - ${formatBytes(topBytes)}` : '-';
    return [
      {
        label: t('dashboard.serviceAnalytics.footer.originPulls'),
        value: formatBytes(footerStats.missBytes),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.missRate'),
        value: formatPercent(missRate)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.topSource'),
        value: topSourceValue
      }
    ];
  }

  // 'service' tab
  const topServiceValue =
    footerStats.topServiceName.length > 0
      ? `${footerStats.topServiceName} - ${formatBytes(footerStats.topServiceBytes)}`
      : '-';
  return [
    {
      label: t('dashboard.serviceAnalytics.footer.totalData'),
      value: formatBytes(footerStats.totalBytes),
      tone: 'primary'
    },
    {
      label: t('dashboard.serviceAnalytics.footer.services'),
      value: String(footerStats.serviceCount)
    },
    {
      label: t('dashboard.serviceAnalytics.footer.topService'),
      value: topServiceValue
    }
  ];
}
