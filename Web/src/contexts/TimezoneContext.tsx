import React, { createContext, useContext, useState, useEffect, useSyncExternalStore, useCallback, useMemo } from 'react';
import preferencesService from '@services/preferences.service';
import { setGlobalTimezonePreference } from '@utils/timezonePreference';
import { setGlobal24HourPreference } from '@utils/timeFormatPreference';
import { setGlobalAlwaysShowYearPreference, getGlobalAlwaysShowYearPreference } from '@utils/yearDisplayPreference';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

// External store for pending selection - prevents flicker when both preferences
// are updated separately via SignalR by providing stable values during transition
let pendingTimeSetting: TimeSettingValue | null = null;
const pendingListeners = new Set<() => void>();

const pendingStore = {
  subscribe: (listener: () => void) => {
    pendingListeners.add(listener);
    return () => { pendingListeners.delete(listener); };
  },
  getSnapshot: () => pendingTimeSetting,
  set: (value: TimeSettingValue | null) => {
    if (pendingTimeSetting === value) return;
    pendingTimeSetting = value;
    pendingListeners.forEach((l) => l());
  }
};

interface TimezoneContextType {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  refreshKey: number;
  setPendingTimeSetting: (value: TimeSettingValue | null) => void;
}

const TimezoneContext = createContext<TimezoneContextType>({
  useLocalTimezone: false,
  use24HourFormat: true,
  refreshKey: 0,
  setPendingTimeSetting: () => {}
});

export const useTimezone = () => useContext(TimezoneContext);

export const TimezoneProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [actualUseLocalTimezone, setActualUseLocalTimezone] = useState(false);
  const [actualUse24HourFormat, setActualUse24HourFormat] = useState(true);
  const [refreshKey, setRefreshKey] = useState(0);
  const pendingChange = useSyncExternalStore(pendingStore.subscribe, pendingStore.getSnapshot);

  // Derive effective values from pending (if set) or actual state
  const useLocalTimezone = pendingChange !== null
    ? pendingChange.startsWith('local')
    : actualUseLocalTimezone;
  const use24HourFormat = pendingChange !== null
    ? pendingChange.endsWith('24h')
    : actualUse24HourFormat;

  // Clear pending when actual values catch up
  useEffect(() => {
    if (pendingChange !== null) {
      const expectedUseLocal = pendingChange.startsWith('local');
      const expectedUse24Hour = pendingChange.endsWith('24h');
      if (actualUseLocalTimezone === expectedUseLocal && actualUse24HourFormat === expectedUse24Hour) {
        // Just clear pending - no refreshKey increment needed since
        // effective values are already correct (derived from pending)
        pendingStore.set(null);
      }
    }
  }, [actualUseLocalTimezone, actualUse24HourFormat, pendingChange]);

  useEffect(() => {
    // Load initial preferences
    const loadPreferences = async () => {
      const prefs = await preferencesService.getPreferences();
      setActualUseLocalTimezone(prefs.useLocalTimezone);
      setActualUse24HourFormat(prefs.use24HourFormat);
      setGlobalTimezonePreference(prefs.useLocalTimezone);
      setGlobal24HourPreference(prefs.use24HourFormat);
      setGlobalAlwaysShowYearPreference(prefs.showYearInDates ?? false);
    };
    loadPreferences();

    // Listen for preference changes from SignalR
    const handlePreferenceChange = (event: Event) => {
      const customEvent = event as CustomEvent<{ key: string; value: boolean }>;
      const { key, value } = customEvent.detail;

      // When there's a pending change, silently update actual values
      // without triggering refreshKey (the effective values come from pending)
      if (pendingTimeSetting !== null && (key === 'useLocalTimezone' || key === 'use24HourFormat')) {
        if (key === 'useLocalTimezone') {
          setActualUseLocalTimezone((prev) => {
            if (prev !== value) {
              setGlobalTimezonePreference(value);
              return value;
            }
            return prev;
          });
        } else {
          setActualUse24HourFormat((prev) => {
            if (prev !== value) {
              setGlobal24HourPreference(value);
              return value;
            }
            return prev;
          });
        }
        return;
      }

      if (key === 'useLocalTimezone') {
        setActualUseLocalTimezone((prev) => {
          if (prev !== value) {
            setGlobalTimezonePreference(value);
            setRefreshKey((prevKey) => prevKey + 1);
            return value;
          }
          return prev;
        });
      }

      if (key === 'use24HourFormat') {
        setActualUse24HourFormat((prev) => {
          if (prev !== value) {
            setGlobal24HourPreference(value);
            setRefreshKey((prevKey) => prevKey + 1);
            return value;
          }
          return prev;
        });
      }

      // Handle showYearInDates preference change - only update if value actually changed
      if (key === 'showYearInDates') {
        const currentValue = getGlobalAlwaysShowYearPreference();
        if (currentValue !== value) {
          setGlobalAlwaysShowYearPreference(value);
          setRefreshKey((prevKey) => prevKey + 1);
        }
      }
    };

    window.addEventListener('preference-changed', handlePreferenceChange);
    return () => window.removeEventListener('preference-changed', handlePreferenceChange);
  }, []);

  const setPendingTimeSetting = useCallback((value: TimeSettingValue | null) => {
    pendingStore.set(value);
  }, []);

  const contextValue = useMemo(() => ({
    useLocalTimezone,
    use24HourFormat,
    refreshKey,
    setPendingTimeSetting
  }), [useLocalTimezone, use24HourFormat, refreshKey, setPendingTimeSetting]);

  return (
    <TimezoneContext.Provider value={contextValue}>
      {children}
    </TimezoneContext.Provider>
  );
};
