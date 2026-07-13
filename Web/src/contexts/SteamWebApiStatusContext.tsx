import React, { type ReactNode } from 'react';
import { useSteamWebApiStatusState } from '@hooks/useSteamWebApiStatus';
import { SteamWebApiStatusContext } from './SteamWebApiStatusContext.types';

interface SteamWebApiStatusProviderProps {
  children: ReactNode;
}

export const SteamWebApiStatusProvider: React.FC<SteamWebApiStatusProviderProps> = ({
  children
}) => {
  const steamWebApiStatus = useSteamWebApiStatusState();

  return (
    <SteamWebApiStatusContext.Provider value={steamWebApiStatus}>
      {children}
    </SteamWebApiStatusContext.Provider>
  );
};
