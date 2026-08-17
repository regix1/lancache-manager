import type { Event, EventCompareResponse } from '@/types';

export const MAX_COMPARE_EVENTS = 8;

export function elapsedLabel(minutes: number): string {
  if (minutes < 60) {
    return `${minutes}m`;
  }

  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  if (hours < 24) {
    return remainder > 0 ? `${hours}h ${remainder}m` : `${hours}h`;
  }

  const days = Math.floor(hours / 24);
  const leftoverHours = hours % 24;
  return leftoverHours > 0 ? `${days}d ${leftoverHours}h` : `${days}d`;
}

export function readCompareEventIds(raw: string | null, knownIds: number[]): number[] {
  if (!raw) {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .map((id) => Number(id))
      .filter((id) => Number.isInteger(id) && knownIds.includes(id))
      .slice(0, MAX_COMPARE_EVENTS);
  } catch {
    return [];
  }
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
  if (!Number.isFinite(hours) || hours >= 999999) {
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
      saved: series.saved.slice(0, elapsedMinutes.length)
    }))
  };
}
