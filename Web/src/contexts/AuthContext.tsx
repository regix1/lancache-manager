import React, { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import authService, { type AuthMode } from '@services/auth.service';

interface AuthContextType {
  isAuthenticated: boolean;
  authMode: AuthMode;
  isLoading: boolean;
  refreshAuth: () => Promise<void>;
  setAuthMode: (mode: AuthMode) => void;
  setIsAuthenticated: (value: boolean) => void;
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
  const [isLoading, setIsLoading] = useState(true);

  const fetchAuth = async () => {
    try {
      const authResult = await authService.checkAuth();
      setIsAuthenticated(authResult.isAuthenticated);
      setAuthMode(authResult.authMode);
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
      setIsAuthenticated(false);
      setAuthMode('unauthenticated');
    } finally {
      setIsLoading(false);
    }
  };

  const refreshAuth = async () => {
    await fetchAuth();
  };

  // Initial fetch
  useEffect(() => {
    fetchAuth();
  }, []);

  // Listen for auth state changes from handleUnauthorized
  useEffect(() => {
    const handleAuthStateChanged = () => {
      console.log('[Auth] Auth state changed, refreshing...');
      refreshAuth();
    };

    window.addEventListener('auth-state-changed', handleAuthStateChanged);
    return () => window.removeEventListener('auth-state-changed', handleAuthStateChanged);
  }, []);

  // Poll for auth changes (device revocation, guest expiration)
  useEffect(() => {
    if (isLoading) return;

    let lastAuthState = authService.isAuthenticated;
    let lastAuthMode = authService.authMode;

    const interval = setInterval(async () => {
      const currentAuthState = authService.isAuthenticated;
      const currentAuthMode = authService.authMode;

      // Only update state if values actually changed
      if (currentAuthState !== lastAuthState) {
        setIsAuthenticated(currentAuthState);
        lastAuthState = currentAuthState;
      }
      if (currentAuthMode !== lastAuthMode) {
        setAuthMode(currentAuthMode);
        lastAuthMode = currentAuthMode;
      }

      // Re-check auth with backend to detect revoked devices
      if (currentAuthState && authService.authMode === 'authenticated') {
        try {
          const result = await authService.checkAuth();
          if (!result.isAuthenticated || result.authMode !== 'authenticated') {
            // Device was revoked! Update state to show authentication modal
            console.warn('[Auth] Device authentication was revoked.');
            setIsAuthenticated(false);
            setAuthMode('unauthenticated');
            lastAuthState = false;
            lastAuthMode = 'unauthenticated';
          }
        } catch (error) {
          console.error('[Auth] Failed to verify authentication:', error);
        }
      }

      // Re-check auth if in guest mode to get updated time
      if (authService.authMode === 'guest' || authService.authMode === 'expired') {
        const result = await authService.checkAuth();
        if (result.authMode !== lastAuthMode) {
          setAuthMode(result.authMode);
          lastAuthMode = result.authMode;
        }
      }
    }, 5000); // Check every 5 seconds

    return () => clearInterval(interval);
  }, [isLoading]);

  return (
    <AuthContext.Provider
      value={{
        isAuthenticated,
        authMode,
        isLoading,
        refreshAuth,
        setAuthMode,
        setIsAuthenticated
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
