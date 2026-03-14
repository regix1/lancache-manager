import React, { type ReactNode } from 'react';
import { useSteamWebApiStatus as useHook } from '@hooks/useSteamWebApiStatus';
import { SteamWebApiStatusContext } from './SteamWebApiStatusContext.types';

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
