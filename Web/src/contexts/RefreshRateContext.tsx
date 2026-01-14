import React, { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from 'react';
import { REFRESH_RATES, type RefreshRate } from '@utils/constants';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import authService from '@services/auth.service';
import type {
  GuestRefreshRateUpdatedEvent,
  DefaultGuestRefreshRateChangedEvent
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
  const { authMode } = useAuth();

  // Fetch refresh rate based on auth mode
  useEffect(() => {
    const fetchRefreshRate = async () => {
      try {
        if (authMode === 'guest') {
          // For guests, fetch from user preferences first, then default guest rate
          setIsControlledByAdmin(true);

          // Try to get user-specific rate from preferences
          const prefsResponse = await fetch('/api/user-preferences', {
            credentials: 'include',
            headers: authService.getAuthHeaders()
          });

          if (prefsResponse.ok) {
            const prefsData = await prefsResponse.json();
            if (prefsData.refreshRate && prefsData.refreshRate in REFRESH_RATES) {
              setRefreshRateState(prefsData.refreshRate as RefreshRate);
              setIsLoaded(true);
              return;
            }
          }

          // Fall back to default guest rate
          const defaultResponse = await fetch('/api/system/default-guest-refresh-rate', {
            credentials: 'include',
            headers: authService.getAuthHeaders()
          });
          if (defaultResponse.ok) {
            const data = await defaultResponse.json();
            if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
              setRefreshRateState(data.refreshRate as RefreshRate);
            }
          }
        } else {
          // For authenticated users, fetch the global rate
          setIsControlledByAdmin(false);

          const response = await fetch('/api/system/refresh-rate', {
            credentials: 'include',
            headers: authService.getAuthHeaders()
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

  // Listen for SignalR events
  useEffect(() => {
    // Handle admin pushing a new rate to this specific guest
    const handleGuestRefreshRateUpdated = (data: GuestRefreshRateUpdatedEvent) => {
      console.log('[RefreshRate] Received GuestRefreshRateUpdated:', data);
      if (data.refreshRate && data.refreshRate in REFRESH_RATES) {
        setRefreshRateState(data.refreshRate as RefreshRate);
      }
    };

    // Handle default guest rate change (affects guests using default)
    const handleDefaultGuestRefreshRateChanged = (data: DefaultGuestRefreshRateChangedEvent) => {
      console.log('[RefreshRate] Received DefaultGuestRefreshRateChanged:', data);
      // Only update if this is a guest user (they might be using the default)
      if (authMode === 'guest' && data.refreshRate && data.refreshRate in REFRESH_RATES) {
        // Re-fetch to see if we should use the new default
        // (only if we don't have a custom rate set)
        fetch('/api/user-preferences', { credentials: 'include', headers: authService.getAuthHeaders() })
          .then((res) => res.json())
          .then((prefsData) => {
            // If no custom rate set, use the new default
            if (!prefsData.refreshRate) {
              setRefreshRateState(data.refreshRate as RefreshRate);
            }
          })
          .catch((error) => {
            console.error('Failed to check user preferences:', error);
            // On error, just apply the new default
            setRefreshRateState(data.refreshRate as RefreshRate);
          });
      }
    };

    on('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
    on('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);

    return () => {
      off('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
      off('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
    };
  }, [on, off, authMode]);

  const setRefreshRate = useCallback(
    async (rate: RefreshRate) => {
      // Block guests from changing their refresh rate
      if (isControlledByAdmin) {
        console.warn('[RefreshRate] Guest users cannot change their refresh rate');
        return;
      }

      // Optimistically update state
      setRefreshRateState(rate);

      // Save to API
      try {
        const response = await fetch('/api/system/refresh-rate', {
          method: 'PATCH',
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json',
            ...authService.getAuthHeaders()
          },
          body: JSON.stringify({ refreshRate: rate })
        });

        if (!response.ok) {
          console.error('Failed to save refresh rate to API');
        }
      } catch (error) {
        console.error('Failed to save refresh rate:', error);
      }
    },
    [isControlledByAdmin]
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
