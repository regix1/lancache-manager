import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import { APP_EVENTS, REFRESH_RATES, type RefreshRate } from '@utils/constants';
import ApiService from '@services/api.service';
import { assertOk } from '@services/apiError';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useAuth } from '@contexts/useAuth';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import type {
  GuestRefreshRateUpdatedEvent,
  DefaultGuestRefreshRateChangedEvent,
  GuestRefreshRateLockChangedEvent,
  ShowToastEvent
} from '@contexts/SignalRContext/types';
import { RefreshRateContext } from './RefreshRateContext.types';

// RefreshRateProvider is an ancestor of NotificationsProvider in AppProviders.tsx, so
// useErrorHandler (useNotifications) is not reachable from it - it would throw. Use the existing
// show-toast bridge instead (mirrors NotificationsContext.tsx:332-356).
const notifyRefreshRateSaveFailed = (): void => {
  window.dispatchEvent(
    new CustomEvent<ShowToastEvent>(APP_EVENTS.SHOW_TOAST, {
      detail: {
        type: 'error',
        message: 'Failed to save your refresh rate setting.',
        duration: 4000
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

  const { on, off, isConnected } = useSignalR();
  const { authMode } = useAuth();
  const { currentPreferences } = useSessionPreferences();

  // Ref to avoid stale closures in SignalR handlers
  const authModeRef = useRef(authMode);
  // Every writer below bumps this, and a fetch response only lands if the generation it captured
  // still matches, so an answer superseded while in flight loses instead of winning.
  const generationRef = useRef(0);
  useEffect(() => {
    authModeRef.current = authMode;
    generationRef.current += 1;
  }, [authMode]);

  // Fetch global default guest rate and lock state (for guests only)
  const fetchGlobalDefaults = useCallback(async () => {
    const requestGeneration = generationRef.current;
    try {
      const response = await fetch('/api/system/default-guest-refresh-rate', {
        credentials: 'include'
      });
      if (response.ok) {
        const data = await response.json();
        if (generationRef.current !== requestGeneration) return;
        setGlobalLocked(data.locked ?? true);
        setDefaultGuestRate(data.refreshRate || null);
      }
    } catch (error) {
      // Background fetch for guests; the STANDARD/locked defaults already set as initial state
      // remain in effect. Deliberately silent.
      console.error('Failed to fetch global guest defaults:', error);
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

  // Guest users derive refresh state from their session preferences plus the
  // global guest defaults/lock.
  useEffect(() => {
    if (authMode !== 'guest') return;

    if (!currentPreferences) {
      // Guest preferences not loaded yet
      return;
    }

    // Guest: use per-session refreshRate, or fall back to global default
    const perSessionRate = currentPreferences.refreshRate;
    const perSessionLocked = currentPreferences.refreshRateLocked;

    if (perSessionRate && perSessionRate in REFRESH_RATES) {
      setRefreshRateState(perSessionRate as RefreshRate);
    } else if (defaultGuestRate && defaultGuestRate in REFRESH_RATES) {
      setRefreshRateState(defaultGuestRate as RefreshRate);
    }

    // Per-session override takes precedence: false means unlocked, true means locked
    // null/undefined means use global default
    const effectiveLocked =
      perSessionLocked !== null && perSessionLocked !== undefined ? perSessionLocked : globalLocked;
    setIsControlledByAdmin(effectiveLocked);
    setIsLoaded(true);
  }, [authMode, currentPreferences, defaultGuestRate, globalLocked]);

  // Authenticated users always use the global system refresh rate and should
  // never inherit guest lock/default behavior.
  const fetchSystemRate = useCallback(async () => {
    const requestGeneration = generationRef.current;
    try {
      const response = await fetch('/api/system/refresh-rate', {
        credentials: 'include'
      });
      if (response.ok) {
        const data = await response.json();
        if (
          generationRef.current === requestGeneration &&
          data.refreshRate &&
          data.refreshRate in REFRESH_RATES
        ) {
          setRefreshRateState(data.refreshRate as RefreshRate);
        }
      }
    } catch (error) {
      // Background fetch for admins; falls back to the STANDARD default already set as initial
      // state. Deliberately silent.
      console.error('Failed to fetch system refresh rate:', error);
    } finally {
      if (generationRef.current === requestGeneration) {
        setIsLoaded(true);
      }
    }
  }, []);

  useEffect(() => {
    if (authMode !== 'authenticated') return;

    setIsControlledByAdmin(false);
    void fetchSystemRate();
  }, [authMode, fetchSystemRate]);

  // Listen for SignalR events.
  // All handlers read authMode/sessionId from refs to avoid stale closures.
  useEffect(() => {
    // Handle admin pushing a new rate to this specific guest
    // SessionPreferencesContext will handle the UserPreferencesUpdated event,
    // but this event is guest-specific and immediate
    const handleGuestRefreshRateUpdated = (data: GuestRefreshRateUpdatedEvent) => {
      if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
        generationRef.current += 1;
        setRefreshRateState(data.refreshRate as RefreshRate);
      }
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

    return () => {
      off('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
      off('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
      off('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);
    };
  }, [on, off]);

  // An unlock broadcast while the socket was down is gone for good, and it leaves the control
  // disabled with nothing on screen to say so. One callback: the fetches never both apply.
  useReconnectRefetch(isConnected, () => {
    if (authModeRef.current === 'guest') {
      void fetchGlobalDefaults();
    } else if (authModeRef.current === 'authenticated') {
      void fetchSystemRate();
    }
  });

  const setRefreshRate = useCallback(
    async (rate: RefreshRate) => {
      // Block guests from changing their refresh rate when locked
      if (isControlledByAdmin) {
        console.warn(
          '[RefreshRate] Guest users cannot change their refresh rate (locked by admin)'
        );
        return;
      }

      // Optimistically update state
      generationRef.current += 1;
      setRefreshRateState(rate);

      // Save to API - guests save to user-preferences, admins to system refresh-rate
      try {
        const endpoint =
          authMode === 'guest' ? '/api/user-preferences/refreshRate' : '/api/system/refresh-rate';
        const body = authMode === 'guest' ? rate : { refreshRate: rate };

        const response = await fetch(
          endpoint,
          ApiService.getJsonFetchOptions(body, { method: 'PATCH' })
        );

        await assertOk(response);
      } catch (error) {
        // User-initiated action (changing the refresh rate setting). The optimistic state
        // update above already applied, so without this the failure would be invisible and the
        // choice would silently not persist. RefreshRateProvider is an ancestor of
        // NotificationsProvider in AppProviders.tsx, so useErrorHandler is not reachable here -
        // use the existing show-toast bridge instead (mirrors NotificationsContext.tsx:332-356).
        console.error('Failed to save refresh rate:', error);
        notifyRefreshRateSaveFailed();
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
    isControlledByAdmin
  };

  // Only render children after we've loaded the refresh rate from API
  // This prevents a flash of default rate before the actual rate is loaded
  if (!isLoaded) {
    return null;
  }

  return <RefreshRateContext.Provider value={value}>{children}</RefreshRateContext.Provider>;
};
