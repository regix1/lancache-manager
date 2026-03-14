import { useContext } from 'react';
import { CalendarSettingsContext } from './CalendarSettingsContext.types';

export const useCalendarSettings = () => {
  const context = useContext(CalendarSettingsContext);
  if (!context) {
    throw new Error('useCalendarSettings must be used within a CalendarSettingsProvider');
  }
  return context;
};
