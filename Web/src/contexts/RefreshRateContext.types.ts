import { createContext } from 'react';
import type { RefreshRate } from '@utils/constants';

interface RefreshRateContextType {
  refreshRate: RefreshRate;
  setRefreshRate: (rate: RefreshRate) => void;
  getRefreshInterval: () => number;
  isControlledByAdmin: boolean; // True when admin has locked the refresh rate for the current guest session
  error: string | null; // Why the last settings read failed; null once a read succeeds
  reload: () => void;
}

export const RefreshRateContext = createContext<RefreshRateContextType | undefined>(undefined);
