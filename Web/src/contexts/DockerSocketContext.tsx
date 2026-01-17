import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import ApiService from '@services/api.service';

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

  const fetchDockerStatus = useCallback(async () => {
    try {
      const permissions = await ApiService.getDirectoryPermissions();
      setIsDockerAvailable(permissions.dockerSocket.available);
      setIsLoading(false);
    } catch (error) {
      console.error('[DockerSocket] Failed to check Docker socket status:', error);
      setIsDockerAvailable(false);
      setIsLoading(false);
    }
  }, []);

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
