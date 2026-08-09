import { useState, useEffect, useCallback } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { TIME_SETTING_VALUES } from '@contexts/TimezoneContext.types';
import type {
  AllowedTimeFormatsChangedEvent,
  DefaultGuestPreferencesChangedEvent
} from '@contexts/SignalRContext/types';
import { useErrorHandler } from './useErrorHandler';

export interface DefaultGuestPreferences {
  useLocalTimezone: boolean;
  useUtcTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  allowedTimeFormats: string[];
}

const defaultPrefs: DefaultGuestPreferences = {
  useLocalTimezone: false,
  useUtcTimezone: false,
  use24HourFormat: true,
  sharpCorners: false,
  disableTooltips: false,
  showDatasourceLabels: true,
  allowedTimeFormats: [...TIME_SETTING_VALUES]
};

// Global cache for default guest preferences
let cachedPrefs: DefaultGuestPreferences = { ...defaultPrefs };
let loaded = false;
const listeners = new Set<() => void>();

const notifyListeners = () => {
  listeners.forEach((listener) => listener());
};

export function getCachedDefaultGuestPreferences(): DefaultGuestPreferences {
  return cachedPrefs;
}

/** The guest defaults either side of one change, so no consumer has to guess at the older one. */
interface DefaultGuestPreferencesChange {
  previous: DefaultGuestPreferences;
  next: DefaultGuestPreferences;
}

/**
 * Fold one broadcast into the cached defaults and hand back the pair either side of it.
 *
 * The defaults as they stood a moment ago exist only here, and only until this line runs. A second
 * listener that reads the cache to answer "was the guest still on the old default" gets whichever
 * snapshot the hub's insertion-ordered handler set happened to leave it, which is a different answer
 * depending on which component mounted first. Returning both snapshots from the one call that moves
 * them is what makes the answer independent of that. [4]
 */
export const applyDefaultGuestPreferencesChange = (
  data: DefaultGuestPreferencesChangedEvent
): DefaultGuestPreferencesChange => {
  const previous = cachedPrefs;
  cachedPrefs =
    data.key === 'clock' ? { ...previous, ...data.clock } : { ...previous, [data.key]: data.value };
  notifyListeners();
  return { previous, next: cachedPrefs };
};

export const applyAllowedTimeFormatsChange = (
  data: AllowedTimeFormatsChangedEvent
): DefaultGuestPreferencesChange => {
  const previous = cachedPrefs;
  cachedPrefs = { ...previous, allowedTimeFormats: data.formats };
  notifyListeners();
  return { previous, next: cachedPrefs };
};

export const useDefaultGuestPreferences = () => {
  const { hasSession, isLoading: authLoading } = useAuth();
  const { notifyError } = useErrorHandler();
  const [prefs, setPrefs] = useState<DefaultGuestPreferences>(cachedPrefs);
  const [loading, setLoading] = useState(!loaded);

  const loadPreferences = useCallback(async () => {
    try {
      const response = await fetch(
        '/api/system/default-guest-preferences',
        ApiService.getFetchOptions()
      );
      if (response.ok) {
        const data = await response.json();
        cachedPrefs = {
          useLocalTimezone: data.useLocalTimezone ?? false,
          useUtcTimezone: data.useUtcTimezone ?? false,
          use24HourFormat: data.use24HourFormat ?? true,
          sharpCorners: data.sharpCorners ?? false,
          disableTooltips: data.disableTooltips ?? false,
          showDatasourceLabels: data.showDatasourceLabels ?? true,
          allowedTimeFormats: data.allowedTimeFormats ?? [...TIME_SETTING_VALUES]
        };
        loaded = true;
        setPrefs(cachedPrefs);
        notifyListeners();
      }
    } catch (err) {
      // Background load: cachedPrefs already holds a sane default, so this fails quietly.
      notifyError('Failed to load default guest preferences', err, {
        silent: true,
        logLabel: 'useDefaultGuestPreferences loadPreferences'
      });
    } finally {
      setLoading(false);
    }
  }, [notifyError]);

  // The broadcasts that move these defaults are received once, by SessionPreferencesProvider, which
  // folds them in through applyDefaultGuestPreferencesChange and wakes the subscription below. A
  // second subscription here would be a second writer of the same cache, and which of the two ran
  // first would decide what the other one read. [4]
  useEffect(() => {
    // Subscribe to global updates
    const listener = () => setPrefs({ ...cachedPrefs });
    listeners.add(listener);

    // This endpoint requires a session, so defer until auth has settled.
    if (!authLoading && !hasSession) {
      setLoading(false);
    } else if (!authLoading && hasSession && !loaded) {
      loadPreferences();
    }

    return () => {
      listeners.delete(listener);
    };
  }, [loadPreferences, authLoading, hasSession]);

  return { prefs, loading };
};
