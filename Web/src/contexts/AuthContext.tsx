import React, { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import authService, { type AuthMode } from '@services/auth.service';
import { useSignalR } from './SignalRContext';
import type { GuestPrefillPermissionChangedEvent, SteamUserBannedEvent, UserSessionRevokedEvent, GuestDurationUpdatedEvent } from './SignalRContext/types';

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
  // Ban status - hides prefill tab when banned
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
  const [isLoading, setIsLoading] = useState(true);
  const [prefillEnabled, setPrefillEnabled] = useState(false);
  const [prefillTimeRemaining, setPrefillTimeRemaining] = useState<number | null>(null);
  const [isBanned, setIsBanned] = useState(false);
  const signalR = useSignalR();

  const fetchAuth = useCallback(async () => {
    try {
      const authResult = await authService.checkAuth();
      setIsAuthenticated(authResult.isAuthenticated);
      setAuthMode(authResult.authMode);
      setPrefillEnabled(authResult.prefillEnabled ?? false);
      setPrefillTimeRemaining(authResult.prefillTimeRemaining ?? null);
      setIsBanned(authResult.isBanned ?? false);
      setIsLoading(false);
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
      setIsAuthenticated(false);
      setAuthMode('unauthenticated');
      setPrefillEnabled(false);
      setPrefillTimeRemaining(null);
      setIsBanned(false);
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
    const handlePrefillPermissionChanged = (event: GuestPrefillPermissionChangedEvent) => {
      // Check if this event is for the current device
      const currentDeviceId = authService.getDeviceId();
      if (event.deviceId === currentDeviceId) {
        console.log('[Auth] Prefill permission changed via SignalR:', event.enabled);
        setPrefillEnabled(event.enabled);
        if (event.expiresAt) {
          const expiresAt = new Date(event.expiresAt);
          const remaining = Math.max(0, Math.floor((expiresAt.getTime() - Date.now()) / 60000));
          setPrefillTimeRemaining(remaining);
        } else if (!event.enabled) {
          setPrefillTimeRemaining(null);
        }
      }
    };

    signalR.on('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);

    return () => {
      signalR.off('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);
    };
  }, [signalR]);

  // Listen for SignalR events that indicate this device was banned
  useEffect(() => {
    const handleSteamUserBanned = (event: SteamUserBannedEvent) => {
      // Check if this event is for the current device
      const currentDeviceId = authService.getDeviceId();
      if (event.deviceId === currentDeviceId) {
        console.log('[Auth] Device banned via SignalR:', event.username);
        setIsBanned(true);
        // Also disable prefill access
        setPrefillEnabled(false);
        setPrefillTimeRemaining(null);
      }
    };

    signalR.on('SteamUserBanned', handleSteamUserBanned);

    return () => {
      signalR.off('SteamUserBanned', handleSteamUserBanned);
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

  // Listen for session revocation via SignalR (replaces polling for device revocation)
  useEffect(() => {
    const handleSessionRevoked = (event: UserSessionRevokedEvent) => {
      const currentDeviceId = authService.getDeviceId();
      if (event.deviceId === currentDeviceId || event.sessionId === currentDeviceId) {
        console.warn('[Auth] Device session was revoked via SignalR:', event.reason);
        setIsAuthenticated(false);
        setAuthMode('unauthenticated');
        authService.clearAuth();
      }
    };

    signalR.on('UserSessionRevoked', handleSessionRevoked);

    return () => {
      signalR.off('UserSessionRevoked', handleSessionRevoked);
    };
  }, [signalR]);

  // Listen for guest duration updates via SignalR (replaces polling for guest expiration)
  useEffect(() => {
    const handleGuestDurationUpdated = (event: GuestDurationUpdatedEvent) => {
      if (authMode === 'guest') {
        console.log('[Auth] Guest duration updated via SignalR:', event.durationMinutes);
        setPrefillTimeRemaining(event.durationMinutes);
        if (event.durationMinutes <= 0) {
          setAuthMode('expired');
        }
      }
    };

    signalR.on('GuestDurationUpdated', handleGuestDurationUpdated);

    return () => {
      signalR.off('GuestDurationUpdated', handleGuestDurationUpdated);
    };
  }, [signalR, authMode]);

  // Join the AuthenticatedUsersGroup when SignalR is connected and user is authenticated
  // This handles the case where SignalR connected before authentication was validated
  useEffect(() => {
    if (signalR.isConnected && authMode === 'authenticated') {
      console.log('[Auth] Joining AuthenticatedUsersGroup via SignalR');
      signalR.invoke('JoinAuthenticatedGroup').catch((err) => {
        console.error('[Auth] Failed to join AuthenticatedUsersGroup:', err);
      });
    }
  }, [signalR.isConnected, signalR.invoke, authMode]);

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
        prefillTimeRemaining,
        isBanned
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
