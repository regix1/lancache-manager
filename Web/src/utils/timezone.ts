import { getGlobalTimezonePreference } from './timezonePreference';
import { getGlobalUtcPreference } from './utcTimezonePreference';

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
 * Get the browser's local timezone.
 *
 * The machine's own zone, never a display preference: getEffectiveTimezone answers "which clock
 * is this reader looking at" and returns UTC when that setting is on, which is the wrong answer
 * for a control that has to name the zone the browser is actually in.
 */
export function getLocalTimezone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * Get the effective timezone based on user preference (local vs server)
 * Use this when you need to determine which timezone to use for display
 */
export function getEffectiveTimezone(useLocalTimezone?: boolean, useUtc?: boolean): string {
  // The UTC preference is a third clock rather than a variation on the other two, so it answers
  // first and the local/server choice below it stops mattering while it is on.
  // Passed explicitly it wins over the module value, the same way the local choice below already
  // does. The module value only catches up once the save echoes back, so a render caused by the
  // switch itself reads the clock the user just left, and nothing asks again afterwards.
  if (useUtc ?? getGlobalUtcPreference()) return 'UTC';
  // If explicitly passed, use that value
  const useLocal = useLocalTimezone ?? getGlobalTimezonePreference();
  return useLocal ? getLocalTimezone() : getServerTimezone();
}

/**
 * Reuse one formatter per zone and option shape. Building an Intl.DateTimeFormat is expensive and
 * the callers below run in loops: the custom schedule preview walks up to 3700 candidate days per
 * recompute and recomputes on every keystroke, which built a formatter per day. The key carries the
 * options as well as the zone, so two different option shapes never share an instance. A formatter
 * is stateless once built, so sharing one across calls is safe.
 *
 * Only a formatter that constructed successfully is stored. An id Intl rejects throws before the
 * write, so the failure is never cached and the caller's fallback still runs. [64]
 */
const zoneFormatters = new Map<string, Intl.DateTimeFormat>();

function formatterForTimezone(
  timezone: string,
  options: Intl.DateTimeFormatOptions
): Intl.DateTimeFormat {
  const key = `${timezone}|${JSON.stringify(options)}`;
  const cached = zoneFormatters.get(key);
  if (cached) return cached;

  const formatter = new Intl.DateTimeFormat('en-US', { ...options, timeZone: timezone });
  zoneFormatters.set(key, formatter);
  return formatter;
}

/**
 * Zone ids this runtime could not resolve. The fallback to UTC is decided once per id: the callers
 * below run in loops, and the custom schedule preview walks up to 3700 candidate days per recompute
 * on every keystroke, so retrying a bad id costs one thrown Intl construction and one identical
 * console warning per day. Recording the id keeps both at one for the life of the tab. [11]
 */
const unresolvableZones = new Set<string>();

/** Whether {@link rememberUnresolvableTimezone} has already settled this id on the UTC fallback. */
export function isUnresolvableTimezone(timezone: string | undefined): boolean {
  return timezone !== undefined && unresolvableZones.has(timezone);
}

/** Records the fallback and warns, once per id, however many callers hit the same bad zone. */
export function rememberUnresolvableTimezone(timezone: string | undefined): void {
  if (timezone === undefined || unresolvableZones.has(timezone)) return;
  unresolvableZones.add(timezone);
  console.warn(`Invalid timezone "${timezone}", falling back to UTC`);
}

/** Whether this runtime can resolve a zone id, using the same cache and fallback record as readers. */
export function canResolveTimezone(timezone: string): boolean {
  if (isUnresolvableTimezone(timezone)) return false;
  try {
    formatterForTimezone(timezone, {});
    return true;
  } catch (_tzError) {
    rememberUnresolvableTimezone(timezone);
    return false;
  }
}

/**
 * Split a moment into parts in a named zone, degrading to UTC when the runtime does not know the
 * id. Intl throws on construction for an id it cannot resolve, and these two readers are called
 * straight from render bodies, so a zone spelled in a form the browser rejects would otherwise
 * take the page down where formatTimestamp merely warns and carries on. [22]
 */
function partsInTimezone(
  date: Date,
  timezone: string,
  options: Intl.DateTimeFormatOptions
): Intl.DateTimeFormatPart[] {
  if (isUnresolvableTimezone(timezone)) {
    return formatterForTimezone('UTC', options).formatToParts(date);
  }
  try {
    return formatterForTimezone(timezone, options).formatToParts(date);
  } catch (_tzError) {
    rememberUnresolvableTimezone(timezone);
    return formatterForTimezone('UTC', options).formatToParts(date);
  }
}

/**
 * Get date components (year, month, day) in a specific timezone
 * Useful for calendar displays and date comparisons across timezones
 */
export function getDateInTimezone(
  date: Date,
  timezone: string
): { year: number; month: number; day: number } {
  const parts = partsInTimezone(date, timezone, {
    year: 'numeric',
    month: 'numeric',
    day: 'numeric'
  });
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
  const parts = partsInTimezone(date, timezone, {
    hour: 'numeric',
    minute: 'numeric',
    second: 'numeric',
    hour12: false
  });
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
export function getCurrentHour(useLocalTimezone?: boolean, useUtc?: boolean): number {
  const timezone = getEffectiveTimezone(useLocalTimezone, useUtc);
  return getTimeInTimezone(new Date(), timezone).hour;
}

/**
 * How far a zone's wall clock runs from UTC at a given moment, in milliseconds.
 *
 * Read back through the two zoned readers above, so an id the runtime rejects degrades to UTC
 * rather than throwing. The readers carry no sub-second field, so the moment is compared at whole
 * seconds on both sides.
 */
export function zoneOffsetMs(utcMs: number, timezone: string): number {
  const moment = new Date(utcMs);
  const { year, month, day } = getDateInTimezone(moment, timezone);
  const { hour, minute, second } = getTimeInTimezone(moment, timezone);
  // Some ICU builds report midnight as hour 24, which Date.UTC would advance into the next day.
  const wallClock = Date.UTC(year, month, day, hour % 24, minute, second);
  return wallClock - Math.floor(utcMs / 1000) * 1000;
}

/**
 * The moment a zone's clock reaches midnight on the given calendar day.
 *
 * Subtracting the offset once already lands inside the right day, but it can still miss by the
 * length of a DST step when the change falls between the guess and the answer, so the offset is
 * read again at the guess and applied a second time. `Date.UTC` normalizes a day number past the
 * end of the month, which is what lets a caller ask for `day + 1` at a month or year boundary.
 */
export function getDayStartInTimezone(
  year: number,
  month: number,
  day: number,
  timezone: string
): Date {
  const wallClock = Date.UTC(year, month, day);
  const guess = wallClock - zoneOffsetMs(wallClock, timezone);
  return new Date(wallClock - zoneOffsetMs(guess, timezone));
}

/**
 * The first and last moment of the calendar day a zone shows for `date`.
 *
 * This is how a day the reader can see in a label becomes the pair of instants a query can use.
 * Reaching for `setHours` instead reads the browser's own calendar, which around midnight names a
 * different day than the one on screen. The end is taken as one millisecond before the next day
 * begins rather than by adding a fixed span, because a DST change makes a calendar day 23 or 25
 * hours long.
 */
export function getDayBoundsInTimezone(date: Date, timezone: string): { start: Date; end: Date } {
  const { year, month, day } = getDateInTimezone(date, timezone);
  return {
    start: getDayStartInTimezone(year, month, day, timezone),
    end: new Date(getDayStartInTimezone(year, month, day + 1, timezone).getTime() - 1)
  };
}
