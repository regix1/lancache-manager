import React, { createContext, useContext, useState, useEffect, useSyncExternalStore, useCallback, useMemo } from 'react';
import preferencesService from '@services/preferences.service';
import { setGlobalTimezonePreference } from '@utils/timezonePreference';
import { setGlobal24HourPreference } from '@utils/timeFormatPreference';
import { setGlobalAlwaysShowYearPreference, getGlobalAlwaysShowYearPreference } from '@utils/yearDisplayPreference';
import {
  setPendingTimezone,
  subscribe as subscribeToPending,
  getPendingValue
} from '@utils/pendingPreferences';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

interface TimezoneContextType {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  refreshKey: number;
  setPendingTimeSetting: (value: TimeSettingValue | null) => void;
  forceRefresh: () => void;
}

const TimezoneContext = createContext<TimezoneContextType>({
  useLocalTimezone: false,
  use24HourFormat: true,
  refreshKey: 0,
  setPendingTimeSetting: () => {},
  forceRefresh: () => {}
});

export const useTimezone = () => useContext(TimezoneContext);

export const TimezoneProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [actualUseLocal, setActualUseLocal] = useState(false);
  const [actualUse24Hour, setActualUse24Hour] = useState(true);
  const [refreshKey, setRefreshKey] = useState(0);

  // Subscribe to pending preference changes for immediate UI updates
  const pendingUseLocal = useSyncExternalStore(
    subscribeToPending,
    () => getPendingValue<boolean>('useLocalTimezone')
  );
  const pendingUse24Hour = useSyncExternalStore(
    subscribeToPending,
    () => getPendingValue<boolean>('use24HourFormat')
  );

  // Derive effective values: pending takes precedence over actual
  const useLocalTimezone = pendingUseLocal ?? actualUseLocal;
  const use24HourFormat = pendingUse24Hour ?? actualUse24Hour;

  // Load initial preferences
  useEffect(() => {
    const load = async () => {
      const prefs = await preferencesService.getPreferences();
      setActualUseLocal(prefs.useLocalTimezone);
      setActualUse24Hour(prefs.use24HourFormat);
      setGlobalTimezonePreference(prefs.useLocalTimezone);
      setGlobal24HourPreference(prefs.use24HourFormat);
      setGlobalAlwaysShowYearPreference(prefs.showYearInDates ?? false);
    };
    load();
  }, []);

  // Listen for preference changes from SignalR
  useEffect(() => {
    const handleChange = (e: Event) => {
      const { key, value } = (e as CustomEvent<{ key: string; value: boolean }>).detail;

      if (key === 'useLocalTimezone') {
        setActualUseLocal(prev => {
          if (prev !== value) {
            setGlobalTimezonePreference(value);
            // Only increment refreshKey if no pending value (pending handles immediate UI)
            if (pendingUseLocal === null) setRefreshKey(k => k + 1);
            return value;
          }
          return prev;
        });
      }

      if (key === 'use24HourFormat') {
        setActualUse24Hour(prev => {
          if (prev !== value) {
            setGlobal24HourPreference(value);
            if (pendingUse24Hour === null) setRefreshKey(k => k + 1);
            return value;
          }
          return prev;
        });
      }

      if (key === 'showYearInDates') {
        const current = getGlobalAlwaysShowYearPreference();
        if (current !== value) {
          setGlobalAlwaysShowYearPreference(value);
          setRefreshKey(k => k + 1);
        }
      }
    };

    window.addEventListener('preference-changed', handleChange);
    return () => window.removeEventListener('preference-changed', handleChange);
  }, [pendingUseLocal, pendingUse24Hour]);

  const setPendingTimeSetting = useCallback((value: TimeSettingValue | null) => {
    setPendingTimezone(value);
  }, []);

  const forceRefresh = useCallback(() => {
    setRefreshKey(k => k + 1);
  }, []);

  const contextValue = useMemo(() => ({
    useLocalTimezone,
    use24HourFormat,
    refreshKey,
    setPendingTimeSetting,
    forceRefresh
  }), [useLocalTimezone, use24HourFormat, refreshKey, setPendingTimeSetting, forceRefresh]);

  return (
    <TimezoneContext.Provider value={contextValue}>
      {children}
    </TimezoneContext.Provider>
  );
};
