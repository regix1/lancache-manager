import React, { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from 'react';
import { POLLING_RATES, type PollingRate } from '@utils/constants';

interface PollingRateContextType {
  pollingRate: PollingRate;
  setPollingRate: (rate: PollingRate) => void;
  getPollingInterval: () => number;
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

  // Fetch polling rate from API on mount
  useEffect(() => {
    const fetchPollingRate = async () => {
      try {
        const response = await fetch('/api/system/polling-rate');
        if (response.ok) {
          const data = await response.json();
          if (data.pollingRate && data.pollingRate in POLLING_RATES) {
            setPollingRateState(data.pollingRate as PollingRate);
          }
        }
      } catch (error) {
        console.error('Failed to fetch polling rate:', error);
      } finally {
        setIsLoaded(true);
      }
    };

    fetchPollingRate();
  }, []);

  const setPollingRate = useCallback(async (rate: PollingRate) => {
    // Optimistically update state
    setPollingRateState(rate);

    // Save to API
    try {
      const response = await fetch('/api/system/polling-rate', {
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ pollingRate: rate })
      });

      if (!response.ok) {
        console.error('Failed to save polling rate to API');
      }
    } catch (error) {
      console.error('Failed to save polling rate:', error);
    }
  }, []);

  const getPollingInterval = useCallback(() => {
    return POLLING_RATES[pollingRate];
  }, [pollingRate]);

  const value: PollingRateContextType = {
    pollingRate,
    setPollingRate,
    getPollingInterval
  };

  // Only render children after we've loaded the polling rate from API
  // This prevents a flash of default rate before the actual rate is loaded
  if (!isLoaded) {
    return null;
  }

  return <PollingRateContext.Provider value={value}>{children}</PollingRateContext.Provider>;
};
