import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  useCallback,
  useRef,
  type ReactNode
} from 'react';
import { REFRESH_RATES, type RefreshRate } from '@utils/constants';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import { useSessionPreferences } from '@contexts/SessionPreferencesContext';
import type {
  GuestRefreshRateUpdatedEvent,
  DefaultGuestRefreshRateChangedEvent,
  GuestRefreshRateLockChangedEvent
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

export const RefreshRateProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  // Default to STANDARD (10s) until we fetch from API
  const [refreshRate, setRefreshRateState] = useState<RefreshRate>('STANDARD');
  const [isLoaded, setIsLoaded] = useState(false);
  const [isControlledByAdmin, setIsControlledByAdmin] = useState(false);
  const [defaultGuestRate, setDefaultGuestRate] = useState<string | null>(null);
  const [globalLocked, setGlobalLocked] = useState<boolean>(true);

  const { on, off } = useSignalR();
  const { authMode } = useAuth();
  const { currentPreferences } = useSessionPreferences();

  // Ref to avoid stale closures in SignalR handlers
  const authModeRef = useRef(authMode);
  useEffect(() => {
    authModeRef.current = authMode;
  }, [authMode]);

  // Fetch global default guest rate and lock state (for guests only)
  useEffect(() => {
    if (authMode !== 'guest') return;

    const fetchGlobalDefaults = async () => {
      try {
        const response = await fetch('/api/system/default-guest-refresh-rate', {
          credentials: 'include'
        });
        if (response.ok) {
          const data = await response.json();
          setGlobalLocked(data.locked ?? true);
          setDefaultGuestRate(data.refreshRate || null);
        }
      } catch (error) {
        console.error('Failed to fetch global guest defaults:', error);
      }
    };

    fetchGlobalDefaults();
  }, [authMode]);

  // Derive refresh rate from SessionPreferencesContext (guests and authenticated)
  useEffect(() => {
    // For unauthenticated users, just mark as loaded with defaults so the
    // UI tree below this provider can render (e.g. the login modal).
    if (authMode === 'unauthenticated') {
      setIsLoaded(true);
      return;
    }

    if (!currentPreferences) {
      // Preferences not loaded yet
      return;
    }

    if (authMode === 'guest') {
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
        perSessionLocked !== null && perSessionLocked !== undefined
          ? perSessionLocked
          : globalLocked;
      setIsControlledByAdmin(effectiveLocked);
    } else {
      // Authenticated: fetch global system refresh rate (not from user preferences)
      setIsControlledByAdmin(false);

      const fetchSystemRate = async () => {
        try {
          const response = await fetch('/api/system/refresh-rate', {
            credentials: 'include'
          });
          if (response.ok) {
            const data = await response.json();
            if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
              setRefreshRateState(data.refreshRate as RefreshRate);
            }
          }
        } catch (error) {
          console.error('Failed to fetch system refresh rate:', error);
        }
      };

      fetchSystemRate();
    }

    setIsLoaded(true);
  }, [authMode, currentPreferences, defaultGuestRate, globalLocked]);

  // Listen for SignalR events.
  // All handlers read authMode/sessionId from refs to avoid stale closures.
  useEffect(() => {
    // Handle admin pushing a new rate to this specific guest
    // SessionPreferencesContext will handle the UserPreferencesUpdated event,
    // but this event is guest-specific and immediate
    const handleGuestRefreshRateUpdated = (data: GuestRefreshRateUpdatedEvent) => {
      if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
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
        setDefaultGuestRate(data.refreshRate);
        // If no per-session rate is set (using default), update immediately
        // SessionPreferencesContext will have currentPreferences.refreshRate === null
      }
    };

    // Handle global lock state change
    // Update global lock state, effective lock will be recalculated in the main effect
    const handleGuestRefreshRateLockChanged = (data: GuestRefreshRateLockChangedEvent) => {
      if (authModeRef.current === 'guest') {
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
      setRefreshRateState(rate);

      // Save to API - guests save to user-preferences, admins to system refresh-rate
      try {
        const endpoint =
          authMode === 'guest' ? '/api/user-preferences/refreshRate' : '/api/system/refresh-rate';
        const body =
          authMode === 'guest' ? JSON.stringify(rate) : JSON.stringify({ refreshRate: rate });

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
