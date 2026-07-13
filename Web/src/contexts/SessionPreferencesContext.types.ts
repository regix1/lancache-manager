import { createContext } from 'react';
import type { UserPreferences } from '@/types/userPreferences';

interface SessionPreferencesContextType {
  getSessionPreferences: (sessionId: string) => UserPreferences | null;
  currentPreferences: UserPreferences | null;
  isLoaded: (sessionId: string) => boolean;
  isLoading: (sessionId: string) => boolean;
  loadSessionPreferences: (sessionId: string) => Promise<void>;
  setOptimisticPreference: <K extends keyof UserPreferences>(
    key: K,
    value: UserPreferences[K]
  ) => void;
  updateSessionPreference: <K extends keyof UserPreferences>(
    sessionId: string,
    key: K,
    value: UserPreferences[K]
  ) => void;
}

export const SessionPreferencesContext = createContext<SessionPreferencesContextType | null>(null);
