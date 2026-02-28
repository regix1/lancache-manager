import React, {
  createContext,
  useContext,
  useEffect,
  useState,
  useCallback,
  type ReactNode
} from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import type { SteamAutoLogoutEvent, SteamSessionErrorEvent } from '@contexts/SignalRContext/types';

type SteamAuthMode = 'anonymous' | 'authenticated';

interface SteamAuthenticationState {
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
  const { authMode, isLoading: authLoading } = useAuth();
  const isAdmin = authMode === 'authenticated';
  const [steamAuthMode, setSteamAuthMode] = useState<SteamAuthMode>('anonymous');
  const [username, setUsername] = useState<string>('');
  const [isLoading, setIsLoading] = useState(true);
  const [autoLogoutMessage, setAutoLogoutMessage] = useState<string | null>(null);

  const fetchSteamAuth = useCallback(async () => {
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
  }, []);

  const refreshSteamAuth = async () => {
    await fetchSteamAuth();
  };

  const clearAutoLogoutMessage = useCallback(() => {
    setAutoLogoutMessage(null);
  }, []);

  // Listen for SteamAutoLogout SignalR events (admin-only)
  useEffect(() => {
    if (!isAdmin) return;

    const handleSteamAutoLogout = async (event: SteamAutoLogoutEvent) => {
      setSteamAuthMode('anonymous');
      setUsername('');
      setAutoLogoutMessage(event.message);
      await fetchSteamAuth();
    };

    signalR.on('SteamAutoLogout', handleSteamAutoLogout);

    return () => {
      signalR.off('SteamAutoLogout', handleSteamAutoLogout);
    };
  }, [signalR, isAdmin, fetchSteamAuth]);

  // Listen for SteamSessionError events (admin-only)
  useEffect(() => {
    if (!isAdmin) return;

    const handleSteamSessionError = async (event: SteamSessionErrorEvent) => {
      const authInvalidatingTypes = [
        'InvalidCredentials',
        'AuthenticationRequired',
        'SessionExpired',
        'AutoLogout'
      ];
      if (authInvalidatingTypes.includes(event.errorType)) {
        await fetchSteamAuth();
      }
    };

    signalR.on('SteamSessionError', handleSteamSessionError);

    return () => {
      signalR.off('SteamSessionError', handleSteamSessionError);
    };
  }, [signalR, isAdmin, fetchSteamAuth]);

  // Initial fetch - only for admin users (guests don't need Steam auth status)
  useEffect(() => {
    if (authLoading) return;
    if (!isAdmin) {
      setIsLoading(false);
      return;
    }
    fetchSteamAuth();
  }, [authLoading, isAdmin, fetchSteamAuth]);

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
