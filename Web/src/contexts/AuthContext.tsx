import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import authService, { type AuthMode } from '@services/auth.service';
import { useSignalR } from './SignalRContext';
import type { GuestPrefillPermissionChangedPayload } from './SignalRContext/types';

interface AuthContextType {
  isAuthenticated: boolean;
  authMode: AuthMode;
  isLoading: boolean;
  refreshAuth: () => Promise<void>;
  setAuthMode: (mode: AuthMode) => void;
  setIsAuthenticated: (value: boolean) => void;
  // Prefill permission for guests
  prefillEnabled: boolean;
  prefillTimeRemaining: number | null;
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
  const [prefillEnabled, setPrefillEnabled] = useState(false);
  const [prefillTimeRemaining, setPrefillTimeRemaining] = useState<number | null>(null);
  const signalR = useSignalR();

  const fetchAuth = useCallback(async () => {
    try {
      const authResult = await authService.checkAuth();
      setIsAuthenticated(authResult.isAuthenticated);
      setAuthMode(authResult.authMode);
      setPrefillEnabled(authResult.prefillEnabled ?? false);
      setPrefillTimeRemaining(authResult.prefillTimeRemaining ?? null);
      setIsLoading(false);
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
      setIsAuthenticated(false);
      setAuthMode('unauthenticated');
      setPrefillEnabled(false);
      setPrefillTimeRemaining(null);
      setIsLoading(false);
    }
  }, []);

  const refreshAuth = useCallback(async () => {
    await fetchAuth();
  }, [fetchAuth]);

  // Initial fetch
  useEffect(() => {
    fetchAuth();
  }, [fetchAuth]);

  // Listen for SignalR events that affect prefill permission
  useEffect(() => {
    const handlePrefillPermissionChanged = (payload: GuestPrefillPermissionChangedPayload) => {
      // Check if this event is for the current device
      const currentDeviceId = authService.getDeviceId();
      if (payload.deviceId === currentDeviceId) {
        console.log('[Auth] Prefill permission changed via SignalR:', payload.enabled);
        setPrefillEnabled(payload.enabled);
        if (payload.expiresAt) {
          const expiresAt = new Date(payload.expiresAt);
          const remaining = Math.max(0, Math.floor((expiresAt.getTime() - Date.now()) / 60000));
          setPrefillTimeRemaining(remaining);
        } else if (!payload.enabled) {
          setPrefillTimeRemaining(null);
        }
      }
    };

    signalR.on('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);

    return () => {
      signalR.off('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);
    };
  }, [signalR]);

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
        setIsAuthenticated,
        prefillEnabled,
        prefillTimeRemaining
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
