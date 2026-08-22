import type { TFunction } from 'i18next';
import { clampEventColorIndex } from '@utils/eventColors';
import { formatMinutes } from '@utils/timeFormatters';
import type { Event, EventCompareResponse, EventCompareSeries } from '@/types';

export const MAX_COMPARE_EVENTS = 8;

/**
 * Series positions whose color repeats one already used by an earlier series. colorIndex is a
 * property the user picks per event and nothing stops two events from sharing one, so a
 * comparison can draw two lines and two legend dots in one color. The calendar and the event
 * list color the same event from the same index, so the chart keeps the color and marks the
 * repeat with a dash instead.
 */
export function repeatedColorPositions(series: EventCompareSeries[]): ReadonlySet<number> {
  const used = new Set<number>();
  const repeated = new Set<number>();

  series.forEach((entry, position) => {
    const colorIndex = clampEventColorIndex(entry.colorIndex);
    if (used.has(colorIndex)) {
      repeated.add(position);
    } else {
      used.add(colorIndex);
    }
  });

  return repeated;
}

export function elapsedLabel(minutes: number, t: TFunction): string {
  return formatMinutes(minutes, t);
}

export function readCompareEventIds(stored: number[] | null, knownIds: number[]): number[] {
  // A hand-edited or stale value parses into anything at all, so the shape is checked here rather
  // than trusted from the type argument. [45]
  if (!Array.isArray(stored)) {
    return [];
  }

  return stored
    .map((id) => Number(id))
    .filter((id) => Number.isInteger(id) && knownIds.includes(id))
    .slice(0, MAX_COMPARE_EVENTS);
}

export function defaultCompareEventIds(events: Event[]): number[] {
  return [...events]
    .sort((left, right) => right.startTimeUtc.localeCompare(left.startTimeUtc))
    .slice(0, 2)
    .map((event) => event.id);
}

export function clipCompareToHours(
  compare: EventCompareResponse,
  hours: number
): EventCompareResponse {
  if (!Number.isFinite(hours)) {
    return compare;
  }

  const maxMinutes = hours * 60;
  const elapsedMinutes = compare.elapsedMinutes.filter((minutes) => minutes <= maxMinutes);
  if (elapsedMinutes.length === compare.elapsedMinutes.length) {
    return compare;
  }

  return {
    ...compare,
    elapsedMinutes,
    series: compare.series.map((series) => ({
      ...series,
      served: series.served.slice(0, elapsedMinutes.length),
      saved: series.saved.slice(0, elapsedMinutes.length),
      missed: series.missed.slice(0, elapsedMinutes.length)
    }))
  };
}
