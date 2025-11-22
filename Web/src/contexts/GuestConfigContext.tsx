import React, { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useSignalR } from '@contexts/SignalRContext';

interface GuestConfigContextType {
  guestDurationHours: number;
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
  const [isLoading, setIsLoading] = useState(true);

  // Guest session duration endpoint was removed during REST API refactoring
  // Using default value of 6 hours (can be updated via SignalR if backend sends updates)
  useEffect(() => {
    // Immediately mark as loaded with default value
    setIsLoading(false);
  }, []);

  // Listen for real-time guest duration updates via SignalR
  useEffect(() => {
    const handleDurationUpdate = (payload: { durationHours: number }) => {
      console.log('[GuestConfig] Duration updated via SignalR:', payload.durationHours);
      setGuestDurationHours(payload.durationHours);
    };

    signalR.on('GuestDurationUpdated', handleDurationUpdate);

    return () => {
      signalR.off('GuestDurationUpdated', handleDurationUpdate);
    };
  }, [signalR]);

  return (
    <GuestConfigContext.Provider value={{ guestDurationHours, isLoading }}>
      {children}
    </GuestConfigContext.Provider>
  );
};
