import React, { useState, useEffect, type ReactNode } from 'react';
import { storage } from '@utils/storage';
import {
  CalendarSettingsContext,
  type EventOpacity,
  type EventDisplayStyle,
  type WeekStartDay
} from './CalendarSettingsContext.types';

interface CalendarSettings {
  // Event appearance
  eventOpacity: EventOpacity;
  eventDisplayStyle: EventDisplayStyle;

  // Calendar view options
  weekStartDay: WeekStartDay;
  showWeekNumbers: boolean;
  showAdjacentMonths: boolean;
  hideEndedEvents: boolean;

  // Density/size
  compactMode: boolean;
}

const DEFAULT_SETTINGS: CalendarSettings = {
  eventOpacity: 'transparent',
  eventDisplayStyle: 'spanning',
  weekStartDay: 'sunday',
  showWeekNumbers: false,
  showAdjacentMonths: true,
  hideEndedEvents: false,
  compactMode: false
};

const STORAGE_KEY = 'lancache_calendar_settings';

interface CalendarSettingsProviderProps {
  children: ReactNode;
}

export const CalendarSettingsProvider: React.FC<CalendarSettingsProviderProps> = ({ children }) => {
  const [settings, setSettings] = useState<CalendarSettings>(() => {
    const saved = storage.getJSON<CalendarSettings>(STORAGE_KEY);
    if (saved) {
      // Merge saved settings with defaults to handle new settings
      return { ...DEFAULT_SETTINGS, ...saved };
    }
    return DEFAULT_SETTINGS;
  });

  // Persist settings to localStorage
  useEffect(() => {
    storage.setJSON(STORAGE_KEY, settings);
  }, [settings]);

  const updateSettings = (updates: Partial<CalendarSettings>) => {
    setSettings((prev) => ({ ...prev, ...updates }));
  };

  const resetSettings = () => {
    setSettings(DEFAULT_SETTINGS);
  };

  return (
    <CalendarSettingsContext.Provider
      value={{
        settings,
        updateSettings,
        resetSettings
      }}
    >
      {children}
    </CalendarSettingsContext.Provider>
  );
};
