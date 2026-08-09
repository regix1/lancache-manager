import type { CacheSnapshotResponse } from '../../../../types';

interface CacheGrowth {
  change: number;
  percent: number | null;
}

export const getCacheGrowth = (
  timeRange: string,
  loading: boolean,
  cacheSnapshot: CacheSnapshotResponse | null
): CacheGrowth | null => {
  if (
    timeRange === 'live' ||
    loading ||
    !cacheSnapshot?.hasData ||
    cacheSnapshot.snapshotCount < 2 ||
    cacheSnapshot.isEstimate
  ) {
    return null;
  }

  const change = cacheSnapshot.endUsedSize - cacheSnapshot.startUsedSize;
  return {
    change,
    percent: cacheSnapshot.startUsedSize > 0 ? (change / cacheSnapshot.startUsedSize) * 100 : null
  };
};
