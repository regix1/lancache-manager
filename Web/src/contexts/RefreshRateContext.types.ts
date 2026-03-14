import { createContext } from 'react';
import type { RefreshRate } from '@utils/constants';

interface RefreshRateContextType {
  refreshRate: RefreshRate;
  setRefreshRate: (rate: RefreshRate) => void;
  getRefreshInterval: () => number;
  isControlledByAdmin: boolean; // True for guests - they can't change their refresh rate
}

export const RefreshRateContext = createContext<RefreshRateContextType | undefined>(undefined);
