import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/AuthContext';

interface DockerSocketContextType {
  isDockerAvailable: boolean;
  isLoading: boolean;
  refreshDockerStatus: () => Promise<void>;
}

const DockerSocketContext = createContext<DockerSocketContextType | undefined>(undefined);

export const useDockerSocket = () => {
  const context = useContext(DockerSocketContext);
  if (!context) {
    throw new Error('useDockerSocket must be used within DockerSocketProvider');
  }
  return context;
};

interface DockerSocketProviderProps {
  children: ReactNode;
}

export const DockerSocketProvider: React.FC<DockerSocketProviderProps> = ({ children }) => {
  const [isDockerAvailable, setIsDockerAvailable] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const { authMode, isLoading: checkingAuth } = useAuth();
  const hasAccess = authMode === 'authenticated' || authMode === 'guest';

  const fetchDockerStatus = useCallback(async () => {
    try {
      if (checkingAuth) {
        return;
      }
      if (!hasAccess) {
        setIsDockerAvailable(false);
        setIsLoading(false);
        return;
      }
      setIsLoading(true);
      const permissions = await ApiService.getDirectoryPermissions();
      setIsDockerAvailable(permissions.dockerSocket.available);
    } catch (error) {
      console.error('[DockerSocket] Failed to check Docker socket status:', error);
      setIsDockerAvailable(false);
    } finally {
      if (!checkingAuth) {
        setIsLoading(false);
      }
    }
  }, [checkingAuth, hasAccess]);

  const refreshDockerStatus = useCallback(async () => {
    await fetchDockerStatus();
  }, [fetchDockerStatus]);

  // Initial fetch
  useEffect(() => {
    fetchDockerStatus();
  }, [fetchDockerStatus]);

  return (
    <DockerSocketContext.Provider value={{ isDockerAvailable, isLoading, refreshDockerStatus }}>
      {children}
    </DockerSocketContext.Provider>
  );
};
