import { createContext } from 'react';
import type { RefreshRate } from '@utils/constants';

interface RefreshRateContextType {
  refreshRate: RefreshRate;
  setRefreshRate: (rate: RefreshRate) => void;
  getRefreshInterval: () => number;
  isControlledByAdmin: boolean; // True when the admin has locked the refresh rate for this guest or user-role session, and while that session's own preferences have not been read yet
  systemRate: RefreshRate; // The install-wide rate an account follows while its session has no rate of its own
  error: string | null; // Why the last settings read failed; null once a read succeeds
  reload: () => void;
}

export const RefreshRateContext = createContext<RefreshRateContextType | undefined>(undefined);
