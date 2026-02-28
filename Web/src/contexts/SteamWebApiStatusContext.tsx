import React, { createContext, useContext, type ReactNode } from 'react';
import {
  useSteamWebApiStatus as useHook,
  type SteamWebApiStatus
} from '@hooks/useSteamWebApiStatus';

interface SteamWebApiStatusContextType {
  status: SteamWebApiStatus | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
  updateStatus: (updater: (prev: SteamWebApiStatus | null) => SteamWebApiStatus | null) => void;
}

const SteamWebApiStatusContext = createContext<SteamWebApiStatusContextType | undefined>(undefined);

export const useSteamWebApiStatus = () => {
  const context = useContext(SteamWebApiStatusContext);
  if (!context) {
    throw new Error('useSteamWebApiStatus must be used within SteamWebApiStatusProvider');
  }
  return context;
};

interface SteamWebApiStatusProviderProps {
  children: ReactNode;
}

export const SteamWebApiStatusProvider: React.FC<SteamWebApiStatusProviderProps> = ({
  children
}) => {
  const steamWebApiStatus = useHook();

  return (
    <SteamWebApiStatusContext.Provider value={steamWebApiStatus}>
      {children}
    </SteamWebApiStatusContext.Provider>
  );
};
