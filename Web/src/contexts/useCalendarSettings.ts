import { CalendarSettingsContext } from './CalendarSettingsContext.types';
import { createContextHook } from './createContextHook';

export const useCalendarSettings = createContextHook(
  CalendarSettingsContext,
  'useCalendarSettings'
);
