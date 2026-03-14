import { useContext } from 'react';
import { SteamWebApiStatusContext } from './SteamWebApiStatusContext.types';

export const useSteamWebApiStatus = () => {
  const context = useContext(SteamWebApiStatusContext);
  if (!context) {
    throw new Error('useSteamWebApiStatus must be used within SteamWebApiStatusProvider');
  }
  return context;
};
