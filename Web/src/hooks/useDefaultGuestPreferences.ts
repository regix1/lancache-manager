import { useState, useEffect, useCallback } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext';

interface DefaultGuestPreferences {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
  allowedTimeFormats: string[];
}

const defaultPrefs: DefaultGuestPreferences = {
  useLocalTimezone: false,
  use24HourFormat: true,
  sharpCorners: false,
  disableTooltips: false,
  showDatasourceLabels: true,
  showYearInDates: false,
  allowedTimeFormats: ['server-24h', 'server-12h', 'local-24h', 'local-12h']
};

// Global cache for default guest preferences
let cachedPrefs: DefaultGuestPreferences = { ...defaultPrefs };
let loaded = false;
const listeners = new Set<() => void>();

const notifyListeners = () => {
  listeners.forEach((listener) => listener());
};

export const useDefaultGuestPreferences = () => {
  const { on, off } = useSignalR();
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
          use24HourFormat: data.use24HourFormat ?? true,
          sharpCorners: data.sharpCorners ?? false,
          disableTooltips: data.disableTooltips ?? false,
          showDatasourceLabels: data.showDatasourceLabels ?? true,
          showYearInDates: data.showYearInDates ?? false,
          allowedTimeFormats: data.allowedTimeFormats ?? [
            'server-24h',
            'server-12h',
            'local-24h',
            'local-12h'
          ]
        };
        loaded = true;
        setPrefs(cachedPrefs);
        notifyListeners();
      }
    } catch (err) {
      console.error('Failed to load default guest preferences:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  // Listen for SignalR updates to default guest preferences
  const handleDefaultGuestPreferencesChanged = useCallback(
    (data: { key: string; value: boolean }) => {
      cachedPrefs = {
        ...cachedPrefs,
        [data.key]: data.value
      };
      setPrefs(cachedPrefs);
      notifyListeners();
    },
    []
  );

  // Listen for SignalR updates to allowed time formats
  const handleAllowedTimeFormatsChanged = useCallback((data: { formats: string[] }) => {
    cachedPrefs = {
      ...cachedPrefs,
      allowedTimeFormats: data.formats
    };
    setPrefs(cachedPrefs);
    notifyListeners();
  }, []);

  useEffect(() => {
    // Subscribe to global updates
    const listener = () => setPrefs({ ...cachedPrefs });
    listeners.add(listener);

    // Load if not already loaded
    if (!loaded) {
      loadPreferences();
    }

    // Listen for SignalR updates
    on('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
    on('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);

    return () => {
      listeners.delete(listener);
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
    };
  }, [
    loadPreferences,
    on,
    off,
    handleDefaultGuestPreferencesChanged,
    handleAllowedTimeFormatsChanged
  ]);

  return { prefs, loading };
};
