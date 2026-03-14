import { createContext } from 'react';

interface UserPreferences {
  selectedTheme: string | null;
  sharpCorners: boolean;
  disableFocusOutlines: boolean;
  disableTooltips: boolean;
  picsAlwaysVisible: boolean;
  disableStickyNotifications: boolean;
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
  refreshRate?: string | null;
  refreshRateLocked?: boolean | null;
  allowedTimeFormats?: string[] | null;
}

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
