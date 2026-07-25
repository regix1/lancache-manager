import { SessionPreferencesContext } from './SessionPreferencesContext.types';
import { createContextHook } from './createContextHook';

export const useSessionPreferences = createContextHook(
  SessionPreferencesContext,
  'useSessionPreferences'
);
