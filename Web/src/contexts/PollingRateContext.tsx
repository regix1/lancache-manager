import React, { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from 'react';
import { POLLING_RATES, type PollingRate } from '@utils/constants';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import authService from '@services/auth.service';
import type {
  GuestPollingRateUpdatedPayload,
  DefaultGuestPollingRateChangedPayload
} from '@contexts/SignalRContext/types';

interface PollingRateContextType {
  pollingRate: PollingRate;
  setPollingRate: (rate: PollingRate) => void;
  getPollingInterval: () => number;
  isControlledByAdmin: boolean; // True for guests - they can't change their polling rate
}

const PollingRateContext = createContext<PollingRateContextType | undefined>(undefined);

export const usePollingRate = () => {
  const context = useContext(PollingRateContext);
  if (!context) {
    throw new Error('usePollingRate must be used within PollingRateProvider');
  }
  return context;
};

interface PollingRateProviderProps {
  children: ReactNode;
}

export const PollingRateProvider: React.FC<PollingRateProviderProps> = ({ children }) => {
  // Default to STANDARD (10s) until we fetch from API
  const [pollingRate, setPollingRateState] = useState<PollingRate>('STANDARD');
  const [isLoaded, setIsLoaded] = useState(false);
  const [isControlledByAdmin, setIsControlledByAdmin] = useState(false);

  const { on, off } = useSignalR();
  const { authMode } = useAuth();

  // Fetch polling rate based on auth mode
  useEffect(() => {
    const fetchPollingRate = async () => {
      try {
        if (authMode === 'guest') {
          // For guests, fetch from user preferences first, then default guest rate
          setIsControlledByAdmin(true);

          // Try to get user-specific polling rate from preferences
          const prefsResponse = await fetch('/api/user-preferences', {
            credentials: 'include',
            headers: authService.getAuthHeaders()
          });

          if (prefsResponse.ok) {
            const prefsData = await prefsResponse.json();
            if (prefsData.pollingRate && prefsData.pollingRate in POLLING_RATES) {
              setPollingRateState(prefsData.pollingRate as PollingRate);
              setIsLoaded(true);
              return;
            }
          }

          // Fall back to default guest polling rate
          const defaultResponse = await fetch('/api/system/default-guest-polling-rate');
          if (defaultResponse.ok) {
            const data = await defaultResponse.json();
            if (data.pollingRate && data.pollingRate in POLLING_RATES) {
              setPollingRateState(data.pollingRate as PollingRate);
            }
          }
        } else {
          // For authenticated users, fetch the global polling rate
          setIsControlledByAdmin(false);

          const response = await fetch('/api/system/polling-rate', {
            credentials: 'include',
            headers: authService.getAuthHeaders()
          });
          if (response.ok) {
            const data = await response.json();
            if (data.pollingRate && data.pollingRate in POLLING_RATES) {
              setPollingRateState(data.pollingRate as PollingRate);
            }
          }
        }
      } catch (error) {
        console.error('Failed to fetch polling rate:', error);
      } finally {
        setIsLoaded(true);
      }
    };

    fetchPollingRate();
  }, [authMode]);

  // Listen for SignalR events
  useEffect(() => {
    // Handle admin pushing a new rate to this specific guest
    const handleGuestPollingRateUpdated = (data: GuestPollingRateUpdatedPayload) => {
      console.log('[PollingRate] Received GuestPollingRateUpdated:', data);
      if (data.pollingRate && data.pollingRate in POLLING_RATES) {
        setPollingRateState(data.pollingRate as PollingRate);
      }
    };

    // Handle default guest polling rate change (affects guests using default)
    const handleDefaultGuestPollingRateChanged = (data: DefaultGuestPollingRateChangedPayload) => {
      console.log('[PollingRate] Received DefaultGuestPollingRateChanged:', data);
      // Only update if this is a guest user (they might be using the default)
      if (authMode === 'guest' && data.pollingRate && data.pollingRate in POLLING_RATES) {
        // Re-fetch to see if we should use the new default
        // (only if we don't have a custom rate set)
        fetch('/api/user-preferences', { credentials: 'include', headers: authService.getAuthHeaders() })
          .then((res) => res.json())
          .then((prefsData) => {
            // If no custom polling rate set, use the new default
            if (!prefsData.pollingRate) {
              setPollingRateState(data.pollingRate as PollingRate);
            }
          })
          .catch((error) => {
            console.error('Failed to check user preferences:', error);
            // On error, just apply the new default
            setPollingRateState(data.pollingRate as PollingRate);
          });
      }
    };

    on('GuestPollingRateUpdated', handleGuestPollingRateUpdated);
    on('DefaultGuestPollingRateChanged', handleDefaultGuestPollingRateChanged);

    return () => {
      off('GuestPollingRateUpdated', handleGuestPollingRateUpdated);
      off('DefaultGuestPollingRateChanged', handleDefaultGuestPollingRateChanged);
    };
  }, [on, off, authMode]);

  const setPollingRate = useCallback(
    async (rate: PollingRate) => {
      // Block guests from changing their polling rate
      if (isControlledByAdmin) {
        console.warn('[PollingRate] Guest users cannot change their polling rate');
        return;
      }

      // Optimistically update state
      setPollingRateState(rate);

      // Save to API
      try {
        const response = await fetch('/api/system/polling-rate', {
          method: 'PATCH',
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json',
            ...authService.getAuthHeaders()
          },
          body: JSON.stringify({ pollingRate: rate })
        });

        if (!response.ok) {
          console.error('Failed to save polling rate to API');
        }
      } catch (error) {
        console.error('Failed to save polling rate:', error);
      }
    },
    [isControlledByAdmin]
  );

  const getPollingInterval = useCallback(() => {
    return POLLING_RATES[pollingRate];
  }, [pollingRate]);

  const value: PollingRateContextType = {
    pollingRate,
    setPollingRate,
    getPollingInterval,
    isControlledByAdmin
  };

  // Only render children after we've loaded the polling rate from API
  // This prevents a flash of default rate before the actual rate is loaded
  if (!isLoaded) {
    return null;
  }

  return <PollingRateContext.Provider value={value}>{children}</PollingRateContext.Provider>;
};
