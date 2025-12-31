import React, { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import { storage } from '@utils/storage';

export type WeekStartDay = 'sunday' | 'monday';
export type EventOpacity = 'transparent' | 'solid';
export type EventDisplayStyle = 'spanning' | 'daily';

export interface CalendarSettings {
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
  compactMode: false,
};

const STORAGE_KEY = 'lancache_calendar_settings';

interface CalendarSettingsContextType {
  settings: CalendarSettings;
  updateSettings: (updates: Partial<CalendarSettings>) => void;
  resetSettings: () => void;
}

const CalendarSettingsContext = createContext<CalendarSettingsContextType | undefined>(undefined);

export const useCalendarSettings = () => {
  const context = useContext(CalendarSettingsContext);
  if (!context) {
    throw new Error('useCalendarSettings must be used within a CalendarSettingsProvider');
  }
  return context;
};

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
    setSettings(prev => ({ ...prev, ...updates }));
  };

  const resetSettings = () => {
    setSettings(DEFAULT_SETTINGS);
  };

  return (
    <CalendarSettingsContext.Provider
      value={{
        settings,
        updateSettings,
        resetSettings,
      }}
    >
      {children}
    </CalendarSettingsContext.Provider>
  );
};
