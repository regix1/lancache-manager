import { useContext } from 'react';
import { SteamAuthContext } from './SteamAuthContext.types';

export const useSteamAuth = () => {
  const context = useContext(SteamAuthContext);
  if (!context) {
    throw new Error('useSteamAuth must be used within SteamAuthProvider');
  }
  return context;
};
