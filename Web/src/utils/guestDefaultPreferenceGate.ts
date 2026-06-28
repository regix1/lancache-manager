export const DEFAULT_GUEST_PREFERENCE_KEYS = new Set([
  'useLocalTimezone',
  'use24HourFormat',
  'sharpCorners',
  'disableTooltips',
  'showDatasourceLabels',
  'showYearInDates'
]);

interface DefaultGuestPreferencesSnapshot {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
  allowedTimeFormats: string[];
}

interface SessionPrefsForGate {
  selectedTheme: string | null;
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
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
