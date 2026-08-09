// Explicit extension: this module is loaded directly by the node test runner, which resolves
// value imports itself and cannot fill in a missing one.
import { CLOCK_KEYS, type ClockPreferences } from '../types/userPreferences.ts';

export const DEFAULT_GUEST_PREFERENCE_KEYS = new Set([
  'useLocalTimezone',
  'useUtcTimezone',
  'use24HourFormat',
  'sharpCorners',
  'disableTooltips',
  'showDatasourceLabels'
]);

// Every key in the Set above needs a field on both shapes below. The lookup casts through
// `keyof`, so a missing field type-checks and silently reads whatever the caller happened to
// pass, which is how a key with no declared type stops being checked at all. [19]
interface DefaultGuestPreferencesSnapshot {
  useLocalTimezone: boolean;
  useUtcTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  allowedTimeFormats: string[];
}

interface SessionPrefsForGate {
  selectedTheme: string | null;
  useLocalTimezone: boolean;
  useUtcTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  allowedTimeFormats?: string[] | null;
}

function formatsEqual(a: string[] | null | undefined, b: string[] | null | undefined): boolean {
  if (!a && !b) return true;
  if (!a || !b) return false;
  if (a.length !== b.length) return false;
  return a.every((f) => b.includes(f));
}

function isUsingDefaultAllowedFormats(
  sessionFormats: string[] | null | undefined,
  previousDefaultFormats: string[]
): boolean {
  if (!sessionFormats || sessionFormats.length === 0) return true;
  return formatsEqual(sessionFormats, previousDefaultFormats);
}

function isUsingDefaultTheme(selectedTheme: string | null): boolean {
  return selectedTheme === null;
}

export function shouldApplyGuestDefaultChange(
  key: string,
  sessionPrefs: SessionPrefsForGate,
  previousDefaults: DefaultGuestPreferencesSnapshot
): boolean {
  if (key === 'selectedTheme') {
    return isUsingDefaultTheme(sessionPrefs.selectedTheme);
  }

  if (key === 'allowedTimeFormats') {
    return isUsingDefaultAllowedFormats(
      sessionPrefs.allowedTimeFormats,
      previousDefaults.allowedTimeFormats
    );
  }

  if (!DEFAULT_GUEST_PREFERENCE_KEYS.has(key)) {
    return false;
  }

  const sessionValue = sessionPrefs[key as keyof SessionPrefsForGate];
  const defaultValue = previousDefaults[key as keyof DefaultGuestPreferencesSnapshot];
  return sessionValue === defaultValue;
}

export function shouldApplyGuestClockChange(
  sessionClock: ClockPreferences,
  previousClock: ClockPreferences
): boolean {
  return CLOCK_KEYS.every((key) => sessionClock[key] === previousClock[key]);
}

/**
 * Replays clock changes received while a guest's preference request was still in flight. The order
 * matters: if defaults move A to B and then B to C before the request for A finishes, keeping only C
 * would compare A with B and incorrectly decide that the guest chose a different clock.
 */
export function applyGuestClockChanges<T extends ClockPreferences>(
  sessionClock: T,
  changes: readonly { clock: ClockPreferences; previousClock: ClockPreferences }[]
): T {
  let settled = sessionClock;
  for (const change of changes) {
    if (shouldApplyGuestClockChange(settled, change.previousClock)) {
      settled = { ...settled, ...change.clock };
    }
  }
  return settled;
}
