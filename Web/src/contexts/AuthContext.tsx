import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import authService, { type AuthMode } from '@services/auth.service';
import type { SessionType } from '@services/auth.service';
import { useSignalR } from './SignalRContext';

interface AuthContextType {
  isAuthenticated: boolean;
  authMode: AuthMode;
  sessionType: SessionType | null;
  isLoading: boolean;
  login: (apiKey: string) => Promise<{ success: boolean; message?: string }>;
  startGuestSession: () => Promise<{ success: boolean; message?: string }>;
  logout: () => Promise<void>;
  refreshAuth: () => Promise<void>;
  setAuthMode: (mode: AuthMode) => void;
  setIsAuthenticated: (value: boolean) => void;
  prefillEnabled: boolean;
  prefillTimeRemaining: number | null;
  isBanned: boolean;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider');
  }
  return context;
};

interface AuthProviderProps {
  children: ReactNode;
}

export const AuthProvider: React.FC<AuthProviderProps> = ({ children }) => {
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [sessionType, setSessionType] = useState<SessionType | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const signalR = useSignalR();

  const fetchAuth = useCallback(async () => {
    try {
      const data = await authService.checkAuth();
      setIsAuthenticated(data.isAuthenticated);
      setSessionType(data.sessionType);

      if (data.isAuthenticated && data.sessionType === 'admin') {
        setAuthMode('authenticated');
      } else if (data.isAuthenticated && data.sessionType === 'guest') {
        setAuthMode('guest');
      } else {
        setAuthMode('unauthenticated');
      }
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
      setIsAuthenticated(false);
      setAuthMode('unauthenticated');
      setSessionType(null);
    }
    setIsLoading(false);
  }, []);

  const refreshAuth = useCallback(async () => {
    await fetchAuth();
  }, [fetchAuth]);

  const login = useCallback(async (apiKey: string) => {
    const result = await authService.login(apiKey);
    if (result.success) {
      await fetchAuth();
    }
    return result;
  }, [fetchAuth]);

  const startGuestSession = useCallback(async () => {
    const result = await authService.startGuestSession();
    if (result.success) {
      await fetchAuth();
    }
    return result;
  }, [fetchAuth]);

  const logout = useCallback(async () => {
    await authService.logout();
    setIsAuthenticated(false);
    setAuthMode('unauthenticated');
    setSessionType(null);
  }, []);

  // Initial fetch
  useEffect(() => {
    fetchAuth();
  }, [fetchAuth]);

  // Listen for 401 events from API calls to trigger re-auth
  useEffect(() => {
    const handleAuthStateChanged = () => {
      fetchAuth();
    };

    window.addEventListener('auth-state-changed', handleAuthStateChanged);
    return () => window.removeEventListener('auth-state-changed', handleAuthStateChanged);
  }, [fetchAuth]);

  // Listen for session revocation via SignalR
  useEffect(() => {
    const handleSessionRevoked = () => {
      setIsAuthenticated(false);
      setAuthMode('unauthenticated');
      setSessionType(null);
    };

    const handleSessionsCleared = () => {
      setIsAuthenticated(false);
      setAuthMode('unauthenticated');
      setSessionType(null);
    };

    signalR.on('UserSessionRevoked', handleSessionRevoked);
    signalR.on('UserSessionsCleared', handleSessionsCleared);

    return () => {
      signalR.off('UserSessionRevoked', handleSessionRevoked);
      signalR.off('UserSessionsCleared', handleSessionsCleared);
    };
  }, [signalR]);

  // Join the AuthenticatedUsersGroup when SignalR is connected and authenticated
  useEffect(() => {
    if (signalR.isConnected && isAuthenticated) {
      signalR.invoke('JoinAuthenticatedGroup').catch((err: unknown) => {
        console.error('[Auth] Failed to join AuthenticatedUsersGroup:', err);
      });
    }
  }, [signalR.isConnected, signalR.invoke, isAuthenticated]);

  return (
    <AuthContext.Provider
      value={{
        isAuthenticated,
        authMode,
        sessionType,
        isLoading,
        login,
        startGuestSession,
        logout,
        refreshAuth,
        setAuthMode,
        setIsAuthenticated,
        prefillEnabled: sessionType === 'admin',
        prefillTimeRemaining: null,
        isBanned: false,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
