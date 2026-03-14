import { createContext } from 'react';

export type WeekStartDay = 'sunday' | 'monday';
export type EventOpacity = 'transparent' | 'solid';
export type EventDisplayStyle = 'spanning' | 'daily';

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

interface CalendarSettingsContextType {
  settings: CalendarSettings;
  updateSettings: (updates: Partial<CalendarSettings>) => void;
  resetSettings: () => void;
}

export const CalendarSettingsContext = createContext<CalendarSettingsContextType | undefined>(
  undefined
);
