import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  useCallback,
  useMemo,
  useRef
} from 'react';
import { useSignalR } from './SignalRContext';
import { useAuth } from './AuthContext';
import ApiService from '@services/api.service';
import type { UserPreferencesUpdatedEvent } from './SignalRContext/types';
import {
  getCorrectedTimezone,
  getCorrectedValue,
  hasPendingPreference
} from '@utils/pendingPreferences';

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

const DEFAULT_PREFERENCES: UserPreferences = {
  selectedTheme: null,
  sharpCorners: false,
  disableFocusOutlines: true,
  disableTooltips: false,
  picsAlwaysVisible: false,
  disableStickyNotifications: false,
  useLocalTimezone: false,
  use24HourFormat: true,
  showDatasourceLabels: true,
  showYearInDates: false,
  refreshRate: null,
  refreshRateLocked: null,
  allowedTimeFormats: null
};

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

const SessionPreferencesContext = createContext<SessionPreferencesContextType | null>(null);

export const useSessionPreferences = () => {
  const context = useContext(SessionPreferencesContext);
  if (!context) {
    throw new Error('useSessionPreferences must be used within a SessionPreferencesProvider');
  }
  return context;
};

export const SessionPreferencesProvider: React.FC<{ children: React.ReactNode }> = ({
  children
}) => {
  const [preferences, setPreferences] = useState<Record<string, UserPreferences>>({});
  const loadingIds = useRef<Set<string>>(new Set());
  const loadedIds = useRef<Set<string>>(new Set());
  const failedIds = useRef<Set<string>>(new Set());
  const preferencesRef = useRef<Record<string, UserPreferences>>({});
  const initialLoadDone = useRef(false);

  const { on, off } = useSignalR();
  const { isAdmin, hasSession, sessionId: authSessionId, isLoading: authLoading } = useAuth();

  const getCurrentSessionId = useCallback((): string | null => {
    return authSessionId ?? null;
  }, [authSessionId]);

  useEffect(() => {
    preferencesRef.current = preferences;
  }, [preferences]);

  const loadSessionPreferences = useCallback(
    async (sessionId: string) => {
      if (
        loadingIds.current.has(sessionId) ||
        loadedIds.current.has(sessionId) ||
        failedIds.current.has(sessionId)
      )
        return;

      const currentSession = getCurrentSessionId();

      // Only admins can fetch other sessions' preferences
      if (sessionId !== currentSession && !isAdmin) {
        console.warn(
          `[SessionPreferencesContext] Skipping load for session ${sessionId} - not authenticated`
        );
        failedIds.current.add(sessionId);
        loadedIds.current.add(sessionId);
        return;
      }

      loadingIds.current.add(sessionId);

      try {
        // Use cookie-based endpoint for current session, session-specific for others
        const isCurrentSession = sessionId === getCurrentSessionId();
        const url = isCurrentSession
          ? '/api/user-preferences'
          : `/api/user-preferences/session/${encodeURIComponent(sessionId)}`;
        const response = await fetch(url, ApiService.getFetchOptions());

        if (response.status === 401) {
          // 401 means unauthorized - mark as failed and loaded to prevent retries
          console.warn(
            `[SessionPreferencesContext] 401 for session ${sessionId} - marking as failed`
          );
          failedIds.current.add(sessionId);
          loadedIds.current.add(sessionId);
          return;
        }

        if (!response.ok) {
          // Any other error - mark as loaded to prevent infinite retries
          console.error(
            `[SessionPreferencesContext] HTTP ${response.status} for session ${sessionId}`
          );
          loadedIds.current.add(sessionId);
          return;
        }

        const prefs = await response.json();
        const normalizedPrefs: UserPreferences = {
          selectedTheme: prefs.selectedTheme || null,
          sharpCorners: prefs.sharpCorners ?? false,
          disableFocusOutlines: prefs.disableFocusOutlines ?? true,
          disableTooltips: prefs.disableTooltips ?? false,
          picsAlwaysVisible: prefs.picsAlwaysVisible ?? false,
          disableStickyNotifications: prefs.disableStickyNotifications ?? false,
          showDatasourceLabels: prefs.showDatasourceLabels ?? true,
          useLocalTimezone: prefs.useLocalTimezone ?? false,
          use24HourFormat: prefs.use24HourFormat ?? true,
          showYearInDates: prefs.showYearInDates ?? false,
          refreshRate: prefs.refreshRate ?? null,
          refreshRateLocked: prefs.refreshRateLocked ?? null,
          allowedTimeFormats: prefs.allowedTimeFormats ?? null
        };

        setPreferences((prev) => ({ ...prev, [sessionId]: normalizedPrefs }));
        loadedIds.current.add(sessionId);
      } catch (err) {
        console.error('[SessionPreferencesContext] Failed to load session preferences:', err);
        // Mark as loaded to prevent infinite retries on network errors
        loadedIds.current.add(sessionId);
      } finally {
        loadingIds.current.delete(sessionId);
      }
    },
    [isAdmin, getCurrentSessionId]
  );

  // Combined effect: reset and load atomically when session becomes available
  useEffect(() => {
    if (authLoading) return;
    if (!hasSession) return;

    const sessionId = getCurrentSessionId();
    if (sessionId && !initialLoadDone.current) {
      // Atomic reset and load - no intermediate state changes
      failedIds.current.clear();
      loadedIds.current.clear();
      initialLoadDone.current = true;
      loadSessionPreferences(sessionId);
    }
  }, [getCurrentSessionId, loadSessionPreferences, authLoading, hasSession]);

  // Reset when session is lost
  useEffect(() => {
    if (!hasSession) {
      initialLoadDone.current = false;
    }
  }, [hasSession]);

  const handleUserPreferencesUpdated = useCallback(
    (data: UserPreferencesUpdatedEvent) => {
      const { sessionId, preferences: newPrefs } = data;
      const isCurrentSession = sessionId === getCurrentSessionId();
      const existing = preferencesRef.current[sessionId];

      // For the current session, correct stale values from SignalR race conditions
      const incomingUseLocal = newPrefs.useLocalTimezone ?? false;
      const incomingUse24Hour = newPrefs.use24HourFormat ?? true;
      const incomingShowYear = newPrefs.showYearInDates ?? false;

      const { useLocal: useLocalTimezone, use24Hour: use24HourFormat } = isCurrentSession
        ? getCorrectedTimezone(incomingUseLocal, incomingUse24Hour)
        : { useLocal: incomingUseLocal, use24Hour: incomingUse24Hour };

      const showYearInDates = isCurrentSession
        ? getCorrectedValue('showYearInDates', incomingShowYear)
        : incomingShowYear;

      const normalizedPrefs: UserPreferences = {
        selectedTheme: newPrefs.selectedTheme || null,
        sharpCorners: newPrefs.sharpCorners ?? false,
        disableFocusOutlines: newPrefs.disableFocusOutlines ?? true,
        disableTooltips: newPrefs.disableTooltips ?? false,
        picsAlwaysVisible: newPrefs.picsAlwaysVisible ?? false,
        disableStickyNotifications: newPrefs.disableStickyNotifications ?? false,
        showDatasourceLabels: newPrefs.showDatasourceLabels ?? true,
        useLocalTimezone,
        use24HourFormat,
        showYearInDates,
        refreshRate: newPrefs.refreshRate ?? null,
        refreshRateLocked: newPrefs.refreshRateLocked ?? null,
        allowedTimeFormats: newPrefs.allowedTimeFormats ?? null
      };

      if (existing && JSON.stringify(existing) === JSON.stringify(normalizedPrefs)) return;

      const baseline = existing ?? DEFAULT_PREFERENCES;

      // Dispatch preference-changed events for the current session
      if (isCurrentSession) {
        const keysToCheck: (keyof UserPreferences)[] = [
          'useLocalTimezone',
          'use24HourFormat',
          'showYearInDates',
          'selectedTheme',
          'sharpCorners',
          'disableTooltips',
          'picsAlwaysVisible',
          'disableStickyNotifications',
          'showDatasourceLabels',
          'allowedTimeFormats'
        ];

        keysToCheck.forEach((key) => {
          // Skip dispatching showYearInDates during cooldown - already handled optimistically
          if (key === 'showYearInDates' && hasPendingPreference('showYearInDates')) {
            return;
          }

          if (baseline[key] !== normalizedPrefs[key]) {
            window.dispatchEvent(
              new CustomEvent('preference-changed', {
                detail: { key, value: normalizedPrefs[key] }
              })
            );
          }
        });
      }

      setPreferences((prev) => ({ ...prev, [sessionId]: normalizedPrefs }));
      if (!loadedIds.current.has(sessionId)) {
        loadedIds.current.add(sessionId);
      }
    },
    [getCurrentSessionId]
  );

  // When bulk preferences are reset, clear all cached prefs so badges refresh
  const handleUserPreferencesReset = useCallback(() => {
    setPreferences({});
    loadedIds.current.clear();
    failedIds.current.clear();
    initialLoadDone.current = false;

    // Reload current session's preferences immediately
    const sessionId = getCurrentSessionId();
    if (sessionId) {
      loadSessionPreferences(sessionId);
    }
  }, [getCurrentSessionId, loadSessionPreferences]);

  useEffect(() => {
    on('UserPreferencesUpdated', handleUserPreferencesUpdated);
    on('UserPreferencesReset', handleUserPreferencesReset);
    return () => {
      off('UserPreferencesUpdated', handleUserPreferencesUpdated);
      off('UserPreferencesReset', handleUserPreferencesReset);
    };
  }, [on, off, handleUserPreferencesUpdated, handleUserPreferencesReset]);

  const getSessionPreferences = useCallback(
    (sessionId: string): UserPreferences | null => {
      return preferences[sessionId] || null;
    },
    [preferences]
  );

  const isLoaded = useCallback(
    (sessionId: string): boolean => loadedIds.current.has(sessionId),
    []
  );
  const isLoading = useCallback(
    (sessionId: string): boolean => loadingIds.current.has(sessionId),
    []
  );

  const setOptimisticPreference = useCallback(
    <K extends keyof UserPreferences>(key: K, value: UserPreferences[K]) => {
      // Session identity is cookie-based, use the current session ID from getCurrentSessionId
      const sessionId = getCurrentSessionId();
      if (!sessionId) return;

      setPreferences((prev) => {
        const updated = {
          ...prev,
          [sessionId]: { ...(prev[sessionId] || DEFAULT_PREFERENCES), [key]: value }
        };
        // Update ref immediately so SignalR handler sees the new value
        preferencesRef.current = updated;
        return updated;
      });
    },
    [getCurrentSessionId]
  );

  const updateSessionPreference = useCallback(
    <K extends keyof UserPreferences>(sessionId: string, key: K, value: UserPreferences[K]) => {
      setPreferences((prev) => {
        if (!prev[sessionId]) return prev;
        return { ...prev, [sessionId]: { ...prev[sessionId], [key]: value } };
      });
    },
    []
  );

  const currentPreferences = useMemo(() => {
    const sessionId = getCurrentSessionId();
    return sessionId ? preferences[sessionId] || null : null;
  }, [preferences, getCurrentSessionId]);

  const contextValue = useMemo<SessionPreferencesContextType>(
    () => ({
      getSessionPreferences,
      currentPreferences,
      isLoaded,
      isLoading,
      loadSessionPreferences,
      setOptimisticPreference,
      updateSessionPreference
    }),
    [
      getSessionPreferences,
      currentPreferences,
      isLoaded,
      isLoading,
      loadSessionPreferences,
      setOptimisticPreference,
      updateSessionPreference
    ]
  );

  return (
    <SessionPreferencesContext.Provider value={contextValue}>
      {children}
    </SessionPreferencesContext.Provider>
  );
};
