import type { CacheSnapshotResponse } from '../../../../types';
import type { TimeRange } from '../../../../contexts/TimeFilterContext.types';

interface CacheGrowth {
  change: number;
  percent: number | null;
}

export const getCacheGrowth = (
  timeRange: TimeRange,
  loading: boolean,
  cacheSnapshot: CacheSnapshotResponse | null
): CacheGrowth | null => {
  if (
    timeRange === 'live' ||
    loading ||
    !cacheSnapshot?.hasData ||
    cacheSnapshot.snapshotCount < 2
  ) {
    return null;
  }

  const change = cacheSnapshot.endUsedSize - cacheSnapshot.startUsedSize;
  return {
    change,
    percent: cacheSnapshot.startUsedSize > 0 ? (change / cacheSnapshot.startUsedSize) * 100 : null
  };
};

type CacheGrowthEmptyState = 'live' | 'emptyCache' | 'waiting' | 'waitingWithNextSnapshot';

/**
 * Which empty-state message the panel should show when getCacheGrowth returns null.
 * 'live' has no start/end to compare at all, so it gets its own message rather than a false
 * claim about missing history. 'emptyCache' means no snapshot has ever been recorded because the
 * cache itself is empty, so promising a next reading would be a lie. A custom range can sit
 * entirely in the past, where the next reading will never land inside it, so only the trailing
 * presets (which always include now) name a time.
 */
export const getCacheGrowthEmptyState = (
  timeRange: TimeRange,
  cacheSnapshot: CacheSnapshotResponse | null,
  hasCurrentCapacity: boolean,
  cacheSectionFailed: boolean
): CacheGrowthEmptyState => {
  if (timeRange === 'live') {
    return 'live';
  }
  if (!hasCurrentCapacity && !cacheSectionFailed) {
    return 'emptyCache';
  }
  if (timeRange !== 'custom' && cacheSnapshot?.nextSnapshotUtc) {
    return 'waitingWithNextSnapshot';
  }
  return 'waiting';
};
