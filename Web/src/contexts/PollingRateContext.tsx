import React, { createContext, useContext, useState, useEffect, type ReactNode } from 'react';
import { POLLING_RATES, STORAGE_KEYS, type PollingRate } from '@utils/constants';
import { storage } from '@utils/storage';

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
  // Load saved polling rate from localStorage, default to STANDARD (10s)
  const [pollingRate, setPollingRateState] = useState<PollingRate>(() => {
    const saved = storage.getItem(STORAGE_KEYS.POLLING_RATE);
    if (saved && saved in POLLING_RATES) {
      return saved as PollingRate;
    }
    return 'STANDARD'; // Default to 10 seconds
  });

  // Save polling rate to localStorage whenever it changes
  useEffect(() => {
    storage.setItem(STORAGE_KEYS.POLLING_RATE, pollingRate);
  }, [pollingRate]);

  const setPollingRate = (rate: PollingRate) => {
    setPollingRateState(rate);
  };

  const getPollingInterval = () => {
    return POLLING_RATES[pollingRate];
  };

  const value: PollingRateContextType = {
    pollingRate,
    setPollingRate,
    getPollingInterval
  };

  return <PollingRateContext.Provider value={value}>{children}</PollingRateContext.Provider>;
};
