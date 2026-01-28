import React, { createContext, useContext, useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useSignalR } from './SignalRContext';
import authService from '@services/auth.service';
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
  allowedTimeFormats: null
};

interface SessionPreferencesContextType {
  getSessionPreferences: (sessionId: string) => UserPreferences | null;
  currentPreferences: UserPreferences | null;
  currentSessionId: string | null;
  isLoaded: (sessionId: string) => boolean;
  isLoading: (sessionId: string) => boolean;
  loadSessionPreferences: (sessionId: string) => Promise<void>;
  setOptimisticPreference: <K extends keyof UserPreferences>(key: K, value: UserPreferences[K]) => void;
  updateSessionPreference: <K extends keyof UserPreferences>(sessionId: string, key: K, value: UserPreferences[K]) => void;
}

const SessionPreferencesContext = createContext<SessionPreferencesContextType | null>(null);

export const useSessionPreferences = () => {
  const context = useContext(SessionPreferencesContext);
  if (!context) {
    throw new Error('useSessionPreferences must be used within a SessionPreferencesProvider');
  }
  return context;
};

export const SessionPreferencesProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [preferences, setPreferences] = useState<Record<string, UserPreferences>>({});
  const [loadingIds, setLoadingIds] = useState<Set<string>>(new Set());
  const [loadedIds, setLoadedIds] = useState<Set<string>>(new Set());
  const preferencesRef = useRef<Record<string, UserPreferences>>({});
  const initialLoadDone = useRef(false);

  const { on, off } = useSignalR();

  const getCurrentSessionId = useCallback((): string | null => {
    return authService.getDeviceId() || authService.getGuestSessionId() || null;
  }, []);

  const currentSessionId = getCurrentSessionId();

  useEffect(() => {
    preferencesRef.current = preferences;
  }, [preferences]);

  const loadSessionPreferences = useCallback(async (sessionId: string) => {
    if (loadingIds.has(sessionId) || loadedIds.has(sessionId)) return;

    setLoadingIds(prev => new Set(prev).add(sessionId));

    try {
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(sessionId)}`,
        ApiService.getFetchOptions()
      );

      if (response.ok) {
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
          allowedTimeFormats: prefs.allowedTimeFormats ?? null
        };

        setPreferences(prev => ({ ...prev, [sessionId]: normalizedPrefs }));
        setLoadedIds(prev => new Set(prev).add(sessionId));
      }
    } catch (err) {
      console.error('[SessionPreferencesContext] Failed to load session preferences:', err);
    } finally {
      setLoadingIds(prev => {
        const next = new Set(prev);
        next.delete(sessionId);
        return next;
      });
    }
  }, [loadingIds, loadedIds]);

  useEffect(() => {
    const sessionId = getCurrentSessionId();
    if (sessionId && !initialLoadDone.current) {
      initialLoadDone.current = true;
      loadSessionPreferences(sessionId);
    }
  }, [getCurrentSessionId, loadSessionPreferences]);

  const handleUserPreferencesUpdated = useCallback((data: UserPreferencesUpdatedEvent) => {
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
      allowedTimeFormats: newPrefs.allowedTimeFormats ?? null
    };

    if (existing && JSON.stringify(existing) === JSON.stringify(normalizedPrefs)) return;

    const baseline = existing ?? DEFAULT_PREFERENCES;

    // Dispatch preference-changed events for the current session
    if (isCurrentSession) {
      const keysToCheck: (keyof UserPreferences)[] = [
        'useLocalTimezone', 'use24HourFormat', 'showYearInDates', 'selectedTheme',
        'sharpCorners', 'disableTooltips', 'picsAlwaysVisible',
        'disableStickyNotifications', 'showDatasourceLabels', 'allowedTimeFormats'
      ];

      keysToCheck.forEach(key => {
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

    setPreferences(prev => ({ ...prev, [sessionId]: normalizedPrefs }));
    setLoadedIds(prev => prev.has(sessionId) ? prev : new Set(prev).add(sessionId));
  }, [getCurrentSessionId]);

  useEffect(() => {
    on('UserPreferencesUpdated', handleUserPreferencesUpdated);
    return () => off('UserPreferencesUpdated', handleUserPreferencesUpdated);
  }, [on, off, handleUserPreferencesUpdated]);

  const getSessionPreferences = useCallback((sessionId: string): UserPreferences | null => {
    return preferences[sessionId] || null;
  }, [preferences]);

  const isLoaded = useCallback((sessionId: string): boolean => loadedIds.has(sessionId), [loadedIds]);
  const isLoading = useCallback((sessionId: string): boolean => loadingIds.has(sessionId), [loadingIds]);

  const setOptimisticPreference = useCallback(<K extends keyof UserPreferences>(key: K, value: UserPreferences[K]) => {
    const sessionId = authService.getDeviceId() || authService.getGuestSessionId();
    if (!sessionId) return;

    setPreferences(prev => {
      const updated = {
        ...prev,
        [sessionId]: { ...(prev[sessionId] || DEFAULT_PREFERENCES), [key]: value }
      };
      // Update ref immediately so SignalR handler sees the new value
      preferencesRef.current = updated;
      return updated;
    });
  }, []);

  const updateSessionPreference = useCallback(<K extends keyof UserPreferences>(
    sessionId: string, key: K, value: UserPreferences[K]
  ) => {
    setPreferences(prev => {
      if (!prev[sessionId]) return prev;
      return { ...prev, [sessionId]: { ...prev[sessionId], [key]: value } };
    });
  }, []);

  const currentPreferences = useMemo(() => {
    const sessionId = getCurrentSessionId();
    return sessionId ? preferences[sessionId] || null : null;
  }, [preferences, getCurrentSessionId]);

  const contextValue = useMemo<SessionPreferencesContextType>(() => ({
    getSessionPreferences, currentPreferences, currentSessionId,
    isLoaded, isLoading, loadSessionPreferences,
    setOptimisticPreference, updateSessionPreference
  }), [
    getSessionPreferences, currentPreferences, currentSessionId,
    isLoaded, isLoading, loadSessionPreferences,
    setOptimisticPreference, updateSessionPreference
  ]);

  return (
    <SessionPreferencesContext.Provider value={contextValue}>
      {children}
    </SessionPreferencesContext.Provider>
  );
};
