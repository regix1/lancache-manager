import { useContext } from 'react';
import { GuestConfigContext } from './GuestConfigContext.types';

export const useGuestConfig = () => {
  const context = useContext(GuestConfigContext);
  if (!context) {
    throw new Error('useGuestConfig must be used within GuestConfigProvider');
  }
  return context;
};
