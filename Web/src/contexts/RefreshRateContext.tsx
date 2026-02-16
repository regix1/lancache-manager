import React, { createContext, useContext, useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import { REFRESH_RATES, type RefreshRate } from '@utils/constants';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import type {
  GuestRefreshRateUpdatedEvent,
  DefaultGuestRefreshRateChangedEvent,
  GuestRefreshRateLockChangedEvent,
  UserPreferencesUpdatedEvent
} from '@contexts/SignalRContext/types';

interface RefreshRateContextType {
  refreshRate: RefreshRate;
  setRefreshRate: (rate: RefreshRate) => void;
  getRefreshInterval: () => number;
  isControlledByAdmin: boolean; // True for guests - they can't change their refresh rate
}

const RefreshRateContext = createContext<RefreshRateContextType | undefined>(undefined);

export const useRefreshRate = () => {
  const context = useContext(RefreshRateContext);
  if (!context) {
    throw new Error('useRefreshRate must be used within RefreshRateProvider');
  }
  return context;
};

interface RefreshRateProviderProps {
  children: ReactNode;
}

export const RefreshRateProvider: React.FC<RefreshRateProviderProps> = ({ children }) => {
  // Default to STANDARD (10s) until we fetch from API
  const [refreshRate, setRefreshRateState] = useState<RefreshRate>('STANDARD');
  const [isLoaded, setIsLoaded] = useState(false);
  const [isControlledByAdmin, setIsControlledByAdmin] = useState(false);

  const { on, off } = useSignalR();
  const { authMode, sessionId } = useAuth();

  // Refs to avoid stale closures in SignalR handlers
  const authModeRef = useRef(authMode);
  const sessionIdRef = useRef(sessionId);
  useEffect(() => { authModeRef.current = authMode; }, [authMode]);
  useEffect(() => { sessionIdRef.current = sessionId; }, [sessionId]);

  // Fetch refresh rate based on auth mode
  useEffect(() => {
    const fetchRefreshRate = async () => {
      try {
        if (authMode === 'guest') {
          // Fetch global lock and default rate
          const defaultResponse = await fetch('/api/system/default-guest-refresh-rate', {
            credentials: 'include'
          });
          let globalLocked = true;
          let defaultRate: string | null = null;
          if (defaultResponse.ok) {
            const defaultData = await defaultResponse.json();
            globalLocked = defaultData.locked ?? true;
            defaultRate = defaultData.refreshRate;
          }

          // Fetch per-session preferences (may have per-session lock override)
          const prefsResponse = await fetch('/api/user-preferences', {
            credentials: 'include'
          });

          let perSessionLocked: boolean | null = null;
          if (prefsResponse.ok) {
            const prefsData = await prefsResponse.json();
            perSessionLocked = prefsData.refreshRateLocked ?? null;

            if (prefsData.refreshRate && prefsData.refreshRate in REFRESH_RATES) {
              setRefreshRateState(prefsData.refreshRate as RefreshRate);
            } else if (defaultRate && defaultRate in REFRESH_RATES) {
              setRefreshRateState(defaultRate as RefreshRate);
            }
          } else if (defaultRate && defaultRate in REFRESH_RATES) {
            setRefreshRateState(defaultRate as RefreshRate);
          }

          // Per-session override takes precedence: false means unlocked, true means locked
          // null means use global default. The toggle is "allow guest to change" so
          // refreshRateLocked=false means unlocked (guest CAN change)
          const effectiveLocked = perSessionLocked !== null ? perSessionLocked : globalLocked;
          setIsControlledByAdmin(effectiveLocked);
        } else {
          // For authenticated users, fetch the global rate
          setIsControlledByAdmin(false);

          const response = await fetch('/api/system/refresh-rate', {
            credentials: 'include'
          });
          if (response.ok) {
            const data = await response.json();
            if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
              setRefreshRateState(data.refreshRate as RefreshRate);
            }
          }
        }
      } catch (error) {
        console.error('Failed to fetch refresh rate:', error);
      } finally {
        setIsLoaded(true);
      }
    };

    fetchRefreshRate();
  }, [authMode]);

  // Listen for SignalR events.
  // All handlers read authMode/sessionId from refs to avoid stale closures.
  useEffect(() => {
    // Handle admin pushing a new rate to this specific guest
    const handleGuestRefreshRateUpdated = (data: GuestRefreshRateUpdatedEvent) => {
      if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
        setRefreshRateState(data.refreshRate as RefreshRate);
      }
    };

    // Handle default guest rate change (affects guests using default)
    const handleDefaultGuestRefreshRateChanged = (data: DefaultGuestRefreshRateChangedEvent) => {
      if (authModeRef.current === 'guest' && data.refreshRate && data.refreshRate in REFRESH_RATES) {
        fetch('/api/user-preferences', { credentials: 'include' })
          .then((res) => res.json())
          .then((prefsData) => {
            if (!prefsData.refreshRate) {
              setRefreshRateState(data.refreshRate as RefreshRate);
            }
          })
          .catch(() => {
            setRefreshRateState(data.refreshRate as RefreshRate);
          });
      }
    };

    // Handle global lock state change - respect per-session override
    const handleGuestRefreshRateLockChanged = (data: GuestRefreshRateLockChangedEvent) => {
      if (authModeRef.current === 'guest') {
        fetch('/api/user-preferences', { credentials: 'include' })
          .then((res) => res.json())
          .then((prefsData) => {
            const perSessionLocked: boolean | null = prefsData.refreshRateLocked ?? null;
            const effectiveLocked = perSessionLocked !== null ? perSessionLocked : data.locked;
            setIsControlledByAdmin(effectiveLocked);
          })
          .catch(() => {
            setIsControlledByAdmin(data.locked);
          });
      }
    };

    // Handle per-session preferences update (includes refreshRateLocked changes)
    const handleUserPreferencesUpdated = (data: UserPreferencesUpdatedEvent) => {
      if (authModeRef.current !== 'guest') return;
      if (data.sessionId !== sessionIdRef.current) return;

      // Update refresh rate if changed
      if (data.preferences.refreshRate && data.preferences.refreshRate in REFRESH_RATES) {
        setRefreshRateState(data.preferences.refreshRate as RefreshRate);
      }

      // Re-evaluate lock state: fetch global lock to use as fallback
      fetch('/api/system/default-guest-refresh-rate', { credentials: 'include' })
        .then((res) => res.json())
        .then((globalData) => {
          const globalLocked = globalData.locked ?? true;
          // The preferences payload doesn't include refreshRateLocked directly,
          // so re-fetch per-session prefs to get the current value
          return fetch('/api/user-preferences', { credentials: 'include' })
            .then((res) => res.json())
            .then((prefsData) => {
              const perSessionLocked: boolean | null = prefsData.refreshRateLocked ?? null;
              const effectiveLocked = perSessionLocked !== null ? perSessionLocked : globalLocked;
              setIsControlledByAdmin(effectiveLocked);
            });
        })
        .catch(() => {
          // On error, leave current state
        });
    };

    on('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
    on('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
    on('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);
    on('UserPreferencesUpdated', handleUserPreferencesUpdated);

    return () => {
      off('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
      off('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
      off('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);
      off('UserPreferencesUpdated', handleUserPreferencesUpdated);
    };
  }, [on, off]);

  const setRefreshRate = useCallback(
    async (rate: RefreshRate) => {
      // Block guests from changing their refresh rate when locked
      if (isControlledByAdmin) {
        console.warn('[RefreshRate] Guest users cannot change their refresh rate (locked by admin)');
        return;
      }

      // Optimistically update state
      setRefreshRateState(rate);

      // Save to API - guests save to user-preferences, admins to system refresh-rate
      try {
        const endpoint = authMode === 'guest'
          ? '/api/user-preferences/refreshRate'
          : '/api/system/refresh-rate';
        const body = authMode === 'guest'
          ? JSON.stringify(rate)
          : JSON.stringify({ refreshRate: rate });

        const response = await fetch(endpoint, {
          method: 'PATCH',
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json'
          },
          body
        });

        if (!response.ok) {
          console.error('Failed to save refresh rate to API');
        }
      } catch (error) {
        console.error('Failed to save refresh rate:', error);
      }
    },
    [isControlledByAdmin, authMode]
  );

  const getRefreshInterval = useCallback(() => {
    return REFRESH_RATES[refreshRate];
  }, [refreshRate]);

  const value: RefreshRateContextType = {
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
