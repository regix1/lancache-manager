import type { TimeRange } from './TimeFilterContext.types';

/** Maps a TimeRange value to its duration in hours. Used by computeTimeRangeParams. */
export function getTimeRangeHours(range: TimeRange): number {
  switch (range) {
    case '1h':
      return 1;
    case '6h':
      return 6;
    case '12h':
      return 12;
    case '24h':
      return 24;
    case '7d':
      return 168;
    case '30d':
      return 720;
    case 'live':
      return 999999;
    case 'custom':
      return 24;
    default:
      return 24;
  }
}

/**
 * Pure function that computes startTime/endTime (Unix seconds) for a given time range.
 * @param range - The time range to compute params for
 * @param now - Current timestamp in milliseconds (e.g. Date.now() or rangeAnchorTime)
 * @param customStart - Custom start date timestamp in ms (only used when range is 'custom')
 * @param customEnd - Custom end date timestamp in ms (only used when range is 'custom')
 */
export function computeTimeRangeParams(
  range: TimeRange,
  now: number,
  customStart?: number | null,
  customEnd?: number | null
): { startTime?: number; endTime?: number } {
  if (range === 'live') {
    return {};
  }

  if (range === 'custom' && customStart != null && customEnd != null) {
    const startTime = Math.floor(customStart / 1000);
    // Set end time to end of day (23:59:59.999)
    const endDate = new Date(customEnd);
    endDate.setHours(23, 59, 59, 999);
    // Cap end time at current time to prevent fetching "future" data
    const endTimestamp = Math.min(endDate.getTime(), now);
    const endTime = Math.floor(endTimestamp / 1000);
    return { startTime, endTime };
  }

  // Rolling ranges (1h, 6h, 12h, 24h, 7d, 30d)
  const hoursMs = getTimeRangeHours(range) * 60 * 60 * 1000;
  const startTime = Math.floor((now - hoursMs) / 1000);
  const endTime = Math.floor(now / 1000);
  return { startTime, endTime };
}
