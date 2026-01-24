import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext';
import type { SteamAutoLogoutEvent } from '@contexts/SignalRContext/types';

export type SteamAuthMode = 'anonymous' | 'authenticated';

export interface SteamAuthenticationState {
  mode: SteamAuthMode;
  username?: string;
  isAuthenticated: boolean;
}

interface SteamAuthContextType {
  steamAuthMode: SteamAuthMode;
  username: string;
  isLoading: boolean;
  autoLogoutMessage: string | null;
  refreshSteamAuth: () => Promise<void>;
  setSteamAuthMode: (mode: SteamAuthMode) => void;
  setUsername: (username: string) => void;
  clearAutoLogoutMessage: () => void;
}

const SteamAuthContext = createContext<SteamAuthContextType | undefined>(undefined);

export const useSteamAuth = () => {
  const context = useContext(SteamAuthContext);
  if (!context) {
    throw new Error('useSteamAuth must be used within SteamAuthProvider');
  }
  return context;
};

interface SteamAuthProviderProps {
  children: ReactNode;
}

export const SteamAuthProvider: React.FC<SteamAuthProviderProps> = ({ children }) => {
  const signalR = useSignalR();
  const [steamAuthMode, setSteamAuthMode] = useState<SteamAuthMode>('anonymous');
  const [username, setUsername] = useState<string>('');
  const [isLoading, setIsLoading] = useState(true);
  const [autoLogoutMessage, setAutoLogoutMessage] = useState<string | null>(null);

  const fetchSteamAuth = async () => {
    try {
      const response = await fetch('/api/steam-auth/status', ApiService.getFetchOptions());
      if (response.ok) {
        const authState: SteamAuthenticationState = await response.json();
        setSteamAuthMode(authState.mode);
        setUsername(
          authState.mode === 'authenticated' && authState.username ? authState.username : ''
        );
      }
    } catch (error) {
      console.error('[SteamAuth] Failed to fetch Steam auth status:', error);
    } finally {
      setIsLoading(false);
    }
  };

  const refreshSteamAuth = async () => {
    await fetchSteamAuth();
  };

  const clearAutoLogoutMessage = useCallback(() => {
    setAutoLogoutMessage(null);
  }, []);

  // Listen for SteamAutoLogout SignalR events
  useEffect(() => {
    const handleSteamAutoLogout = async (event: SteamAutoLogoutEvent) => {
      // Update local state immediately to reflect logout
      setSteamAuthMode('anonymous');
      setUsername('');
      setAutoLogoutMessage(event.message);
      // Also refresh from server to ensure we're in sync
      await fetchSteamAuth();
    };

    signalR.on('SteamAutoLogout', handleSteamAutoLogout);

    return () => {
      signalR.off('SteamAutoLogout', handleSteamAutoLogout);
    };
  }, [signalR]);

  // Initial fetch
  useEffect(() => {
    fetchSteamAuth();
  }, []);

  return (
    <SteamAuthContext.Provider
      value={{
        steamAuthMode,
        username,
        isLoading,
        autoLogoutMessage,
        refreshSteamAuth,
        setSteamAuthMode,
        setUsername,
        clearAutoLogoutMessage
      }}
    >
      {children}
    </SteamAuthContext.Provider>
  );
};
