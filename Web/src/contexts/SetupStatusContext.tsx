import React, { createContext, useContext, useEffect, useState, type ReactNode } from 'react';

export interface SetupStatus {
  isCompleted: boolean;
  hasProcessedLogs: boolean;
  isSetupCompleted: boolean;
}

interface SetupStatusContextType {
  setupStatus: SetupStatus | null;
  isLoading: boolean;
  refreshSetupStatus: () => Promise<void>;
  markSetupCompleted: () => void;
}

const SetupStatusContext = createContext<SetupStatusContextType | undefined>(undefined);

export const useSetupStatus = () => {
  const context = useContext(SetupStatusContext);
  if (!context) {
    throw new Error('useSetupStatus must be used within SetupStatusProvider');
  }
  return context;
};

interface SetupStatusProviderProps {
  children: ReactNode;
}

export const SetupStatusProvider: React.FC<SetupStatusProviderProps> = ({ children }) => {
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const fetchSetupStatus = async () => {
    try {
      const response = await fetch('/api/management/setup-status');
      if (response.ok) {
        const data = await response.json();
        setSetupStatus({
          isCompleted: data.isCompleted === true,
          hasProcessedLogs: data.hasProcessedLogs === true,
          isSetupCompleted: data.isSetupCompleted || false
        });
      }
    } catch (error) {
      console.error('[SetupStatus] Failed to fetch setup status:', error);
    } finally {
      setIsLoading(false);
    }
  };

  const refreshSetupStatus = async () => {
    await fetchSetupStatus();
  };

  const markSetupCompleted = () => {
    setSetupStatus((prev) =>
      prev
        ? {
            ...prev,
            isCompleted: true,
            isSetupCompleted: true
          }
        : null
    );
  };

  // Initial fetch
  useEffect(() => {
    fetchSetupStatus();
  }, []);

  return (
    <SetupStatusContext.Provider
      value={{ setupStatus, isLoading, refreshSetupStatus, markSetupCompleted }}
    >
      {children}
    </SetupStatusContext.Provider>
  );
};
