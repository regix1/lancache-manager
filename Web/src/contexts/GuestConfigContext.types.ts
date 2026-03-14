import { createContext } from 'react';

interface GuestConfigContextType {
  guestDurationHours: number;
  guestModeLocked: boolean;
  isLoading: boolean;
}

export const GuestConfigContext = createContext<GuestConfigContextType | undefined>(undefined);
