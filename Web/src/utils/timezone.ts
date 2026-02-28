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
 * Format a date to a short date string (e.g., "Jan 15") in a specific timezone
 * Useful for date range labels
 */
export function formatShortDate(
  date: Date,
  timezone: string,
  options?: Partial<Intl.DateTimeFormatOptions>
): string {
  return date.toLocaleDateString(undefined, {
    timeZone: timezone,
    month: 'short',
    day: 'numeric',
    ...options
  });
}
