import { useMemo } from 'react';
import type { GameDetectionSummary, ServiceStat } from '@/types';
import { isActiveGame } from '@utils/gameDetection';
import type { ChartData, GameSliceExtra, TabId } from './types';
import { useGameColors } from './useGameColors';
import { useServiceColors } from './useServiceColors';

const MIN_SLICE_DEGREES = 4;

function applyMinimumSlice(values: number[], minPercent: number): number[] {
  const total = values.reduce((sum, value) => sum + value, 0);
  if (total <= 0) return values;

  const positiveIndices = values
    .map((value, index) => (value > 0 ? index : -1))
    .filter((v) => v >= 0);
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

const MAX_GAME_SLICES = 20;

export function useChartData(
  serviceStats: ServiceStat[],
  activeTab: TabId,
  games?: GameDetectionSummary[]
): ChartData {
  const { getColor, getCacheHitColor, getCacheMissColor, getBorderColor } = useServiceColors();
  const { getGameColors, getOtherColor } = useGameColors();

  return useMemo(() => {
    const borderColor = getBorderColor();

    if (activeTab === 'games') {
      const activeGames = (games ?? []).filter(isActiveGame);

      if (activeGames.length === 0) {
        return { labels: [], datasets: [], total: 0, isEmpty: true };
      }

      const sorted = activeGames
        .map((g) => ({
          name: g.game_name,
          value: g.total_size_bytes,
          cacheFiles: g.cache_files_found,
          service: g.service ?? 'steam'
        }))
        .sort((a, b) => b.value - a.value);

      const hasOther = sorted.length > MAX_GAME_SLICES;
      const topGames = sorted.slice(0, MAX_GAME_SLICES);
      const otherGames = hasOther ? sorted.slice(MAX_GAME_SLICES) : [];

      const labels = topGames.map((g) => g.name);
      const originalData = topGames.map((g) => g.value);
      const sliceExtras: GameSliceExtra[] = topGames.map((g) => ({
        cacheFiles: g.cacheFiles,
        service: g.service
      }));

      if (hasOther) {
        const otherTotal = otherGames.reduce((sum, g) => sum + g.value, 0);
        const otherFiles = otherGames.reduce((sum, g) => sum + g.cacheFiles, 0);
        labels.push(`Other (${otherGames.length} games)`);
        originalData.push(otherTotal);
        sliceExtras.push({ cacheFiles: otherFiles, service: 'mixed' });
      }

      const total = sorted.reduce((sum, g) => sum + g.value, 0);
      const gameColors = getGameColors(topGames.length);
      const bgColors = hasOther ? [...gameColors, getOtherColor()] : gameColors;
      const data = applyMinimumSlice(originalData, MIN_SLICE_DEGREES / 360);

      return {
        labels,
        datasets: [
          {
            id: 'games-distribution',
            data,
            originalData,
            backgroundColor: bgColors,
            borderColor,
            borderWidth: 2,
            borderRadius: 4,
            spacing: 2,
            hoverOffset: 8
          }
        ],
        total,
        isEmpty: false,
        gameSliceExtras: sliceExtras
      };
    }

    if (!serviceStats?.length) {
      return { labels: [], datasets: [], total: 0, isEmpty: true };
    }

    switch (activeTab) {
      case 'service': {
        const sorted = serviceStats
          .map((s) => ({ name: s.service, value: s.totalBytes }))
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
              hoverOffset: 8
            }
          ],
          total,
          isEmpty: false
        };
      }

      case 'hit-ratio': {
        const totalHits = serviceStats.reduce((sum, s) => sum + s.totalCacheHitBytes, 0);
        const totalMisses = serviceStats.reduce((sum, s) => sum + s.totalCacheMissBytes, 0);
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
              hoverOffset: 8
            }
          ],
          total,
          isEmpty: false
        };
      }

      case 'bandwidth': {
        const sorted = serviceStats
          .map((s) => ({ name: s.service, value: s.totalCacheHitBytes }))
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
              hoverOffset: 8
            }
          ],
          total,
          isEmpty: false
        };
      }

      case 'misses': {
        const sorted = serviceStats
          .map((s) => ({ name: s.service, value: s.totalCacheMissBytes }))
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
              id: 'cache-misses',
              data,
              originalData,
              backgroundColor: sorted.map((s) => getColor(s.name)),
              borderColor,
              borderWidth: 2,
              borderRadius: 4,
              spacing: 2,
              hoverOffset: 8
            }
          ],
          total,
          isEmpty: false
        };
      }

      default:
        return { labels: [], datasets: [], total: 0, isEmpty: true };
    }
  }, [
    serviceStats,
    activeTab,
    games,
    getColor,
    getCacheHitColor,
    getCacheMissColor,
    getBorderColor,
    getGameColors,
    getOtherColor
  ]);
}
