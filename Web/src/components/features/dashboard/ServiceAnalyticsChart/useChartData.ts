import { useMemo } from 'react';
import type { ServiceStat } from '@/types';
import type { ChartData, TabId } from './types';
import { useServiceColors } from './useServiceColors';

export function useChartData(serviceStats: ServiceStat[], activeTab: TabId): ChartData {
  const { getColor, getCacheHitColor, getCacheMissColor, getBorderColor } = useServiceColors();

  return useMemo(() => {
    const borderColor = getBorderColor();

    if (!serviceStats?.length) {
      return { labels: [], datasets: [], total: 0, isEmpty: true };
    }

    switch (activeTab) {
      case 'service': {
        const sorted = serviceStats
          .map((s) => ({ name: s.service, value: s.totalBytes || 0 }))
          .filter((s) => s.value > 0)
          .sort((a, b) => b.value - a.value);

        if (sorted.length === 0) {
          return { labels: [], datasets: [], total: 0, isEmpty: true };
        }

        const total = sorted.reduce((sum, s) => sum + s.value, 0);

        return {
          labels: sorted.map((s) => s.name),
          datasets: [
            {
              id: 'service-distribution',
              data: sorted.map((s) => s.value),
              backgroundColor: sorted.map((s) => getColor(s.name)),
              borderColor,
              borderWidth: 2,
              borderRadius: 4,
              spacing: 2,
              hoverOffset: 8,
            },
          ],
          total,
          isEmpty: false,
        };
      }

      case 'hit-ratio': {
        const totalHits = serviceStats.reduce((sum, s) => sum + (s.totalCacheHitBytes || 0), 0);
        const totalMisses = serviceStats.reduce((sum, s) => sum + (s.totalCacheMissBytes || 0), 0);
        const total = totalHits + totalMisses;

        if (total === 0) {
          return { labels: [], datasets: [], total: 0, isEmpty: true };
        }

        return {
          labels: ['Cache Hits', 'Cache Misses'],
          datasets: [
            {
              id: 'cache-hit-ratio',
              data: [totalHits, totalMisses],
              backgroundColor: [getCacheHitColor(), getCacheMissColor()],
              borderColor,
              borderWidth: 2,
              borderRadius: 4,
              spacing: 2,
              hoverOffset: 8,
            },
          ],
          total,
          isEmpty: false,
        };
      }

      case 'bandwidth': {
        const sorted = serviceStats
          .map((s) => ({ name: s.service, value: s.totalCacheHitBytes || 0 }))
          .filter((s) => s.value > 0)
          .sort((a, b) => b.value - a.value);

        if (sorted.length === 0) {
          return { labels: [], datasets: [], total: 0, isEmpty: true };
        }

        const total = sorted.reduce((sum, s) => sum + s.value, 0);

        return {
          labels: sorted.map((s) => s.name),
          datasets: [
            {
              id: 'bandwidth-saved',
              data: sorted.map((s) => s.value),
              backgroundColor: sorted.map((s) => getColor(s.name)),
              borderColor,
              borderWidth: 2,
              borderRadius: 4,
              spacing: 2,
              hoverOffset: 8,
            },
          ],
          total,
          isEmpty: false,
        };
      }

      default:
        return { labels: [], datasets: [], total: 0, isEmpty: true };
    }
  }, [serviceStats, activeTab, getColor, getCacheHitColor, getCacheMissColor, getBorderColor]);
}
