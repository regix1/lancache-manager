import React, { createContext, useContext, useState, useEffect } from 'react';
import preferencesService from '@services/preferences.service';
import { setGlobalTimezonePreference } from '@utils/timezonePreference';
import { setGlobal24HourPreference } from '@utils/timeFormatPreference';

interface TimezoneContextType {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  refreshKey: number; // Increment this to force re-renders
}

const TimezoneContext = createContext<TimezoneContextType>({
  useLocalTimezone: false,
  use24HourFormat: true,
  refreshKey: 0
});

export const useTimezone = () => useContext(TimezoneContext);

export const TimezoneProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [useLocalTimezone, setUseLocalTimezone] = useState(false);
  const [use24HourFormat, setUse24HourFormat] = useState(true);
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    // Load initial preferences
    const loadPreferences = async () => {
      const prefs = await preferencesService.getPreferences();
      setUseLocalTimezone(prefs.useLocalTimezone);
      setUse24HourFormat(prefs.use24HourFormat);
      setGlobalTimezonePreference(prefs.useLocalTimezone); // Set global state
      setGlobal24HourPreference(prefs.use24HourFormat); // Set global state
    };
    loadPreferences();

    // Listen for preference changes
    const handlePreferenceChange = (event: Event) => {
      const customEvent = event as CustomEvent<{ key: string; value: boolean }>;
      const { key, value } = customEvent.detail;

      if (key === 'useLocalTimezone') {
        // Only update if value actually changed
        setUseLocalTimezone((prev) => {
          if (prev !== value) {
            console.log('[TimezoneContext] Timezone preference changed, forcing re-render');
            setGlobalTimezonePreference(value); // Update global state
            // Increment refreshKey to force all components using timestamps to re-render
            setRefreshKey((prevKey) => prevKey + 1);
            return value;
          }
          return prev;
        });
      }

      if (key === 'use24HourFormat') {
        // Only update if value actually changed
        setUse24HourFormat((prev) => {
          if (prev !== value) {
            console.log('[TimezoneContext] Time format preference changed, forcing re-render');
            setGlobal24HourPreference(value); // Update global state
            // Increment refreshKey to force all components using timestamps to re-render
            setRefreshKey((prevKey) => prevKey + 1);
            return value;
          }
          return prev;
        });
      }
    };

    window.addEventListener('preference-changed', handlePreferenceChange);
    return () => window.removeEventListener('preference-changed', handlePreferenceChange);
  }, []);

  return (
    <TimezoneContext.Provider value={{ useLocalTimezone, use24HourFormat, refreshKey }}>
      {children}
    </TimezoneContext.Provider>
  );
};
