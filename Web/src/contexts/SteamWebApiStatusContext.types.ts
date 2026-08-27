import { createContext } from 'react';
import type { SteamWebApiStatus } from '@hooks/useSteamWebApiStatus';

interface SteamWebApiStatusContextType {
  status: SteamWebApiStatus | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}

export const SteamWebApiStatusContext = createContext<SteamWebApiStatusContextType | undefined>(
  undefined
);
