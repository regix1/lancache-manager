import { formatTimestamp } from './dateTimeFormat';
import { getGlobalTimezonePreference } from './timezonePreference';

// Server timezone storage
let serverTimezone: string | null = null;

export function setServerTimezone(tz: string) {
  serverTimezone = tz;
}

export function getServerTimezone(): string {
  return serverTimezone || Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * Parse a backend timestamp as UTC.
 *
 * The API returns some timestamps without a timezone suffix, which `new Date()`
 * interprets in the browser's local zone. That shifts the value by the local
 * offset and can make a past time read as being in the future. Append the `Z`
 * only when the string carries neither `Z` nor an explicit `+HH:MM`/`-HH:MM`.
 */
export function parseUtcDate(value: string): Date {
  const normalized = value.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(value) ? value : `${value}Z`;
  return new Date(normalized);
}

/**
 * Get the browser's local timezone
 */
function getLocalTimezone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * Get the effective timezone based on user preference (local vs server)
 * Use this when you need to determine which timezone to use for display
 */
export function getEffectiveTimezone(useLocalTimezone?: boolean): string {
  // If explicitly passed, use that value
  const useLocal = useLocalTimezone ?? getGlobalTimezonePreference();
  return useLocal ? getLocalTimezone() : getServerTimezone();
}

/**
 * Get date components (year, month, day) in a specific timezone
 * Useful for calendar displays and date comparisons across timezones
 */
export function getDateInTimezone(
  date: Date,
  timezone: string
): { year: number; month: number; day: number } {
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone: timezone,
    year: 'numeric',
    month: 'numeric',
    day: 'numeric'
  });
  const parts = formatter.formatToParts(date);
  return {
    year: parseInt(parts.find((p) => p.type === 'year')?.value || '0'),
    month: parseInt(parts.find((p) => p.type === 'month')?.value || '1') - 1, // 0-indexed
    day: parseInt(parts.find((p) => p.type === 'day')?.value || '1')
  };
}

/**
 * Get time components (hour, minute, second) in a specific timezone
 * Useful for clock displays and time comparisons across timezones
 */
export function getTimeInTimezone(
  date: Date,
  timezone: string
): { hour: number; minute: number; second: number } {
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone: timezone,
    hour: 'numeric',
    minute: 'numeric',
    second: 'numeric',
    hour12: false
  });
  const parts = formatter.formatToParts(date);
  return {
    hour: parseInt(parts.find((p) => p.type === 'hour')?.value || '0'),
    minute: parseInt(parts.find((p) => p.type === 'minute')?.value || '0'),
    second: parseInt(parts.find((p) => p.type === 'second')?.value || '0')
  };
}

/**
 * Get current hour in the effective timezone (based on user preference)
 * Useful for "current hour" highlighting in charts
 */
export function getCurrentHour(useLocalTimezone?: boolean): number {
  const timezone = getEffectiveTimezone(useLocalTimezone);
  return getTimeInTimezone(new Date(), timezone).hour;
}

/**
 * Format a date to a short date string (e.g., "1/15/26")
 * Useful for date range labels.
 *
 * The compact shape is deliberate: this feeds a nowrap label that sits beside a fixed-width control,
 * where a spelled-out month would widen the row enough to push the group onto its own line.
 *
 * Callers pass calendar days that were built in the browser's own calendar, so the value is read
 * back in that same calendar: reinterpreting a local midnight in the server's timezone can move the
 * label onto the day before the one the user picked. A date carries no time, so the 24-hour
 * preference has nothing to act on here.
 */
export function formatShortDate(date: Date): string {
  return formatTimestamp(date, {
    useLocalTimezone: true,
    use24Hour: true,
    forceYear: false,
    style: 'dateShort'
  });
}
