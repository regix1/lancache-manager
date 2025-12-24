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
      setIsLoading(false);
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
      setIsAuthenticated(false);
      setAuthMode('unauthenticated');
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

  // Listen for auth state changes from handleUnauthorized and other events
  useEffect(() => {
    const handleAuthStateChanged = () => {
      console.log('[Auth] Auth state changed, refreshing...');
      refreshAuth();
    };

    // Listen for localStorage changes (cross-tab sync)
    const handleStorageChange = (e: StorageEvent) => {
      if (e.key === 'auth-token' || e.key === 'auth-mode') {
        console.log('[Auth] Storage changed in another tab, refreshing...');
        refreshAuth();
      }
    };

    window.addEventListener('auth-state-changed', handleAuthStateChanged);
    window.addEventListener('storage', handleStorageChange);

    return () => {
      window.removeEventListener('auth-state-changed', handleAuthStateChanged);
      window.removeEventListener('storage', handleStorageChange);
    };
  }, []);

  // Reduced polling for specific scenarios (device revocation, guest expiration)
  // Only polls when user is authenticated or in guest mode
  useEffect(() => {
    if (isLoading) return;

    // Only set up polling if we're authenticated or in guest/expired mode
    const needsPolling = authService.authMode === 'authenticated' ||
                         authService.authMode === 'guest' ||
                         authService.authMode === 'expired';

    if (!needsPolling) {
      return; // No polling needed for unauthenticated users
    }

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
    }, 30000); // Reduced to 30 seconds (was 5 seconds)

    return () => clearInterval(interval);
  }, [isLoading, authMode]);

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
