import React, { useEffect, useState, type ReactNode } from 'react';
import authService from '@services/auth.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { APP_EVENTS } from '@utils/constants';
import { GuestConfigContext } from './GuestConfigContext.types';

export const GuestConfigProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const signalR = useSignalR();
  const [guestDurationHours, setGuestDurationHours] = useState<number>(
    authService.guestDurationHours
  );
  const [guestModeLocked, setGuestModeLocked] = useState<boolean>(!authService.guestAccessEnabled);
  const [isLoading, setIsLoading] = useState(true);

  // The sign-in screen shows both settings to a visitor who has no session, and the guest config
  // routes all require one, so they come from the status call AuthProvider already makes on mount
  // and repeats on every session change. AuthProvider announces each answer with this event.
  useEffect(() => {
    const applyAuthStatus = () => {
      setGuestDurationHours(authService.guestDurationHours);
      setGuestModeLocked(!authService.guestAccessEnabled);
      setIsLoading(false);
    };

    window.addEventListener(APP_EVENTS.AUTH_SESSION_UPDATED, applyAuthStatus);
    return () => window.removeEventListener(APP_EVENTS.AUTH_SESSION_UPDATED, applyAuthStatus);
  }, []);

  // Listen for real-time guest duration updates via SignalR
  useEffect(() => {
    const handleDurationUpdate = (update: { durationHours: number }) => {
      setGuestDurationHours(update.durationHours);
    };

    const handleLockUpdate = (update: { isLocked: boolean }) => {
      setGuestModeLocked(update.isLocked);
    };

    signalR.on('GuestDurationUpdated', handleDurationUpdate);
    signalR.on('GuestModeLockChanged', handleLockUpdate);

    return () => {
      signalR.off('GuestDurationUpdated', handleDurationUpdate);
      signalR.off('GuestModeLockChanged', handleLockUpdate);
    };
  }, [signalR]);

  return (
    <GuestConfigContext.Provider value={{ guestDurationHours, guestModeLocked, isLoading }}>
      {children}
    </GuestConfigContext.Provider>
  );
};
