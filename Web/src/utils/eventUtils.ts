/**
 * Shared event utility functions for sorting and classifying events by status.
 * Used by DateRangePicker and TimeFilter components.
 */

type EventStatus = 'active' | 'upcoming' | 'past';

interface EventWithTimes {
  startTimeUtc: string;
  endTimeUtc: string;
}

/**
 * Determines whether an event is active, upcoming, or past based on current time.
 */
export function getEventStatus(startUtc: string, endUtc: string): EventStatus {
  const now = new Date();
  const start = new Date(startUtc);
  const end = new Date(endUtc);
  if (now >= start && now <= end) return 'active';
  if (now < start) return 'upcoming';
  return 'past';
}

/**
 * Sorts events by status priority: active first, then upcoming, then past.
 * Within each group, sorts by start time ascending.
 */
export function sortEventsByStatus<T extends EventWithTimes>(events: T[]): T[] {
  const now = new Date();
  return [...events].sort((a, b) => {
    const aStart = new Date(a.startTimeUtc);
    const aEnd = new Date(a.endTimeUtc);
    const bStart = new Date(b.startTimeUtc);
    const bEnd = new Date(b.endTimeUtc);

    const aIsActive = now >= aStart && now <= aEnd;
    const bIsActive = now >= bStart && now <= bEnd;
    const aIsUpcoming = now < aStart;
    const bIsUpcoming = now < bStart;

    if (aIsActive && !bIsActive) return -1;
    if (!aIsActive && bIsActive) return 1;
    if (aIsUpcoming && !bIsUpcoming) return -1;
    if (!aIsUpcoming && bIsUpcoming) return 1;

    return aStart.getTime() - bStart.getTime();
  });
}
