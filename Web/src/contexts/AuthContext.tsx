import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import authService, { type AuthMode } from '@services/auth.service';
import { useSignalR } from './SignalRContext';

interface AuthContextType {
  isAuthenticated: boolean;
  authMode: AuthMode;
  isLoading: boolean;
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
  const [, setIsAuthenticated] = useState(true);
  const [, setAuthMode] = useState<AuthMode>('authenticated');
  const [isLoading, setIsLoading] = useState(true);
  const signalR = useSignalR();

  const fetchAuth = useCallback(async () => {
    try {
      await authService.checkAuth();
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
    }
    setIsAuthenticated(true);
    setAuthMode('authenticated');
    setIsLoading(false);
  }, []);

  const refreshAuth = useCallback(async () => {
    await fetchAuth();
  }, [fetchAuth]);

  // Initial fetch
  useEffect(() => {
    fetchAuth();
  }, [fetchAuth]);

  // Join the AuthenticatedUsersGroup when SignalR is connected
  useEffect(() => {
    if (signalR.isConnected) {
      signalR.invoke('JoinAuthenticatedGroup').catch((err: unknown) => {
        console.error('[Auth] Failed to join AuthenticatedUsersGroup:', err);
      });
    }
  }, [signalR.isConnected, signalR.invoke]);

  return (
    <AuthContext.Provider
      value={{
        isAuthenticated: true,
        authMode: 'authenticated',
        isLoading,
        refreshAuth,
        setAuthMode,
        setIsAuthenticated,
        prefillEnabled: true,
        prefillTimeRemaining: null,
        isBanned: false,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
