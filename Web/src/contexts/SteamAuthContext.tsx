import React, { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import ApiService from '@services/api.service';

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
  refreshSteamAuth: () => Promise<void>;
  setSteamAuthMode: (mode: SteamAuthMode) => void;
  setUsername: (username: string) => void;
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
  const [steamAuthMode, setSteamAuthMode] = useState<SteamAuthMode>('anonymous');
  const [username, setUsername] = useState<string>('');
  const [isLoading, setIsLoading] = useState(true);

  const fetchSteamAuth = async () => {
    try {
      const response = await fetch('/api/management/steam-auth-status', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const authState: SteamAuthenticationState = await response.json();
        setSteamAuthMode(authState.mode);
        setUsername(authState.mode === 'authenticated' && authState.username ? authState.username : '');
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
        refreshSteamAuth,
        setSteamAuthMode,
        setUsername
      }}
    >
      {children}
    </SteamAuthContext.Provider>
  );
};
