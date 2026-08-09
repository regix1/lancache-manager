import React, { useState, useEffect, useSyncExternalStore, useCallback, useMemo } from 'react';
import { useSessionPreferences } from './useSessionPreferences';
import { setGlobalTimezonePreference } from '@utils/timezonePreference';
import { setGlobalUtcPreference } from '@utils/utcTimezonePreference';
import {
  setPendingTimezone,
  dropPendingTimezone,
  subscribe as subscribeToPending,
  getPendingValue
} from '@utils/pendingPreferences';
import { TimezoneContext, type TimeSettingValue } from './TimezoneContext.types';
import { APP_EVENTS } from '@utils/constants';

export const TimezoneProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { currentPreferences } = useSessionPreferences();
  const [actualUseLocal, setActualUseLocal] = useState(false);
  const [actualUseUtc, setActualUseUtc] = useState(false);
  const [actualUse24Hour, setActualUse24Hour] = useState(true);
  const [refreshKey, setRefreshKey] = useState(0);

  // Subscribe to pending preference changes for immediate UI updates
  const pendingUseLocal = useSyncExternalStore(subscribeToPending, () =>
    getPendingValue<boolean>('useLocalTimezone')
  );
  const pendingUseUtc = useSyncExternalStore(subscribeToPending, () =>
    getPendingValue<boolean>('useUtcTimezone')
  );
  const pendingUse24Hour = useSyncExternalStore(subscribeToPending, () =>
    getPendingValue<boolean>('use24HourFormat')
  );

  // Derive effective values: pending takes precedence over actual
  const useLocalTimezone = pendingUseLocal ?? actualUseLocal;
  const useUtcTimezone = pendingUseUtc ?? actualUseUtc;
  const use24HourFormat = pendingUse24Hour ?? actualUse24Hour;

  // Initialize from SessionPreferencesContext when preferences are loaded
  useEffect(() => {
    if (currentPreferences) {
      setActualUseLocal(currentPreferences.useLocalTimezone);
      setActualUseUtc(currentPreferences.useUtcTimezone);
      setActualUse24Hour(currentPreferences.use24HourFormat);
      // The two timezone flags keep a module-level mirror because getEffectiveTimezone is called
      // from plain modules that cannot read this context. The 24-hour flag needs no mirror: it
      // only ever reaches Intl through a TimestampSettings a caller built from this context.
      setGlobalTimezonePreference(currentPreferences.useLocalTimezone);
      setGlobalUtcPreference(currentPreferences.useUtcTimezone);
    }
  }, [currentPreferences]);

  // Listen for preference changes from SignalR
  useEffect(() => {
    const handleChange = (e: Event) => {
      const { key, value } = (e as CustomEvent<{ key: string; value: boolean }>).detail;

      if (key === 'useLocalTimezone') {
        setActualUseLocal((prev) => {
          if (prev !== value) {
            setGlobalTimezonePreference(value);
            // Only increment refreshKey if no pending value (pending handles immediate UI)
            if (pendingUseLocal === null) setRefreshKey((k) => k + 1);
            return value;
          }
          return prev;
        });
      }

      if (key === 'useUtcTimezone') {
        setActualUseUtc((prev) => {
          if (prev !== value) {
            setGlobalUtcPreference(value);
            if (pendingUseUtc === null) setRefreshKey((k) => k + 1);
            return value;
          }
          return prev;
        });
      }

      if (key === 'use24HourFormat') {
        setActualUse24Hour((prev) => {
          if (prev !== value) {
            if (pendingUse24Hour === null) setRefreshKey((k) => k + 1);
            return value;
          }
          return prev;
        });
      }
    };

    window.addEventListener(APP_EVENTS.PREFERENCE_CHANGED, handleChange);
    return () => window.removeEventListener(APP_EVENTS.PREFERENCE_CHANGED, handleChange);
  }, [pendingUseLocal, pendingUseUtc, pendingUse24Hour]);

  const setPendingTimeSetting = useCallback((value: TimeSettingValue) => {
    setPendingTimezone(value);
  }, []);

  const dropPendingTimeSetting = useCallback((value: TimeSettingValue) => {
    dropPendingTimezone(value);
  }, []);

  const contextValue = useMemo(
    () => ({
      useLocalTimezone,
      useUtcTimezone,
      use24HourFormat,
      refreshKey,
      setPendingTimeSetting,
      dropPendingTimeSetting
    }),
    [
      useLocalTimezone,
      useUtcTimezone,
      use24HourFormat,
      refreshKey,
      setPendingTimeSetting,
      dropPendingTimeSetting
    ]
  );

  return <TimezoneContext.Provider value={contextValue}>{children}</TimezoneContext.Provider>;
};
