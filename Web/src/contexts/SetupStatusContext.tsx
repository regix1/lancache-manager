import React, { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useAuth } from '@contexts/AuthContext';
import ApiService from '@services/api.service';
import { API_BASE } from '@utils/constants';

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
  const { isLoading: authLoading } = useAuth();

  const fetchSetupStatus = async () => {
    try {
      // This is a public endpoint - no auth required
      const response = await fetch(
        `${API_BASE}/system/setup`,
        ApiService.getFetchOptions({ cache: 'no-store' })
      );
      if (response.ok) {
        const data = await response.json();
        const isCompleted =
          data.isCompleted === true ||
          data.setupCompleted === true ||
          data.isSetupCompleted === true;
        setSetupStatus({
          isCompleted,
          hasProcessedLogs: data.hasProcessedLogs === true,
          isSetupCompleted: isCompleted
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

  // Initial fetch - setup status is public so we can check before auth
  useEffect(() => {
    // Wait for auth loading to settle, but don't require auth for setup check
    // The /api/system/setup endpoint is public so we can determine if setup wizard is needed
    if (authLoading) {
      return;
    }

    fetchSetupStatus();
  }, [authLoading]);

  return (
    <SetupStatusContext.Provider
      value={{ setupStatus, isLoading, refreshSetupStatus, markSetupCompleted }}
    >
      {children}
    </SetupStatusContext.Provider>
  );
};
