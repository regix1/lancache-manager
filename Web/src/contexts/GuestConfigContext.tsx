import React, { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useSignalR } from '@contexts/SignalRContext';

interface GuestConfigContextType {
  guestDurationHours: number;
  guestModeLocked: boolean;
  isLoading: boolean;
}

const GuestConfigContext = createContext<GuestConfigContextType | undefined>(undefined);

export const useGuestConfig = () => {
  const context = useContext(GuestConfigContext);
  if (!context) {
    throw new Error('useGuestConfig must be used within GuestConfigProvider');
  }
  return context;
};

interface GuestConfigProviderProps {
  children: ReactNode;
}

export const GuestConfigProvider: React.FC<GuestConfigProviderProps> = ({ children }) => {
  const signalR = useSignalR();
  const [guestDurationHours, setGuestDurationHours] = useState<number>(6);
  const [guestModeLocked, setGuestModeLocked] = useState<boolean>(false);
  const [isLoading, setIsLoading] = useState(true);

  // Fetch initial guest config from the public endpoint
  useEffect(() => {
    const fetchGuestConfig = async () => {
      try {
        // Use the public auth status endpoint which doesn't require auth
        const response = await fetch('/api/auth/guest/status');
        if (response.ok) {
          const data = await response.json();
          setGuestModeLocked(data.isLocked || false);
          if (data.durationHours) {
            setGuestDurationHours(data.durationHours);
          }
        }
      } catch (err) {
        console.error('[GuestConfig] Failed to fetch guest config:', err);
      } finally {
        setIsLoading(false);
      }
    };

    fetchGuestConfig();
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
