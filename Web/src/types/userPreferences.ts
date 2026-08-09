/**
 * The three flags that together name one clock. None of them describes a state on its own, so they
 * are read, sent and broadcast as one tuple: two of the three written apart leave a row naming a
 * clock nobody chose.
 */
export interface ClockPreferences {
  useUtcTimezone: boolean;
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
}

/**
 * The clock keys in the one order every caller uses to track, compare and replay the tuple. Kept
 * next to the shape so a fourth flag cannot be added to the type and missed by a caller.
 */
export const CLOCK_KEYS: readonly (keyof ClockPreferences)[] = [
  'useUtcTimezone',
  'useLocalTimezone',
  'use24HourFormat'
];

export interface UserPreferences {
  selectedTheme: string | null;
  sharpCorners: boolean;
  disableFocusOutlines: boolean;
  disableTooltips: boolean;
  picsAlwaysVisible: boolean;
  disableStickyNotifications: boolean;
  useLocalTimezone: boolean;
  /** Reads every time on the UTC clock, whatever useLocalTimezone says. */
  useUtcTimezone: boolean;
  use24HourFormat: boolean;
  showDatasourceLabels: boolean;
  refreshRate?: string | null;
  refreshRateLocked?: boolean | null;
  allowedTimeFormats?: string[] | null;
  // Per-session prefill thread limits. Steam and Epic are tracked separately
  // because each service enforces its own limit. null = use the system default.
  steamMaxThreadCount?: number | null;
  epicMaxThreadCount?: number | null;
}

/**
 * Default preferences used until a session-specific value is available.
 */
export const DEFAULT_PREFERENCES: UserPreferences = {
  selectedTheme: null,
  sharpCorners: false,
  disableFocusOutlines: true,
  disableTooltips: false,
  picsAlwaysVisible: false,
  disableStickyNotifications: false,
  useLocalTimezone: false,
  useUtcTimezone: false,
  use24HourFormat: true,
  showDatasourceLabels: true,
  refreshRate: null,
  refreshRateLocked: null,
  allowedTimeFormats: null
};
