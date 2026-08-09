import {
  getServerTimezone,
  isUnresolvableTimezone,
  rememberUnresolvableTimezone
} from './timezone';
import { getGlobalUtcPreference } from './utcTimezonePreference';

/**
 * The absolute timestamp shapes the app renders.
 *
 * - `stamp` is the default and covers next run, last run, table cells and detail rows.
 * - `log` keeps seconds, for exports and viewers where ordering inside a minute is real resolution.
 * - `dateOnly` and `timeOnly` are for readouts that already carry the other half elsewhere.
 * - `timeSeconds` is the same resolution as `log` without the date, for a narrow fixed-width
 *   column that has no room for one but where the order of two entries inside a minute is the
 *   whole point of the column.
 * - `dateShort` is for space-constrained labels, where a spelled-out month would push a row wide
 *   enough to wrap. It still carries the year, in two digits.
 */
export type TimestampStyle =
  | 'stamp'
  | 'log'
  | 'dateOnly'
  | 'timeOnly'
  | 'timeSeconds'
  | 'dateShort';

export interface TimestampSettings {
  /** True renders in the browser's timezone, false renders in the server's. */
  useLocalTimezone: boolean;
  /**
   * True renders on the UTC clock and {@link useLocalTimezone} stops mattering. Left out, the
   * app-wide preference decides, which is what makes one switch in the header reach every
   * timestamp. Pass `false` to opt a caller out: a few of them use `useLocalTimezone: true` to
   * render a wall clock that must NOT be shifted into another zone, and UTC would shift it.
   */
  useUtc?: boolean;
  /**
   * An IANA zone to render in, whatever the two flags above say. For the callers that speak a
   * zone of their own rather than the reader's: a custom schedule stores the zone it fires in,
   * and its own fields have to agree with each other before they agree with anyone's preference.
   */
  timeZone?: string;
  use24Hour: boolean;
  /**
   * Kept because both public entry points expose it, but every style below is built on
   * `dateStyle`, and every `dateStyle` already contains the year. There is no way to ask
   * Intl for a year-less locale pattern, so the year is now always present and this flag
   * has nothing left to force.
   */
  forceYear: boolean;
  /** Defaults to `stamp`. */
  style?: TimestampStyle;
}

/**
 * Which clock to render on, and nothing else, for the formatters that work out the style and
 * `forceYear` for themselves.
 *
 * It exists so those formatters can take the preferences as an argument instead of reading module
 * state. A plain module cannot subscribe to the timezone context, so a global read leaves its text
 * showing the clock the reader just switched away from until something unrelated re-renders it.
 */
export type ReaderClock = Omit<TimestampSettings, 'style' | 'forceYear'>;

/**
 * `dateStyle` / `timeStyle` pick the pattern the locale actually writes. Asking for individual
 * fields instead makes Intl glue them together with the locale's generic separator, which in
 * German produces `7. Aug., 16:52:22` - two abbreviation periods, a comma, and no year at all.
 *
 * These presets are also the reason a whole class of runtime crash cannot happen here. Combining
 * a style with any individual field (`hour`, `month`, `second`, `weekday`, `timeZoneName`) makes
 * `Intl.DateTimeFormat` throw a TypeError, and TypeScript cannot catch it because every field of
 * `Intl.DateTimeFormatOptions` is independently optional. Callers pick a preset, never a field,
 * so no caller can assemble an illegal pair. `hour12` and `timeZone` are safe with every style.
 */
const STYLE_OPTIONS: Record<TimestampStyle, Intl.DateTimeFormatOptions> = {
  stamp: { dateStyle: 'medium', timeStyle: 'short' },
  log: { dateStyle: 'short', timeStyle: 'medium' },
  dateOnly: { dateStyle: 'medium' },
  timeOnly: { timeStyle: 'short' },
  timeSeconds: { timeStyle: 'medium' },
  dateShort: { dateStyle: 'short' }
};

/**
 * Format an absolute moment for display, in the browser's locale.
 *
 * Preferences are arguments rather than reads, so the same body serves the reactive hook and the
 * plain function without either of them keeping a copy of the Intl options.
 */
export function formatTimestamp(
  value: string | Date | null | undefined,
  settings: TimestampSettings
): string {
  if (!value) return 'N/A';

  try {
    const date = typeof value === 'string' ? new Date(value) : value;

    if (isNaN(date.getTime())) return 'Invalid Date';

    // undefined lets Intl pick the browser's own timezone. A named zone wins outright - the
    // caller has one in hand and is not asking about anybody's preference. UTC comes next
    // because it is a third clock rather than a variation on the other two, and overrides both.
    const targetTimezone =
      settings.timeZone ??
      ((settings.useUtc ?? getGlobalUtcPreference())
        ? 'UTC'
        : settings.useLocalTimezone
          ? undefined
          : getServerTimezone());

    const formatOptions: Intl.DateTimeFormatOptions = {
      ...STYLE_OPTIONS[settings.style ?? 'stamp'],
      timeZone: targetTimezone,
      hour12: !settings.use24Hour
    };

    // A zone already settled on the UTC fallback skips straight to it. A schedule stores the zone
    // it fires in, and its preview formats up to 3700 candidate days per recompute, so retrying an
    // id this runtime rejects costs a thrown Intl construction and an identical warning per
    // day. [11]
    if (isUnresolvableTimezone(targetTimezone)) {
      return date.toLocaleString(undefined, { ...formatOptions, timeZone: 'UTC' });
    }

    try {
      return date.toLocaleString(undefined, formatOptions);
    } catch (_tzError) {
      rememberUnresolvableTimezone(targetTimezone);
      return date.toLocaleString(undefined, {
        ...formatOptions,
        timeZone: 'UTC'
      });
    }
  } catch (_error) {
    return 'Invalid Date';
  }
}
