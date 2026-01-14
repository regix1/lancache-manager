import { useMemo } from 'react';
import type { ServiceStat } from '@/types';
import type { ChartData, TabId } from './types';
import { useServiceColors } from './useServiceColors';

const MIN_SLICE_DEGREES = 4;

function applyMinimumSlice(values: number[], minPercent: number): number[] {
  const total = values.reduce((sum, value) => sum + value, 0);
  if (total <= 0) return values;

  const positiveIndices = values.map((value, index) => (value > 0 ? index : -1)).filter((v) => v >= 0);
  if (positiveIndices.length === 0) return values;

  const maxMinPercent = 1 / positiveIndices.length;
  const effectiveMinPercent = Math.min(minPercent, maxMinPercent);
  const minValue = total * effectiveMinPercent;

  const adjusted = values.slice();
  const locked = new Set<number>();

  while (true) {
    let lockedTotal = 0;
    let remainingOriginalTotal = 0;
    const remainingIndices: number[] = [];

    for (const index of positiveIndices) {
      if (locked.has(index) || values[index] < minValue) {
        locked.add(index);
        adjusted[index] = minValue;
        lockedTotal += minValue;
      } else {
        remainingIndices.push(index);
        remainingOriginalTotal += values[index];
      }
    }

    if (remainingIndices.length === 0) {
      break;
    }

    const remainingTotal = total - lockedTotal;
    if (remainingTotal <= 0 || remainingOriginalTotal <= 0) {
      break;
    }

    let newlyLocked = false;
    for (const index of remainingIndices) {
      adjusted[index] = values[index] * (remainingTotal / remainingOriginalTotal);
      if (adjusted[index] < minValue) {
        locked.add(index);
        newlyLocked = true;
      }
    }

    if (!newlyLocked) {
      break;
    }
  }

  return adjusted;
}

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

        const originalData = sorted.map((s) => s.value);
        const data = applyMinimumSlice(originalData, MIN_SLICE_DEGREES / 360);

        return {
          labels: sorted.map((s) => s.name),
          datasets: [
            {
              id: 'service-distribution',
              data,
              originalData,
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

        const originalData = [totalHits, totalMisses];
        const data = applyMinimumSlice(originalData, MIN_SLICE_DEGREES / 360);

        return {
          labels: ['Cache Hits', 'Cache Misses'],
          datasets: [
            {
              id: 'cache-hit-ratio',
              data,
              originalData,
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

        const originalData = sorted.map((s) => s.value);
        const data = applyMinimumSlice(originalData, MIN_SLICE_DEGREES / 360);

        return {
          labels: sorted.map((s) => s.name),
          datasets: [
            {
              id: 'bandwidth-saved',
              data,
              originalData,
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
