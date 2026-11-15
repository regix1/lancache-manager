import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import i18n from '@/i18n';
import { APP_EVENTS, REFRESH_RATES, type RefreshRate } from '@utils/constants';
import ApiService from '@services/api.service';
import { assertOk } from '@services/apiError';
import { getErrorMessage } from '@utils/error';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useAuth } from '@contexts/useAuth';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import type {
  GuestRefreshRateUpdatedEvent,
  DefaultGuestRefreshRateChangedEvent,
  GuestRefreshRateLockChangedEvent,
  SystemRefreshRateChangedEvent,
  ShowToastEvent
} from '@contexts/SignalRContext/types';
import { RefreshRateContext } from './RefreshRateContext.types';

// RefreshRateProvider is an ancestor of NotificationsProvider in AppProviders.tsx, so
// useErrorHandler (useNotifications) is not reachable from it - it would throw. Use the existing
// show-toast bridge instead (the SHOW_TOAST listener in NotificationsContext.tsx).
const notifyRefreshRateSaveFailed = (error: unknown): void => {
  window.dispatchEvent(
    new CustomEvent<ShowToastEvent>(APP_EVENTS.SHOW_TOAST, {
      detail: {
        type: 'error',
        message: i18n.t('common.refreshRate.errors.saveFailed'),
        error: getErrorMessage(error)
      }
    })
  );
};

export const RefreshRateProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  // Default to STANDARD (10s) until we fetch from API
  const [refreshRate, setRefreshRateState] = useState<RefreshRate>('STANDARD');
  const [isLoaded, setIsLoaded] = useState(false);
  const [isControlledByAdmin, setIsControlledByAdmin] = useState(false);
  const [defaultGuestRate, setDefaultGuestRate] = useState<string | null>(null);
  const [globalLocked, setGlobalLocked] = useState<boolean>(true);
  // The install-wide rate. An admin follows it; a user-role account follows it while its session has no
  // rate of its own.
  const [systemRate, setSystemRate] = useState<RefreshRate>('STANDARD');
  const [error, setError] = useState<string | null>(null);
  // The rate the server last confirmed, which a failed pick puts back. Only a pick moves pickRef, so
  // a read that left the rate alone cannot keep a failed pick on screen; revertedPickRef is the pick
  // whose failure put the saved rate back.
  const savedRateRef = useRef<RefreshRate>('STANDARD');
  const pickRef = useRef(0);
  const revertedPickRef = useRef(0);
  // The pick whose save last wrote savedRateRef, so an older pick that answers later cannot replace a
  // newer saved pick; and how many of this window's saves are still on the wire.
  const savedPickRef = useRef(0);
  const savesInFlightRef = useRef(0);

  const { on, off, isConnected } = useSignalR();
  const { authMode, sessionId, sessionType } = useAuth();
  const {
    currentPreferences,
    error: preferencesError,
    resyncPreferences
  } = useSessionPreferences();
  // A guest and a user-role account follow their own session's row, so a failed read of it is theirs.
  const ownRateSession =
    authMode === 'guest' || (authMode === 'authenticated' && sessionType === 'user');

  // Ref to avoid stale closures in SignalR handlers
  const authModeRef = useRef(authMode);
  // Every writer and every read below bumps this, and a fetch response only lands if the
  // generation it took still matches, so an answer superseded while in flight (by a newer read,
  // a broadcast or a pick) loses instead of winning.
  const generationRef = useRef(0);
  useEffect(() => {
    authModeRef.current = authMode;
    generationRef.current += 1;
  }, [authMode]);
  const sessionIdRef = useRef(sessionId);
  useEffect(() => {
    sessionIdRef.current = sessionId;
  }, [sessionId]);
  const sessionTypeRef = useRef(sessionType);
  useEffect(() => {
    sessionTypeRef.current = sessionType;
  }, [sessionType]);

  // Fetch global default guest rate and lock state (for guests only)
  const fetchGlobalDefaults = useCallback(async () => {
    const requestGeneration = ++generationRef.current;
    try {
      const response = await fetch('/api/system/default-guest-refresh-rate', {
        credentials: 'include'
      });
      await assertOk(response);
      const body = await response.json();
      if (generationRef.current !== requestGeneration) return;
      setGlobalLocked(body.locked ?? true);
      setDefaultGuestRate(body.refreshRate || null);
      setError(null);
    } catch (err) {
      // The locked STANDARD defaults stay in effect, so the guest still cannot change a rate the
      // admin may have locked; the selector shows the failure instead of that lock.
      console.error('Failed to fetch global guest defaults:', err);
      if (generationRef.current === requestGeneration) setError(getErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    if (authMode !== 'guest') return;

    void fetchGlobalDefaults();
  }, [authMode, fetchGlobalDefaults]);

  // For unauthenticated users, just mark as loaded with defaults so the
  // UI tree below this provider can render (e.g. the login modal).
  useEffect(() => {
    if (authMode === 'unauthenticated') {
      setIsControlledByAdmin(false);
      setIsLoaded(true);
    }
  }, [authMode]);

  // A guest and a user-role account derive refresh state from their own session preferences: the
  // session's rate and lock when the admin set them, else the guest default and the lock for every
  // guest (guest), or the install-wide rate, unlocked (user-role account).
  useEffect(() => {
    const userRole = authMode === 'authenticated' && sessionType === 'user';
    if (authMode !== 'guest' && !userRole) return;

    if (!currentPreferences) {
      // Not read yet. The window stays locked meanwhile, so its selector never offers a change the
      // server would refuse, also in the frame after a Retry that read the row. A failed read is drawn
      // as the selector's failure with a Retry instead.
      setIsControlledByAdmin(true);
      if (preferencesError !== null) setIsLoaded(true);
      return;
    }

    const perSessionRate = currentPreferences.refreshRate;
    const perSessionLocked = currentPreferences.refreshRateLocked;
    const fallbackRate = userRole ? systemRate : defaultGuestRate;
    // The row this window's own earlier pick announced is older than a pick still saving, so while a
    // save is on the wire the row only moves the rate a failure goes back to.
    const redraw = savesInFlightRef.current === 0;

    if (perSessionRate && perSessionRate in REFRESH_RATES) {
      savedRateRef.current = perSessionRate as RefreshRate;
      if (redraw) setRefreshRateState(perSessionRate as RefreshRate);
    } else if (fallbackRate && fallbackRate in REFRESH_RATES) {
      savedRateRef.current = fallbackRate as RefreshRate;
      if (redraw) setRefreshRateState(fallbackRate as RefreshRate);
    }

    // Per-session override takes precedence: false means unlocked, true means locked
    // null/undefined means the default for the session's type
    const effectiveLocked =
      perSessionLocked !== null && perSessionLocked !== undefined
        ? perSessionLocked
        : !userRole && globalLocked;
    setIsControlledByAdmin(effectiveLocked);
    setIsLoaded(true);
  }, [
    authMode,
    sessionType,
    currentPreferences,
    preferencesError,
    defaultGuestRate,
    globalLocked,
    systemRate
  ]);

  // An admin follows the install-wide rate, and never inherits guest lock/default behavior. A user-role
  // account reads it too, as the rate it follows while its session has none of its own.
  const fetchSystemRate = useCallback(async () => {
    const requestGeneration = ++generationRef.current;
    try {
      const response = await fetch('/api/system/refresh-rate', {
        credentials: 'include'
      });
      await assertOk(response);
      const body = await response.json();
      if (generationRef.current === requestGeneration) {
        if (body.refreshRate && body.refreshRate in REFRESH_RATES) {
          setSystemRate(body.refreshRate as RefreshRate);
          // A user-role account's rate is settled by the session effect above.
          if (sessionTypeRef.current !== 'user') {
            savedRateRef.current = body.refreshRate as RefreshRate;
            setRefreshRateState(body.refreshRate as RefreshRate);
          }
        }
        setError(null);
      }
    } catch (err) {
      // This browser keeps polling at the STANDARD default, but the selector shows the failure
      // instead of that default, which would read as the saved system rate.
      console.error('Failed to fetch system refresh rate:', err);
      if (generationRef.current === requestGeneration) setError(getErrorMessage(err));
    } finally {
      if (generationRef.current === requestGeneration) {
        setIsLoaded(true);
      }
    }
  }, []);

  useEffect(() => {
    if (authMode !== 'authenticated') return;

    // A user-role account's lock is its own session's, set by the session effect above.
    if (sessionType !== 'user') setIsControlledByAdmin(false);
    void fetchSystemRate();
  }, [authMode, sessionType, fetchSystemRate]);

  // Listen for SignalR events.
  // All handlers read authMode/sessionId from refs to avoid stale closures.
  useEffect(() => {
    // Handle admin pushing a new rate to this specific guest
    // SessionPreferencesContext will handle the UserPreferencesUpdated event,
    // but this event is guest-specific and immediate
    const handleGuestRefreshRateUpdated = (data: GuestRefreshRateUpdatedEvent) => {
      // Sent to every client; only the window of the session that was changed applies it, a guest's or
      // a user-role account's (an admin follows the install-wide rate).
      const ownRate =
        authModeRef.current === 'guest' ||
        (authModeRef.current === 'authenticated' && sessionTypeRef.current === 'user');
      if (!ownRate || data.sessionId !== sessionIdRef.current) return;
      if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
        generationRef.current += 1;
        savedRateRef.current = data.refreshRate as RefreshRate;
        setRefreshRateState(data.refreshRate as RefreshRate);
        return;
      }
      // "Use default" cleared the session's rate, so the window goes to the rate it falls back to.
      const fallbackRate = sessionTypeRef.current === 'user' ? systemRate : defaultGuestRate;
      if (fallbackRate && fallbackRate in REFRESH_RATES) {
        generationRef.current += 1;
        savedRateRef.current = fallbackRate as RefreshRate;
        setRefreshRateState(fallbackRate as RefreshRate);
      }
    };

    // An admin changed the install-wide rate, which an admin window follows and a user-role account
    // follows while its session has no rate of its own. The generation is left alone: an admin's first
    // read sets isLoaded only while its generation still matches.
    const handleSystemRefreshRateChanged = (event: SystemRefreshRateChangedEvent) => {
      setSystemRate(event.refreshRate as RefreshRate);
      if (authModeRef.current !== 'authenticated' || sessionTypeRef.current === 'user') return;
      // The rate a failed own save goes back to. While this window's own save is on the wire, that
      // save's answer settles what is drawn.
      savedRateRef.current = event.refreshRate as RefreshRate;
      if (savesInFlightRef.current === 0) setRefreshRateState(event.refreshRate as RefreshRate);
    };

    // Handle default guest rate change (affects guests using default)
    // Only apply if guest doesn't have a per-session override
    const handleDefaultGuestRefreshRateChanged = (data: DefaultGuestRefreshRateChangedEvent) => {
      if (
        authModeRef.current === 'guest' &&
        data.refreshRate &&
        data.refreshRate in REFRESH_RATES
      ) {
        generationRef.current += 1;
        setDefaultGuestRate(data.refreshRate);
        // If no per-session rate is set (using default), update immediately
        // SessionPreferencesContext will have currentPreferences.refreshRate === null
      }
    };

    // Handle global lock state change
    // Update global lock state, effective lock will be recalculated in the main effect
    const handleGuestRefreshRateLockChanged = (data: GuestRefreshRateLockChangedEvent) => {
      if (authModeRef.current === 'guest') {
        generationRef.current += 1;
        setGlobalLocked(data.locked);
        // The main effect will recalculate effectiveLocked based on per-session override
      }
    };

    // UserPreferencesUpdated is now handled by SessionPreferencesContext
    // The main effect will react to currentPreferences changes automatically

    on('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
    on('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
    on('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);
    on('SystemRefreshRateChanged', handleSystemRefreshRateChanged);

    return () => {
      off('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
      off('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
      off('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);
      off('SystemRefreshRateChanged', handleSystemRefreshRateChanged);
    };
  }, [on, off, systemRate, defaultGuestRate]);

  // An unlock broadcast while the socket was down is gone for good, and it leaves the control
  // disabled with nothing on screen to say so. One callback: the fetches never both apply. The
  // selector's failure chip repeats the same read. A guest's or user-role account's window also reads
  // its own session's row again, whose failure the chip may be showing.
  const reload = () => {
    if (authModeRef.current === 'guest') {
      void fetchGlobalDefaults();
    } else if (authModeRef.current === 'authenticated') {
      void fetchSystemRate();
    }
    if (ownRateSession) void resyncPreferences();
  };
  useReconnectRefetch(isConnected, reload);

  const setRefreshRate = useCallback(
    async (rate: RefreshRate) => {
      // Block a locked guest or user-role account from changing its refresh rate
      if (isControlledByAdmin) {
        console.warn('[RefreshRate] This session cannot change its refresh rate (locked by admin)');
        return;
      }

      // Optimistically update state
      generationRef.current += 1;
      const pick = ++pickRef.current;
      savesInFlightRef.current += 1;
      setRefreshRateState(rate);

      // Save to API - a guest or a user-role account saves its own session's rate, an admin the
      // install-wide rate
      const ownRate = authMode === 'guest' || sessionTypeRef.current === 'user';
      try {
        const endpoint = ownRate ? '/api/user-preferences/refreshRate' : '/api/system/refresh-rate';
        const body = ownRate ? rate : { refreshRate: rate };

        const response = await fetch(
          endpoint,
          ApiService.getJsonFetchOptions(body, { method: 'PATCH' })
        );

        await assertOk(response);
        // An older pick that answers after a newer one saved is not what the server holds.
        if (pick > savedPickRef.current) {
          savedPickRef.current = pick;
          savedRateRef.current = rate;
          // An admin's saved pick is the install-wide rate the Users page draws as a default.
          if (!ownRate) setSystemRate(rate);
          // A newer pick that failed already put the older saved rate back; this one is now saved.
          if (revertedPickRef.current === pickRef.current) setRefreshRateState(rate);
        }
      } catch (error) {
        // User-initiated action (changing the refresh rate setting). The optimistic state
        // update above already applied, so the newest pick goes back to the saved rate, and without
        // the toast the failure would be invisible. RefreshRateProvider is an ancestor of
        // NotificationsProvider in AppProviders.tsx, so useErrorHandler is not reachable here -
        // use the existing show-toast bridge instead (the SHOW_TOAST listener in
        // NotificationsContext.tsx).
        if (pickRef.current === pick) {
          revertedPickRef.current = pick;
          setRefreshRateState(savedRateRef.current);
        }
        console.error('Failed to save refresh rate:', error);
        notifyRefreshRateSaveFailed(error);
      } finally {
        savesInFlightRef.current -= 1;
      }
    },
    [isControlledByAdmin, authMode]
  );

  const getRefreshInterval = useCallback(() => {
    return REFRESH_RATES[refreshRate];
  }, [refreshRate]);

  const value = {
    refreshRate,
    setRefreshRate,
    getRefreshInterval,
    isControlledByAdmin,
    systemRate,
    error: error ?? (ownRateSession ? preferencesError : null),
    reload
  };

  // Only render children after we've loaded the refresh rate from API
  // This prevents a flash of default rate before the actual rate is loaded
  if (!isLoaded) {
    return null;
  }

  return <RefreshRateContext.Provider value={value}>{children}</RefreshRateContext.Provider>;
};
