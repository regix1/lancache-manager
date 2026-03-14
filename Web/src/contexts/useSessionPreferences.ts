import { useContext } from 'react';
import { SessionPreferencesContext } from './SessionPreferencesContext.types';

export const useSessionPreferences = () => {
  const context = useContext(SessionPreferencesContext);
  if (!context) {
    throw new Error('useSessionPreferences must be used within a SessionPreferencesProvider');
  }
  return context;
};
