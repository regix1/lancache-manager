/**
 * A schedule expressed as a cron recurrence plus an optional time-of-day window, mirroring
 * the shape the API stores against a service. A service whose schedule is null runs on its
 * plain interval instead, and the interval value is left untouched so clearing a custom
 * schedule returns the service to exactly the interval it had before.
 */
export interface CustomSchedule {
  /** Standard 5-field cron expression, evaluated in {@link timeZoneId}. */
  expression: string;
  /** IANA zone id the expression and the window are both read in. */
  timeZoneId: string;
  /** "HH:mm" local to {@link timeZoneId}, or null when there is no window. */
  windowStart: string | null;
  /** "HH:mm". May be earlier than the start, which means the window crosses midnight. */
  windowEnd: string | null;
}

/**
 * The recurrence shapes the builder controls can express. A hand-written expression outside
 * these shapes is kept verbatim rather than being rewritten into one of them.
 */
export const SCHEDULE_REPEATS = ['hourly', 'daily', 'weekly', 'monthly'] as const;

export type ScheduleRepeat = (typeof SCHEDULE_REPEATS)[number];

/** Weekday keys in the order the picker lists them. Cron's own numbering starts on Sunday. */
export const WEEKDAY_KEYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const;

export type WeekdayKey = (typeof WEEKDAY_KEYS)[number];

/** A wall-clock time local to the schedule's timezone, not an instant. */
export interface ClockTime {
  hour: number;
  minute: number;
}

/**
 * Half of the day on a 12-hour face. Storage stays on the 24-hour clock everywhere, because
 * cron needs 0-23 and the wire shape is "HH:mm"; this exists only at the input edge.
 */
export type ClockPeriod = 'am' | 'pm';

/** What the builder controls hold. The cron expression is derived from it, never the reverse. */
export interface ScheduleDraft {
  repeat: ScheduleRepeat;
  /** Only meaningful for the hourly shape. */
  everyNHours: number;
  time: ClockTime;
  /** Only meaningful for the weekly shape. */
  weekdays: WeekdayKey[];
  /** Only meaningful for the monthly shape. */
  dayOfMonth: number;
  windowEnabled: boolean;
  windowStart: ClockTime;
  windowEnd: ClockTime;
}
