import React, { createContext, useContext, useEffect, useState, useCallback, useRef, type ReactNode } from 'react';
import authService, { type AuthMode } from '@services/auth.service';
import type { SessionType } from '@services/auth.service';
import { useSignalR } from './SignalRContext';

interface AuthContextType {
  isAdmin: boolean;
  hasSession: boolean;
  authMode: AuthMode;
  sessionType: SessionType | null;
  sessionId: string | null;
  sessionExpiresAt: string | null;
  isLoading: boolean;
  login: (apiKey: string) => Promise<{ success: boolean; message?: string }>;
  startGuestSession: () => Promise<{ success: boolean; message?: string }>;
  logout: () => Promise<void>;
  refreshAuth: () => Promise<void>;
  setAuthMode: (mode: AuthMode) => void;
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
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [sessionType, setSessionType] = useState<SessionType | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [sessionExpiresAt, setSessionExpiresAt] = useState<string | null>(null);
  const [prefillEnabled, setPrefillEnabled] = useState(false);
  const [prefillExpiresAt, setPrefillExpiresAt] = useState<string | null>(null);
  const signalR = useSignalR();

  // Derive isAdmin and hasSession from authMode
  const isAdmin = authMode === 'authenticated';
  const hasSession = authMode !== 'unauthenticated';

  // Refs to avoid stale closures in SignalR handlers.
  // Handlers must read current values without being recreated on every state change.
  const sessionIdRef = useRef<string | null>(sessionId);
  const sessionTypeRef = useRef<SessionType | null>(sessionType);
  useEffect(() => { sessionIdRef.current = sessionId; }, [sessionId]);
  useEffect(() => { sessionTypeRef.current = sessionType; }, [sessionType]);

  const fetchAuth = useCallback(async () => {
    try {
      const data = await authService.checkAuth();
      setSessionType(data.sessionType);
      setSessionId(data.sessionId);
      setSessionExpiresAt(data.expiresAt);
      setPrefillEnabled(data.prefillEnabled);
      setPrefillExpiresAt(data.prefillExpiresAt);

      if (data.isAuthenticated && data.sessionType === 'admin') {
        setAuthMode('authenticated');
      } else if (data.isAuthenticated && data.sessionType === 'guest') {
        setAuthMode('guest');
      } else {
        setAuthMode('unauthenticated');
      }
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
      setAuthMode('unauthenticated');
      setSessionType(null);
      setSessionId(null);
      setSessionExpiresAt(null);
      setPrefillEnabled(false);
      setPrefillExpiresAt(null);
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
    setAuthMode('unauthenticated');
    setSessionType(null);
    setSessionId(null);
    setSessionExpiresAt(null);
    setPrefillEnabled(false);
    setPrefillExpiresAt(null);
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

  // Listen for session revocation/deletion via SignalR.
  // All handlers read sessionId/sessionType from refs to avoid stale closures.
  // This keeps the dependency array minimal so handlers are registered once and
  // never miss events during re-registration windows.
  useEffect(() => {
    const clearAuthState = () => {
      setAuthMode('unauthenticated');
      setSessionType(null);
      setSessionId(null);
      setSessionExpiresAt(null);
      setPrefillEnabled(false);
      setPrefillExpiresAt(null);
    };

    const handleSessionRevoked = (data: { sessionId: string; sessionType: string }) => {
      if (data.sessionId === sessionIdRef.current) {
        clearAuthState();
      }
    };

    const handleSessionDeleted = (data: { sessionId: string; sessionType: string }) => {
      if (data.sessionId === sessionIdRef.current) {
        clearAuthState();
      }
    };

    const handleSessionsCleared = () => {
      if (sessionTypeRef.current === 'guest') {
        clearAuthState();
      }
    };

    const handlePrefillPermissionChanged = (data: { sessionId?: string; enabled?: boolean; prefillExpiresAt?: string }) => {
      if (data.sessionId && data.sessionId === sessionIdRef.current) {
        setPrefillEnabled(data.enabled ?? false);
        setPrefillExpiresAt(data.prefillExpiresAt ?? null);
      }
    };

    signalR.on('UserSessionRevoked', handleSessionRevoked);
    signalR.on('UserSessionDeleted', handleSessionDeleted);
    signalR.on('UserSessionsCleared', handleSessionsCleared);
    signalR.on('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);

    return () => {
      signalR.off('UserSessionRevoked', handleSessionRevoked);
      signalR.off('UserSessionDeleted', handleSessionDeleted);
      signalR.off('UserSessionsCleared', handleSessionsCleared);
      signalR.off('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);
    };
  }, [signalR]);

  // Join the AuthenticatedUsersGroup when SignalR is connected and has a session (admin or guest)
  useEffect(() => {
    if (signalR.isConnected && hasSession) {
      signalR.invoke('JoinAuthenticatedGroup').catch((err: unknown) => {
        console.error('[Auth] Failed to join AuthenticatedUsersGroup:', err);
      });
    }
  }, [signalR.isConnected, signalR.invoke, hasSession]);

  // Calculate time remaining for prefill access
  // Ensure UTC interpretation for timestamps without timezone suffix
  const prefillTimeRemaining = prefillExpiresAt
    ? Math.max(0, Math.floor((new Date(
        prefillExpiresAt.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(prefillExpiresAt)
          ? prefillExpiresAt
          : prefillExpiresAt + 'Z'
      ).getTime() - Date.now()) / 1000 / 60))
    : null;

  return (
    <AuthContext.Provider
      value={{
        isAdmin,
        hasSession,
        authMode,
        sessionType,
        sessionId,
        sessionExpiresAt,
        isLoading,
        login,
        startGuestSession,
        logout,
        refreshAuth,
        setAuthMode,
        prefillEnabled,
        prefillTimeRemaining,
        isBanned: false,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
