import React, { createContext, useContext, useState, useEffect } from 'react';
import preferencesService from '@services/preferences.service';
import { setGlobalTimezonePreference } from '@utils/timezonePreference';

interface TimezoneContextType {
  useLocalTimezone: boolean;
  refreshKey: number; // Increment this to force re-renders
}

const TimezoneContext = createContext<TimezoneContextType>({
  useLocalTimezone: false,
  refreshKey: 0
});

export const useTimezone = () => useContext(TimezoneContext);

export const TimezoneProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [useLocalTimezone, setUseLocalTimezone] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    // Load initial preference
    const loadPreference = async () => {
      const prefs = await preferencesService.getPreferences();
      setUseLocalTimezone(prefs.useLocalTimezone);
      setGlobalTimezonePreference(prefs.useLocalTimezone); // Set global state
    };
    loadPreference();

    // Listen for timezone preference changes
    const handlePreferenceChange = (event: any) => {
      const { key, value } = event.detail;
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
    };

    window.addEventListener('preference-changed', handlePreferenceChange);
    return () => window.removeEventListener('preference-changed', handlePreferenceChange);
  }, []);

  return (
    <TimezoneContext.Provider value={{ useLocalTimezone, refreshKey }}>
      {children}
    </TimezoneContext.Provider>
  );
};
