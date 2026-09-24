import React, { useEffect, useState, useCallback, useRef, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getErrorMessage } from '@utils/error';
import { useSignalR } from './SignalRContext/useSignalR';
import { DockerSocketContext } from './DockerSocketContext.types';

interface DockerSocketProviderProps {
  children: ReactNode;
}

export const DockerSocketProvider: React.FC<DockerSocketProviderProps> = ({ children }) => {
  const [isDockerAvailable, setIsDockerAvailable] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { authMode, isLoading: checkingAuth } = useAuth();
  const { isConnected } = useSignalR();
  const hasAccess = authMode === 'authenticated' || authMode === 'guest';
  const probeRequestRef = useRef(0);

  const fetchDockerStatus = useCallback(async () => {
    if (checkingAuth) {
      return;
    }
    // The first probe, the reconnect probe and Retry can overlap; only the newest one writes.
    const request = ++probeRequestRef.current;
    if (!hasAccess) {
      setIsDockerAvailable(false);
      setError(null);
      setIsLoading(false);
      return;
    }
    setIsLoading(true);
    try {
      const permissions = await ApiService.getDirectoryPermissions();
      if (request !== probeRequestRef.current) return;
      setIsDockerAvailable(permissions.dockerSocket.available);
      setError(null);
    } catch (err) {
      console.error('[DockerSocket] Failed to check Docker socket status:', err);
      if (request !== probeRequestRef.current) return;
      // Keeps the last answer: a failed probe says nothing about Docker, and the Prefill page
      // shows this reason in its error box instead of hiding the tab.
      setError(getErrorMessage(err));
    } finally {
      if (request === probeRequestRef.current) setIsLoading(false);
    }
  }, [checkingAuth, hasAccess]);

  const refreshDockerStatus = useCallback(async () => {
    await fetchDockerStatus();
  }, [fetchDockerStatus]);

  // Initial fetch
  useEffect(() => {
    fetchDockerStatus();
  }, [fetchDockerStatus]);

  // Re-probe after a reconnect, so a probe that failed during an outage does not leave the Prefill
  // page on its error box.
  useReconnectRefetch(isConnected, () => void fetchDockerStatus());

  return (
    <DockerSocketContext.Provider
      value={{ isDockerAvailable, isLoading, error, refreshDockerStatus }}
    >
      {children}
    </DockerSocketContext.Provider>
  );
};
