import { getServerTimezone } from './timezone';

/**
 * The absolute timestamp shapes the app renders.
 *
 * - `stamp` is the default and covers next run, last run, table cells and detail rows.
 * - `log` keeps seconds, for exports and viewers where ordering inside a minute is real resolution.
 * - `dateOnly` and `timeOnly` are for readouts that already carry the other half elsewhere.
 * - `dateShort` is for space-constrained labels, where a spelled-out month would push a row wide
 *   enough to wrap. It still carries the year, in two digits.
 */
export type TimestampStyle = 'stamp' | 'log' | 'dateOnly' | 'timeOnly' | 'dateShort';

export interface TimestampSettings {
  /** True renders in the browser's timezone, false renders in the server's. */
  useLocalTimezone: boolean;
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

    // undefined lets Intl pick the browser's own timezone
    const targetTimezone = settings.useLocalTimezone ? undefined : getServerTimezone();

    const formatOptions: Intl.DateTimeFormatOptions = {
      ...STYLE_OPTIONS[settings.style ?? 'stamp'],
      timeZone: targetTimezone,
      hour12: !settings.use24Hour
    };

    try {
      return date.toLocaleString(undefined, formatOptions);
    } catch (_tzError) {
      // Timezone invalid, fall back to UTC
      console.warn(`Invalid timezone "${targetTimezone}", falling back to UTC`);
      return date.toLocaleString(undefined, {
        ...formatOptions,
        timeZone: 'UTC'
      });
    }
  } catch (_error) {
    return 'Invalid Date';
  }
}
