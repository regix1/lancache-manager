import { Cron } from 'croner';
import type { useTranslation } from 'react-i18next';
import { getTimeInTimezone } from '@utils/timezone';
import {
  WEEKDAY_KEYS,
  type ClockPeriod,
  type ClockTime,
  type CustomSchedule,
  type ScheduleDraft,
  type WeekdayKey
} from './types';

/** react-i18next's translate function, as returned by `useTranslation()`. */
type TranslateFn = ReturnType<typeof useTranslation>['t'];

const BASE_KEY = 'management.schedules.customSchedule';

/**
 * Cron numbers weekdays from Sunday, while the picker lists them from Monday, so the two
 * orders are kept apart rather than one being derived from the other's index.
 */
const WEEKDAY_CRON_NUMBERS: Record<WeekdayKey, number> = {
  sun: 0,
  mon: 1,
  tue: 2,
  wed: 3,
  thu: 4,
  fri: 5,
  sat: 6
};

const MINUTES_PER_DAY = 1440;
export const MS_PER_DAY = 86_400_000;

/**
 * Bounds on the occurrence walk. An expression and a window that never intersect (03:00 daily
 * inside a 22:00-06:00 window) would otherwise walk forever looking for a run that cannot exist.
 *
 * The lookahead has to clear the longest gap this grammar can put between two runs, or a legal
 * expression reads as one that never runs and the modal refuses to save it. That longest gap is
 * the eight years an every-29-February expression waits across a century that skips its leap
 * year: 29 Feb 2096 to 29 Feb 2104 is 2921 days. The value below clears it with room to spare
 * and is the same span the server allows.
 */
const MAX_CANDIDATES = 4000;
const MAX_LOOKAHEAD_DAYS = 3700;
const OCCURRENCE_BATCH = 50;

/** Daily at 02:00 with no window: valid on open, so the preview has something to show. */
export const DEFAULT_SCHEDULE_DRAFT: ScheduleDraft = {
  repeat: 'daily',
  everyNHours: 2,
  time: { hour: 0, minute: 0 },
  weekdays: ['mon', 'tue', 'wed', 'thu', 'fri'],
  dayOfMonth: 1,
  windowEnabled: false,
  windowStart: { hour: 22, minute: 0 },
  windowEnd: { hour: 6, minute: 0 }
};

export function isWeekdayKey(value: string): value is WeekdayKey {
  return (WEEKDAY_KEYS as readonly string[]).includes(value);
}

/**
 * The five positions of a cron expression, in the order they are written. The builder never
 * shows these; they exist so the raw expression can be edited one position at a time instead
 * of as a single run of characters where nobody can tell where one position ends.
 */
export const CRON_POSITIONS = ['minute', 'hour', 'dayOfMonth', 'month', 'weekday'] as const;

interface PositionLimit {
  /** The largest number the position accepts. */
  max: number;
  /** How many digits a written number runs to. A weekday is never padded to two. */
  digits: number;
}

const POSITION_LIMITS: readonly PositionLimit[] = [
  { max: 59, digits: 2 },
  { max: 23, digits: 2 },
  { max: 31, digits: 2 },
  { max: 12, digits: 2 },
  { max: 7, digits: 1 }
];

/**
 * Whether a typed number fills its position, meaning no further digit could be added and still
 * land in range: hour `3` is filled because `30` is past 23, hour `2` is not because `23` is
 * real. Only plain numbers can be filled; a `*`, a list or a step can always grow, so a box
 * holding one of those never says it is done and never moves the caret out from under someone
 * halfway through typing a step.
 */
export function fillsPosition(index: number, value: string): boolean {
  const limit = POSITION_LIMITS[index];
  if (!limit || !/^\d{1,2}$/.test(value)) return false;
  const parsed = Number(value);
  if (parsed > limit.max) return false;
  return value.length >= limit.digits || parsed * 10 > limit.max;
}

/**
 * An expression split into its five positions, padded with empties so a half-typed string
 * still fills every box. A blank position makes the whole expression invalid, which is what
 * it is; the alternative is guessing a `*` the user did not type.
 */
export function splitExpression(expression: string): string[] {
  const trimmed = expression.trim();
  const parts = trimmed.length === 0 ? [] : trimmed.split(/\s+/);
  return CRON_POSITIONS.map((_, index) => parts[index] ?? '');
}

export function joinExpression(positions: readonly string[]): string {
  return positions.join(' ').trim();
}

/** 12-hour face of a 24-hour hour. Midnight is 12 am, noon is 12 pm. */
export function toTwelveHour(hour: number): { hour: number; period: ClockPeriod } {
  const shown = hour % 12;
  return { hour: shown === 0 ? 12 : shown, period: hour < 12 ? 'am' : 'pm' };
}

/** The 24-hour hour behind a 12-hour face. 12 am is 00, 12 pm is 12. */
export function toTwentyFourHour(hour: number, period: ClockPeriod): number {
  const base = hour % 12;
  return period === 'am' ? base : base + 12;
}

function readNumber(field: string, min: number, max: number): number | null {
  if (!/^\d{1,2}$/.test(field)) return null;
  const parsed = Number(field);
  return parsed >= min && parsed <= max ? parsed : null;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/** "HH:mm" for the wire. */
function formatClockTime(time: ClockTime): string {
  return `${pad(time.hour)}:${pad(time.minute)}`;
}

/** Reads a "HH:mm" wire value. Returns null for null, blank or malformed input. */
function parseClockTime(value: string | null): ClockTime | null {
  if (!value) return null;
  const match = /^(\d{1,2}):(\d{2})$/.exec(value.trim());
  if (!match) return null;
  const hour = Number(match[1]);
  const minute = Number(match[2]);
  if (hour > 23 || minute > 59) return null;
  return { hour, minute };
}

/**
 * The cron expression for a draft. Weekly with no day selected has no valid expression, so
 * it yields an empty string; every consumer treats that as "nothing to compute yet" rather
 * than guessing a day on the user's behalf.
 */
export function buildExpression(draft: ScheduleDraft): string {
  const { hour, minute } = draft.time;
  switch (draft.repeat) {
    // An hourly repeat fires at the same minute past every Nth hour counted from midnight, so
    // it has no hour of its own and the draft's hour is held at zero for this shape.
    case 'hourly':
      return `${minute} */${draft.everyNHours} * * *`;
    case 'weekly': {
      if (draft.weekdays.length === 0) return '';
      const days = draft.weekdays
        .map((day) => WEEKDAY_CRON_NUMBERS[day])
        .sort((left, right) => left - right)
        .join(',');
      return `${minute} ${hour} * * ${days}`;
    }
    case 'monthly':
      return `${minute} ${hour} ${draft.dayOfMonth} * *`;
    case 'daily':
    default:
      return `${minute} ${hour} * * *`;
  }
}

function readWeekdays(field: string): WeekdayKey[] | null {
  const days: WeekdayKey[] = [];
  for (const part of field.split(',')) {
    const value = readNumber(part, 0, 7);
    if (value === null) return null;
    // Cron accepts both 0 and 7 for Sunday.
    const key = WEEKDAY_KEYS.find((day) => WEEKDAY_CRON_NUMBERS[day] === value % 7);
    if (!key || days.includes(key)) return null;
    days.push(key);
  }
  return days.length > 0 ? days : null;
}

/**
 * The builder controls that produce this expression, or null when the expression is outside
 * what they can express (`L`, `W`, `#`, step-within-range, a month restriction, or a
 * day-of-month and day-of-week together). Callers keep such an expression verbatim and lock
 * the builder rather than rewriting it into something the user did not type.
 */
export function readDraft(expression: string): ScheduleDraft | null {
  const fields = expression.trim().split(/\s+/);
  if (fields.length !== 5) return null;
  const [minuteField, hourField, dayOfMonthField, monthField, weekdayField] = fields;
  if (monthField !== '*') return null;

  const minute = readNumber(minuteField, 0, 59);
  if (minute === null) return null;

  const step = /^\*\/(\d{1,2})$/.exec(hourField);
  if (step) {
    if (dayOfMonthField !== '*' || weekdayField !== '*') return null;
    const everyNHours = Number(step[1]);
    if (everyNHours < 1 || everyNHours > 23) return null;
    return {
      ...DEFAULT_SCHEDULE_DRAFT,
      repeat: 'hourly',
      everyNHours,
      time: { hour: 0, minute }
    };
  }

  const hour = readNumber(hourField, 0, 23);
  if (hour === null) return null;
  const time: ClockTime = { hour, minute };

  if (dayOfMonthField === '*' && weekdayField === '*') {
    return { ...DEFAULT_SCHEDULE_DRAFT, repeat: 'daily', time };
  }
  if (dayOfMonthField === '*') {
    const weekdays = readWeekdays(weekdayField);
    if (!weekdays) return null;
    return { ...DEFAULT_SCHEDULE_DRAFT, repeat: 'weekly', time, weekdays };
  }
  if (weekdayField === '*') {
    const dayOfMonth = readNumber(dayOfMonthField, 1, 31);
    if (dayOfMonth === null) return null;
    return { ...DEFAULT_SCHEDULE_DRAFT, repeat: 'monthly', time, dayOfMonth };
  }
  return null;
}

/** The saved schedule read back into builder controls, or null when it is hand-written cron. */
export function readSchedule(schedule: CustomSchedule): ScheduleDraft | null {
  const draft = readDraft(schedule.expression);
  if (!draft) return null;
  const windowStart = parseClockTime(schedule.windowStart);
  const windowEnd = parseClockTime(schedule.windowEnd);
  if (!windowStart || !windowEnd) return draft;
  return { ...draft, windowEnabled: true, windowStart, windowEnd };
}

export function toCustomSchedule(
  draft: ScheduleDraft,
  expression: string,
  timeZoneId: string
): CustomSchedule {
  return {
    expression,
    timeZoneId,
    windowStart: draft.windowEnabled ? formatClockTime(draft.windowStart) : null,
    windowEnd: draft.windowEnabled ? formatClockTime(draft.windowEnd) : null
  };
}

export function isSameSchedule(left: CustomSchedule | null, right: CustomSchedule | null): boolean {
  if (!left || !right) return left === right;
  return (
    left.expression === right.expression &&
    left.timeZoneId === right.timeZoneId &&
    left.windowStart === right.windowStart &&
    left.windowEnd === right.windowEnd
  );
}

/**
 * The parser's complaint about an expression, or null when it parses. Constructed without a
 * callback so nothing is scheduled: croner only starts a timer when it is handed a function.
 */
export function expressionError(expression: string, timeZoneId: string): string | null {
  const trimmed = expression.trim();
  if (trimmed.length === 0) return '';
  try {
    new Cron(trimmed, { timezone: timeZoneId });
    return null;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    // The parser names its own internal class in front of the part that helps ("CronPattern:
    // Invalid value for hour: 99"). The detail is worth showing to someone hand-typing an
    // expression; the class name is not something they can act on.
    return message.replace(/^CronPattern:\s*/, '');
  }
}

/**
 * Which of the five positions is itself unparseable, found by putting each one into an
 * otherwise wide-open expression and asking the parser. Marking all five red when one number
 * is wrong sends the user hunting through four correct fields; reading the parser's own
 * message to find the culprit would mean matching English prose, which breaks on the next
 * locale or the next version of the library. A position that is only wrong in combination
 * with another marks nothing, and the message underneath still explains it.
 */
export function invalidPositions(positions: readonly string[], timeZoneId: string): boolean[] {
  return CRON_POSITIONS.map((_, index) => {
    const value = positions[index] ?? '';
    if (value.length === 0) return true;
    const probe = CRON_POSITIONS.map((__, at) => (at === index ? value : '*'));
    return expressionError(joinExpression(probe), timeZoneId) !== null;
  });
}

function minutesOfDay(time: ClockTime): number {
  return time.hour * 60 + time.minute;
}

/**
 * Whether an instant falls inside the window. An end earlier than the start means the window
 * crosses midnight, so the test becomes an OR rather than an AND. No window means always true.
 */
function isWithinWindow(
  instant: Date,
  timeZoneId: string,
  start: ClockTime | null,
  end: ClockTime | null
): boolean {
  if (!start || !end) return true;
  const local = getTimeInTimezone(instant, timeZoneId);
  // Some ICU builds report midnight as hour 24 under a 24-hour cycle.
  const minutes = ((local.hour % 24) * 60 + local.minute) % MINUTES_PER_DAY;
  const from = minutesOfDay(start);
  const to = minutesOfDay(end);
  return from <= to ? minutes >= from && minutes < to : minutes >= from || minutes < to;
}

/**
 * The next occurrences that also fall inside the window, walking forward from `from`. Returns
 * fewer than `count` (possibly none) when the expression and the window never intersect; the
 * walk is bounded so that case ends rather than spinning.
 *
 * The whole walk is guarded, not just the parse. A zone id this browser's database has no entry
 * for is accepted by the parser and only fails once it is asked for a run, and the window test
 * reads the same zone through Intl, so both throw from inside the loop. This runs in a render
 * body, so an empty list is the answer rather than an exception taking the page down.
 */
export function computeNextRuns(schedule: CustomSchedule, count: number, from: Date): Date[] {
  const runs: Date[] = [];
  const trimmed = schedule.expression.trim();
  if (trimmed.length === 0) return runs;

  const start = parseClockTime(schedule.windowStart);
  const end = parseClockTime(schedule.windowEnd);
  const deadline = from.getTime() + MAX_LOOKAHEAD_DAYS * MS_PER_DAY;
  const listed = new Set<number>();
  let cursor = from;
  let examined = 0;

  try {
    const cron = new Cron(trimmed, { timezone: schedule.timeZoneId });
    while (runs.length < count && examined < MAX_CANDIDATES) {
      const batch = cron.nextRuns(OCCURRENCE_BATCH, cursor);
      if (batch.length === 0) return runs;
      for (const occurrence of batch) {
        examined += 1;
        if (occurrence.getTime() > deadline) return runs;
        // The hour a zone skips going on to daylight saving time is emitted twice for a step
        // expression, once on each side of the jump, and both land on the same instant. Listing
        // it twice reads as two runs a second apart and hands two rows the same key.
        if (listed.has(occurrence.getTime())) continue;
        listed.add(occurrence.getTime());
        if (isWithinWindow(occurrence, schedule.timeZoneId, start, end)) {
          runs.push(occurrence);
          if (runs.length === count) return runs;
        }
      }
      cursor = batch[batch.length - 1];
    }
  } catch {
    return runs;
  }
  return runs;
}

/**
 * The separator is locale data rather than a constant because Chinese lists items with the
 * enumeration comma and no trailing space, while English uses a comma and a space. Hardcoding
 * either one puts two comma conventions in a single Chinese sentence.
 */
function joinWeekdays(weekdays: readonly WeekdayKey[], t: TranslateFn): string {
  return WEEKDAY_KEYS.filter((day) => weekdays.includes(day))
    .map((day) => t(`${BASE_KEY}.weekdays.${day}`))
    .join(t(`${BASE_KEY}.weekdays.separator`));
}

/**
 * The schedule as one whole sentence rather than a fragment chain, so a language that orders
 * time before the verb can express it.
 *
 * The context names the repeat shape and whether a window applies, because a single template
 * cannot carry all of them: only the weekly one has days to name, only the monthly one has a
 * day of the month, and only the hourly one repeats within a day. i18next matches a context
 * key exactly and never falls back partway, so a missing variant lands on the plain key rather
 * than on a half-right one.
 */
export function describeSchedule(
  draft: ScheduleDraft,
  timeZoneId: string,
  t: TranslateFn,
  formatTime: (time: ClockTime) => string
): string {
  return t(`${BASE_KEY}.preview.sentence`, {
    context: draft.windowEnabled ? `${draft.repeat}_window` : draft.repeat,
    repeat: t(`${BASE_KEY}.repeat.${draft.repeat}`),
    hours: draft.everyNHours,
    minute: draft.time.minute,
    dayOfMonth: draft.dayOfMonth,
    days: draft.repeat === 'weekly' ? joinWeekdays(draft.weekdays, t) : '',
    time: formatTime(draft.time),
    windowStart: formatTime(draft.windowStart),
    windowEnd: formatTime(draft.windowEnd),
    zone: timeZoneId
  });
}

/**
 * "in 9 hours" for the first run only. Built with Intl rather than a translated template
 * because the whole phrase differs in shape by language: English leads with "in", Chinese
 * trails with a suffix, and only the formatter knows which.
 */
export function formatTimeUntil(target: Date, from: Date): string {
  const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'always', style: 'long' });
  const minutes = Math.round((target.getTime() - from.getTime()) / 60_000);
  if (Math.abs(minutes) < 60) return relative.format(minutes, 'minute');
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 48) return relative.format(hours, 'hour');
  return relative.format(Math.round(hours / 24), 'day');
}
