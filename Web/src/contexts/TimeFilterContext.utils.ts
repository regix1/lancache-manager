import { getDayBoundsInTimezone, getEffectiveTimezone } from '@utils/timezone';
import type { TimeRange } from './TimeFilterContext.types';
import type { Event } from '../types';

/**
 * `live` covers all of history and `custom` has no span until both dates exist. Infinity says so,
 * where a very large hour count would silently behave as a real duration.
 */
export const NO_TIME_WINDOW_HOURS = Infinity;

/** Duration of a TimeRange in hours, or NO_TIME_WINDOW_HOURS for the ranges with no window. */
function getTimeRangeHours(range: TimeRange): number {
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
      return NO_TIME_WINDOW_HOURS;
    case 'custom':
      // Only reached before both dates are picked; a picked span is measured from the dates.
      return NO_TIME_WINDOW_HOURS;
    default:
      return 24;
  }
}

/**
 * Computes startTime/endTime (Unix seconds) for a given time range.
 *
 * A custom range closes on the reader's clock, read from the global preference: TimeFilterProvider
 * wraps TimezoneProvider (AppProviders.tsx:61,79), so the flags cannot be read as a hook here.
 *
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
  if (range === 'custom' && customStart != null && customEnd != null) {
    const startTime = Math.floor(customStart / 1000);
    // The picker offers whole calendar days, so the last day runs to its own last moment on the
    // reader's clock; setHours would cut that day short whenever they are not on the browser's.
    const dayEnd = getDayBoundsInTimezone(new Date(customEnd), getEffectiveTimezone()).end;
    // Cap end time at current time to prevent fetching "future" data
    const endTimestamp = Math.min(dayEnd.getTime(), now);
    const endTime = Math.floor(endTimestamp / 1000);
    return { startTime, endTime };
  }

  const hours = getTimeRangeHours(range);
  if (!Number.isFinite(hours)) {
    // live, and custom before both dates: no bounds, so the server answers over all of history.
    return {};
  }

  // Rolling ranges (1h, 6h, 12h, 24h, 7d, 30d)
  const hoursMs = hours * 60 * 60 * 1000;
  const startTime = Math.floor((now - hoursMs) / 1000);
  const endTime = Math.floor(now / 1000);
  return { startTime, endTime };
}

/**
 * Drops ids from a selected-event-id list that no longer have a matching event.
 * Returns the SAME array reference when nothing is removed, so an effect that calls this on every
 * `events` change can write the result back into state without looping.
 */
export function pruneMissingEventIds(selectedIds: number[], events: Event[]): number[] {
  if (selectedIds.length === 0) {
    return selectedIds;
  }
  const existingIds = new Set<number>(events.map((event: Event) => event.id));
  const kept = selectedIds.filter((id: number) => existingIds.has(id));
  return kept.length === selectedIds.length ? selectedIds : kept;
}
