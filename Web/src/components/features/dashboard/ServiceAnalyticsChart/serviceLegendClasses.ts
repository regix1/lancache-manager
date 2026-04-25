import type { TFunction } from 'i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import type { ChartData, TabId } from './types';

/**
 * Mapping of normalized service identifiers to legend color CSS class names.
 * Used internally by `getLegendColorClass` to color the swatch / left bar / dot
 * of each row in ChartLegend. Local-only (not exported - knip clean).
 */
const SERVICE_LEGEND_CLASSES: Record<string, string> = {
  steam: 'legend-color-steam',
  epic: 'legend-color-epic',
  epicgames: 'legend-color-epic',
  origin: 'legend-color-origin',
  ea: 'legend-color-origin',
  blizzard: 'legend-color-blizzard',
  battlenet: 'legend-color-blizzard',
  'battle.net': 'legend-color-blizzard',
  wsus: 'legend-color-wsus',
  windows: 'legend-color-wsus',
  riot: 'legend-color-riot',
  riotgames: 'legend-color-riot',
  xbox: 'legend-color-xbox',
  xboxlive: 'legend-color-xbox',
  ubisoft: 'legend-color-ubisoft',
  uplay: 'legend-color-ubisoft',
  gog: 'legend-color-gog',
  rockstar: 'legend-color-rockstar'
};

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
  return SERVICE_LEGEND_CLASSES[normalizedLabel] ?? 'legend-color-default';
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
 * Per-tab content (per acceptance criteria 11-16):
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
        label: t('dashboard.serviceAnalytics.footer.totalDisk', 'Total on Disk'),
        value: formatBytes(footerStats.totalBytes),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.gamesDetected', 'Games'),
        value: String(footerStats.gameCount)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.largestGameValue', 'Largest'),
        value: largestValue
      }
    ];
  }

  if (activeTab === 'hit-ratio') {
    return [
      {
        label: t('dashboard.serviceAnalytics.footer.totalData', 'Total Data'),
        value: formatBytes(footerStats.totalBytes),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.cacheHits', 'Cache Hits'),
        value: formatBytes(footerStats.totalHitBytes)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.hitRate', 'Hit Rate'),
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
        label: t('dashboard.serviceAnalytics.footer.bandwidthSaved', 'Bandwidth Saved'),
        value: formatBytes(chartData.total),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.hitRate', 'Hit Rate'),
        value: formatPercent(footerStats.hitRatio)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.topService', 'Top Service'),
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
        label: t('dashboard.serviceAnalytics.footer.originPulls', 'From Internet'),
        value: formatBytes(footerStats.missBytes),
        tone: 'primary'
      },
      {
        label: t('dashboard.serviceAnalytics.footer.missRate', 'Miss Rate'),
        value: formatPercent(missRate)
      },
      {
        label: t('dashboard.serviceAnalytics.footer.topSource', 'Top Source'),
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
      label: t('dashboard.serviceAnalytics.footer.totalData', 'Total Data'),
      value: formatBytes(footerStats.totalBytes),
      tone: 'primary'
    },
    {
      label: t('dashboard.serviceAnalytics.footer.services', 'Services'),
      value: String(footerStats.serviceCount)
    },
    {
      label: t('dashboard.serviceAnalytics.footer.topService', 'Top Service'),
      value: topServiceValue
    }
  ];
}
