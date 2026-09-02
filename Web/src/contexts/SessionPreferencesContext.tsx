import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { useSignalR } from './SignalRContext/useSignalR';
import { useAuth } from './useAuth';
import ApiService from '@services/api.service';
import type {
  UserPreferencesUpdatedEvent,
  DefaultGuestThemeChangedEvent,
  DefaultGuestPreferencesChangedEvent,
  AllowedTimeFormatsChangedEvent
} from './SignalRContext/types';
import { preferPendingTimezone, preferPendingValue } from '@utils/pendingPreferences';
import {
  applyGuestClockChanges,
  DEFAULT_GUEST_PREFERENCE_KEYS,
  shouldApplyGuestClockChange,
  shouldApplyGuestDefaultChange
} from '@utils/guestDefaultPreferenceGate';
import {
  applyAllowedTimeFormatsChange,
  applyDefaultGuestPreferencesChange,
  getCachedDefaultGuestPreferences,
  type DefaultGuestPreferences
} from '@hooks/useDefaultGuestPreferences';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { SessionPreferencesContext } from './SessionPreferencesContext.types';
import { APP_EVENTS } from '@utils/constants';
import {
  CLOCK_KEYS,
  DEFAULT_PREFERENCES,
  OPTIMISTIC_TOGGLE_KEYS,
  type ClockPreferences,
  type OptimisticToggleKey,
  type UserPreferences
} from '@/types/userPreferences';

/**
 * Let a switch whose save is still on the wire outrank the row a broadcast carries.
 *
 * The broadcast is not wrong, only older than the click still in flight: it was built before the
 * server saw that click, so for every key other than the one it announces it repeats what the row
 * held a moment ago. Keys with nothing in flight take the broadcast unchanged, which is what keeps
 * this a settle rather than a reason to ignore broadcasts.
 */
const settlePendingToggles = (prefs: UserPreferences): UserPreferences => {
  const settled: UserPreferences = { ...prefs };
  OPTIMISTIC_TOGGLE_KEYS.forEach((key: OptimisticToggleKey) => {
    settled[key] = preferPendingValue<boolean>(key, prefs[key]);
  });
  return settled;
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
  const pendingDefaultClocks = useRef<
    Map<string, { clock: ClockPreferences; previousClock: ClockPreferences }[]>
  >(new Map());
  const loadGenerations = useRef<Map<string, number>>(new Map());

  const { on, off, isConnected } = useSignalR();
  const { isAdmin, hasSession, sessionId: authSessionId, isLoading: authLoading } = useAuth();

  const getCurrentSessionId = useCallback((): string | null => {
    return authSessionId ?? null;
  }, [authSessionId]);

  useEffect(() => {
    preferencesRef.current = preferences;
  }, [preferences]);

  /** The next number for a session, claimed by whoever is about to change what that session holds. */
  const nextLoadGeneration = useCallback((sessionId: string): number => {
    const generation = (loadGenerations.current.get(sessionId) ?? 0) + 1;
    loadGenerations.current.set(sessionId, generation);
    return generation;
  }, []);

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
      const generation = nextLoadGeneration(sessionId);

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
          pendingDefaultClocks.current.delete(sessionId);
          return;
        }

        if (!response.ok) {
          // Any other error - mark as loaded to prevent infinite retries
          console.error(
            `[SessionPreferencesContext] HTTP ${response.status} for session ${sessionId}`
          );
          loadedIds.current.add(sessionId);
          pendingDefaultClocks.current.delete(sessionId);
          return;
        }

        const prefs = await response.json();

        if (loadGenerations.current.get(sessionId) !== generation) {
          // A write landed while the request was on the wire, so it is newer than this answer.
          loadedIds.current.add(sessionId);
          pendingDefaultClocks.current.delete(sessionId);
          return;
        }

        // A pick still waiting on its save outranks the clock this answer carries. A pick the
        // server has confirmed does not: the client just asked it directly, so that pick is let go.
        const incomingUseLocal = prefs.useLocalTimezone ?? false;
        const incomingUseUtc = prefs.useUtcTimezone ?? false;
        const incomingUse24Hour = prefs.use24HourFormat ?? true;

        const {
          useLocal: useLocalTimezone,
          useUtc: useUtcTimezone,
          use24Hour: use24HourFormat
        } = isCurrentSession
          ? preferPendingTimezone(incomingUseLocal, incomingUseUtc, incomingUse24Hour)
          : { useLocal: incomingUseLocal, useUtc: incomingUseUtc, use24Hour: incomingUse24Hour };

        const normalizedPrefs: UserPreferences = {
          selectedTheme: prefs.selectedTheme || null,
          sharpCorners: prefs.sharpCorners ?? false,
          disableFocusOutlines: prefs.disableFocusOutlines ?? true,
          disableTooltips: prefs.disableTooltips ?? false,
          picsAlwaysVisible: prefs.picsAlwaysVisible ?? false,
          disableStickyNotifications: prefs.disableStickyNotifications ?? false,
          showDatasourceLabels: prefs.showDatasourceLabels ?? true,
          useLocalTimezone,
          useUtcTimezone,
          use24HourFormat,
          refreshRate: prefs.refreshRate ?? null,
          refreshRateLocked: prefs.refreshRateLocked ?? null,
          allowedTimeFormats: prefs.allowedTimeFormats ?? null
        };

        const pendingClocks = pendingDefaultClocks.current.get(sessionId) ?? [];
        const settledPrefs = applyGuestClockChanges(
          isCurrentSession ? settlePendingToggles(normalizedPrefs) : normalizedPrefs,
          pendingClocks
        );
        pendingDefaultClocks.current.delete(sessionId);

        // Keep the ref and loaded marker in step with the state write. A SignalR callback can run after
        // this function returns but before React commits the update; it must see the settled preferences
        // rather than conclude that the event arrived before the load.
        const updated = { ...preferencesRef.current, [sessionId]: settledPrefs };
        preferencesRef.current = updated;
        setPreferences(updated);
        loadedIds.current.add(sessionId);
      } catch (err) {
        // Background per-session load (also used to load OTHER users' preferences for admin
        // views) - a toast per failed session load would be noisy. Deliberately silent; marking
        // as loaded below prevents an infinite retry loop.
        console.error('[SessionPreferencesContext] Failed to load session preferences:', err);
        // Mark as loaded to prevent infinite retries on network errors
        loadedIds.current.add(sessionId);
        pendingDefaultClocks.current.delete(sessionId);
      } finally {
        loadingIds.current.delete(sessionId);
      }
    },
    [isAdmin, getCurrentSessionId, nextLoadGeneration]
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

  // Nothing replays a preference changed while the socket was down, so clear the two markers that
  // make a load a no-op and ask again. Declared after the load effect: a session and a connection
  // arriving in one commit then load once rather than twice.
  const resyncPreferences = useCallback(() => {
    const sessionId = getCurrentSessionId();
    if (!sessionId) return;

    loadedIds.current.delete(sessionId);
    failedIds.current.delete(sessionId);
    loadSessionPreferences(sessionId);
  }, [getCurrentSessionId, loadSessionPreferences]);

  useReconnectRefetch(isConnected, resyncPreferences);

  // Reset when session is lost
  useEffect(() => {
    if (!hasSession) {
      initialLoadDone.current = false;
      pendingDefaultClocks.current.clear();
    }
  }, [hasSession]);

  const handleUserPreferencesUpdated = useCallback(
    (data: UserPreferencesUpdatedEvent) => {
      const { sessionId, preferences: newPrefs } = data;
      const isCurrentSession = sessionId === getCurrentSessionId();
      const existing = preferencesRef.current[sessionId];

      // For the current session, correct stale values from SignalR race conditions
      const incomingUseLocal = newPrefs.useLocalTimezone;
      const incomingUseUtc = newPrefs.useUtcTimezone;
      const incomingUse24Hour = newPrefs.use24HourFormat;

      const {
        useLocal: useLocalTimezone,
        useUtc: useUtcTimezone,
        use24Hour: use24HourFormat
      } = isCurrentSession
        ? preferPendingTimezone(incomingUseLocal, incomingUseUtc, incomingUse24Hour)
        : { useLocal: incomingUseLocal, useUtc: incomingUseUtc, use24Hour: incomingUse24Hour };

      const normalizedPrefs: UserPreferences = {
        selectedTheme: newPrefs.selectedTheme || null,
        sharpCorners: newPrefs.sharpCorners,
        disableFocusOutlines: newPrefs.disableFocusOutlines,
        disableTooltips: newPrefs.disableTooltips,
        picsAlwaysVisible: newPrefs.picsAlwaysVisible,
        disableStickyNotifications: newPrefs.disableStickyNotifications,
        showDatasourceLabels: newPrefs.showDatasourceLabels,
        useLocalTimezone,
        useUtcTimezone,
        use24HourFormat,
        refreshRate: newPrefs.refreshRate ?? null,
        refreshRateLocked: newPrefs.refreshRateLocked ?? null,
        allowedTimeFormats: newPrefs.allowedTimeFormats ?? null
      };

      const settledPrefs = isCurrentSession
        ? settlePendingToggles(normalizedPrefs)
        : normalizedPrefs;

      if (existing && JSON.stringify(existing) === JSON.stringify(settledPrefs)) return;

      const baseline = existing ?? DEFAULT_PREFERENCES;

      // Dispatch preference-changed events for the current session
      if (isCurrentSession) {
        const keysToCheck: (keyof UserPreferences)[] = [
          'useLocalTimezone',
          'useUtcTimezone',
          'use24HourFormat',
          'selectedTheme',
          'sharpCorners',
          'disableTooltips',
          'picsAlwaysVisible',
          'disableStickyNotifications',
          'showDatasourceLabels',
          'allowedTimeFormats'
        ];

        keysToCheck.forEach((key) => {
          if (baseline[key] !== settledPrefs[key]) {
            window.dispatchEvent(
              new CustomEvent(APP_EVENTS.PREFERENCE_CHANGED, {
                detail: { key, value: settledPrefs[key] }
              })
            );
          }
        });
      }

      nextLoadGeneration(sessionId);
      setPreferences((prev) => ({ ...prev, [sessionId]: settledPrefs }));
      if (!loadedIds.current.has(sessionId)) {
        loadedIds.current.add(sessionId);
      }
    },
    [getCurrentSessionId, nextLoadGeneration]
  );

  // When bulk preferences are reset, clear all cached prefs so badges refresh.
  // The backend may include sessionType ('guest' | 'authenticated') to scope the reset.
  // If sessionType is 'guest' and this client is admin, ignore it (admin cache is unaffected).
  // If sessionType is 'authenticated' and this client is not admin, ignore it.
  // If sessionType is absent it is a global reset - always apply.
  const handleUserPreferencesReset = useCallback(
    (data?: { sessionType?: string }) => {
      const sessionType = data?.sessionType;

      if (sessionType === 'guest' && isAdmin) {
        // Guest-scoped reset - admin cache is unaffected, skip
        return;
      }

      if (sessionType === 'authenticated' && !isAdmin) {
        // Admin-scoped reset - guest cache is unaffected, skip
        return;
      }

      setPreferences({});
      loadedIds.current.clear();
      failedIds.current.clear();
      initialLoadDone.current = false;

      // Reload current session's preferences immediately
      const sessionId = getCurrentSessionId();
      if (sessionId) {
        loadSessionPreferences(sessionId);
      }
    },
    [isAdmin, getCurrentSessionId, loadSessionPreferences]
  );

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

  /**
   * Write one or more of the current session's preferences in a single update. The clock is three
   * columns that only mean something together, so it is written through here rather than as three
   * calls: three calls put a render's worth of "UTC on, 12-hour face" between the first and the last.
   */
  const applyOptimisticPreferences = useCallback(
    (changes: Partial<UserPreferences>) => {
      // Session identity is cookie-based, use the current session ID from getCurrentSessionId
      const sessionId = getCurrentSessionId();
      if (!sessionId) return;

      nextLoadGeneration(sessionId);
      setPreferences((prev) => {
        const updated = {
          ...prev,
          [sessionId]: { ...(prev[sessionId] || DEFAULT_PREFERENCES), ...changes }
        };
        // Update ref immediately so SignalR handler sees the new value
        preferencesRef.current = updated;
        return updated;
      });
    },
    [getCurrentSessionId, nextLoadGeneration]
  );

  const setOptimisticPreference = useCallback(
    <K extends keyof UserPreferences>(key: K, value: UserPreferences[K]) => {
      applyOptimisticPreferences({ [key]: value });
    },
    [applyOptimisticPreferences]
  );

  const dispatchPreferenceChanged = useCallback((key: string, value: unknown) => {
    window.dispatchEvent(
      new CustomEvent(APP_EVENTS.PREFERENCE_CHANGED, {
        detail: { key, value }
      })
    );
  }, []);

  const isGuestWithLoadedPrefs = useCallback((): UserPreferences | null => {
    if (isAdmin || !hasSession) return null;

    const sessionId = getCurrentSessionId();
    if (!sessionId || !loadedIds.current.has(sessionId)) return null;

    return preferencesRef.current[sessionId] ?? null;
  }, [isAdmin, hasSession, getCurrentSessionId]);

  const handleDefaultGuestThemeChanged = useCallback(
    (data: DefaultGuestThemeChangedEvent) => {
      const currentPrefs = isGuestWithLoadedPrefs();
      if (!currentPrefs) return;

      // The theme decision reads only whether this session picked a theme of its own, never the
      // snapshot, so how old the cached defaults are cannot change the answer here.
      const previousDefaults: DefaultGuestPreferences = getCachedDefaultGuestPreferences();
      if (!shouldApplyGuestDefaultChange('selectedTheme', currentPrefs, previousDefaults)) return;

      setOptimisticPreference('selectedTheme', null);
      dispatchPreferenceChanged('selectedTheme', data.newThemeId);
    },
    [isGuestWithLoadedPrefs, setOptimisticPreference, dispatchPreferenceChanged]
  );

  /**
   * A guest inherits a new default clock only if it was still reading the old one, and that is one
   * decision about three columns rather than three decisions about one column each: a guest that
   * chose UTC itself while the default moved from server-12h to local-24h matches the old default on
   * use24HourFormat alone, and taking that field on its own would leave it on a clock nobody picked.
   */
  const applyDefaultGuestClock = useCallback(
    (currentPrefs: UserPreferences, previousClock: ClockPreferences, clock: ClockPreferences) => {
      if (!shouldApplyGuestClockChange(currentPrefs, previousClock)) return;

      applyOptimisticPreferences(clock);
      CLOCK_KEYS.forEach((key) => dispatchPreferenceChanged(key, clock[key]));
    },
    [applyOptimisticPreferences, dispatchPreferenceChanged]
  );

  const handleDefaultGuestPreferencesChanged = useCallback(
    (data: DefaultGuestPreferencesChangedEvent) => {
      // Fold the broadcast into the shared defaults first and keep what they were: this is the only
      // subscriber that moves them, so `previous` is the snapshot every decision below needs and no
      // other listener can have advanced it.
      const { previous } = applyDefaultGuestPreferencesChange(data);

      const currentPrefs = isGuestWithLoadedPrefs();
      if (!currentPrefs) {
        if (data.key === 'clock' && !isAdmin && hasSession) {
          const sessionId = getCurrentSessionId();
          if (sessionId && !loadedIds.current.has(sessionId)) {
            const pending = pendingDefaultClocks.current.get(sessionId) ?? [];
            pending.push(data);
            pendingDefaultClocks.current.set(sessionId, pending);
          }
        }
        return;
      }

      if (data.key === 'clock') {
        // The server captured previousClock under the same lock as the write, so it names the tuple
        // this change replaced even when two admin clients change the clock back to back.
        applyDefaultGuestClock(currentPrefs, data.previousClock, data.clock);
        return;
      }

      if (!DEFAULT_GUEST_PREFERENCE_KEYS.has(data.key)) return;
      if (!shouldApplyGuestDefaultChange(data.key, currentPrefs, previous)) return;

      setOptimisticPreference(data.key, data.value);
      dispatchPreferenceChanged(data.key, data.value);
    },
    [
      applyDefaultGuestClock,
      isGuestWithLoadedPrefs,
      setOptimisticPreference,
      dispatchPreferenceChanged,
      isAdmin,
      hasSession,
      getCurrentSessionId
    ]
  );

  const handleAllowedTimeFormatsChanged = useCallback(
    (data: AllowedTimeFormatsChangedEvent) => {
      const { previous } = applyAllowedTimeFormatsChange(data);

      const currentPrefs = isGuestWithLoadedPrefs();
      if (!currentPrefs) return;

      if (!shouldApplyGuestDefaultChange('allowedTimeFormats', currentPrefs, previous)) {
        return;
      }

      setOptimisticPreference('allowedTimeFormats', data.formats);
      dispatchPreferenceChanged('allowedTimeFormats', data.formats);
    },
    [isGuestWithLoadedPrefs, setOptimisticPreference, dispatchPreferenceChanged]
  );

  useEffect(() => {
    on('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
    on('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
    on('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
    return () => {
      off('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
    };
  }, [
    on,
    off,
    handleDefaultGuestThemeChanged,
    handleDefaultGuestPreferencesChanged,
    handleAllowedTimeFormatsChanged
  ]);

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

  const contextValue = useMemo(
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
