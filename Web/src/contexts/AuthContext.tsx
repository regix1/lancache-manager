import React, { useEffect, useState, useCallback, useRef, type ReactNode } from 'react';
import authService, { type AuthMode, type SessionType } from '@services/auth.service';
import { useSignalR } from './SignalRContext/useSignalR';
import type { ShowToastEvent } from './SignalRContext/types';
import { isAbortError } from '@utils/error';
import { AuthContext } from './AuthContext.types';
import { APP_EVENTS } from '@utils/constants';

interface AuthProviderProps {
  children: ReactNode;
}

export const AuthProvider: React.FC<AuthProviderProps> = ({ children }) => {
  const [authMode, setAuthMode] = useState<AuthMode>('unauthenticated');
  const [sessionType, setSessionType] = useState<SessionType | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [sessionExpiresAt, setSessionExpiresAt] = useState<string | null>(null);
  const [authenticationEnabled, setAuthenticationEnabled] = useState(true);
  const [steamPrefillEnabled, setSteamPrefillEnabled] = useState(false);
  const [steamPrefillExpiresAt, setSteamPrefillExpiresAt] = useState<string | null>(null);
  const [epicPrefillEnabled, setEpicPrefillEnabled] = useState(false);
  const [epicPrefillExpiresAt, setEpicPrefillExpiresAt] = useState<string | null>(null);
  const [battlenetPrefillEnabled, setBattlenetPrefillEnabled] = useState(false);
  const [battlenetPrefillExpiresAt, setBattlenetPrefillExpiresAt] = useState<string | null>(null);
  const [riotPrefillEnabled, setRiotPrefillEnabled] = useState(false);
  const [riotPrefillExpiresAt, setRiotPrefillExpiresAt] = useState<string | null>(null);
  const [xboxPrefillEnabled, setXboxPrefillEnabled] = useState(false);
  const [xboxPrefillExpiresAt, setXboxPrefillExpiresAt] = useState<string | null>(null);
  const signalR = useSignalR();

  // Derive isAdmin and hasSession from authMode
  const isAdmin = authMode === 'authenticated';
  const hasSession = authMode !== 'unauthenticated';

  // Refs to avoid stale closures in SignalR handlers.
  // Handlers must read current values without being recreated on every state change.
  const sessionIdRef = useRef<string | null>(sessionId);
  const sessionTypeRef = useRef<SessionType | null>(sessionType);
  const isFetchingAuthRef = useRef(false);
  useEffect(() => {
    sessionIdRef.current = sessionId;
  }, [sessionId]);
  useEffect(() => {
    sessionTypeRef.current = sessionType;
  }, [sessionType]);
  const notifyAuthSessionUpdated = useCallback(() => {
    window.dispatchEvent(new Event(APP_EVENTS.AUTH_SESSION_UPDATED));
  }, []);

  const fetchAuth = useCallback(async () => {
    // Timeout is handled by AbortController in auth.service.ts (10s)
    try {
      const data = await authService.checkAuth();

      if (data.authenticationEnabled === false) {
        console.warn(
          '[Auth] Authentication DISABLED via Security:EnableAuthentication — bypassing login + setup wizard, all access granted'
        );
      }

      setAuthenticationEnabled(data.authenticationEnabled);
      setSessionType(data.sessionType);
      setSessionId(data.sessionId);
      setSessionExpiresAt(data.expiresAt);
      setSteamPrefillEnabled(data.steamPrefillEnabled ?? data.prefillEnabled);
      setSteamPrefillExpiresAt(data.steamPrefillExpiresAt ?? data.prefillExpiresAt);
      setEpicPrefillEnabled(data.epicPrefillEnabled);
      setEpicPrefillExpiresAt(data.epicPrefillExpiresAt ?? null);
      setBattlenetPrefillEnabled(data.battlenetPrefillEnabled);
      setBattlenetPrefillExpiresAt(data.battlenetPrefillExpiresAt ?? null);
      setRiotPrefillEnabled(data.riotPrefillEnabled);
      setRiotPrefillExpiresAt(data.riotPrefillExpiresAt ?? null);
      setXboxPrefillEnabled(data.xboxPrefillEnabled);
      setXboxPrefillExpiresAt(data.xboxPrefillExpiresAt ?? null);

      if (data.isAuthenticated && data.sessionType === 'admin') {
        setAuthMode('authenticated');
      } else if (data.isAuthenticated && data.sessionType === 'guest') {
        setAuthMode('guest');
      } else {
        setAuthMode('unauthenticated');
      }
    } catch (error) {
      console.error('[Auth] Failed to check auth status:', error);
      // AuthProvider is an ancestor of NotificationsProvider in AppProviders.tsx, so
      // useErrorHandler (useNotifications) is not reachable here - it would throw. Use the
      // existing show-toast bridge instead (mirrors NotificationsContext.tsx:332-356). A failed
      // session check silently defaults to unauthenticated below, so surface it - otherwise the
      // user has no idea why they were logged out. Cancellation (request timeout abort) is not
      // a failure worth surfacing.
      if (!isAbortError(error)) {
        window.dispatchEvent(
          new CustomEvent<ShowToastEvent>(APP_EVENTS.SHOW_TOAST, {
            detail: {
              type: 'error',
              message: 'Failed to verify your session. Please refresh the page.',
              duration: 5000
            }
          })
        );
      }
      setAuthenticationEnabled(true);
      setAuthMode('unauthenticated');
      setSessionType(null);
      setSessionId(null);
      setSessionExpiresAt(null);
      setSteamPrefillEnabled(false);
      setSteamPrefillExpiresAt(null);
      setEpicPrefillEnabled(false);
      setEpicPrefillExpiresAt(null);
      setBattlenetPrefillEnabled(false);
      setBattlenetPrefillExpiresAt(null);
      setRiotPrefillEnabled(false);
      setRiotPrefillExpiresAt(null);
      setXboxPrefillEnabled(false);
      setXboxPrefillExpiresAt(null);
    } finally {
      setIsLoading(false);
      notifyAuthSessionUpdated();
    }
  }, [notifyAuthSessionUpdated]);

  const refreshAuth = useCallback(async () => {
    await fetchAuth();
  }, [fetchAuth]);

  const login = useCallback(
    async (apiKey: string) => {
      const result = await authService.login(apiKey);
      if (result.success) {
        await fetchAuth();
      }
      return result;
    },
    [fetchAuth]
  );

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
    setSteamPrefillEnabled(false);
    setSteamPrefillExpiresAt(null);
    setEpicPrefillEnabled(false);
    setEpicPrefillExpiresAt(null);
    setBattlenetPrefillEnabled(false);
    setBattlenetPrefillExpiresAt(null);
    setRiotPrefillEnabled(false);
    setRiotPrefillExpiresAt(null);
    setXboxPrefillEnabled(false);
    setXboxPrefillExpiresAt(null);
    notifyAuthSessionUpdated();
  }, [notifyAuthSessionUpdated]);

  // Initial fetch
  useEffect(() => {
    fetchAuth();
  }, [fetchAuth]);

  // Listen for 401 events from API calls to trigger re-auth.
  // Deduplicate concurrent 401s to avoid multiple fetchAuth calls in quick succession.
  useEffect(() => {
    const handleAuthStateChanged = () => {
      if (isFetchingAuthRef.current) {
        return;
      }
      isFetchingAuthRef.current = true;
      fetchAuth().finally(() => {
        isFetchingAuthRef.current = false;
      });
    };

    window.addEventListener(APP_EVENTS.AUTH_STATE_CHANGED, handleAuthStateChanged);
    return () => window.removeEventListener(APP_EVENTS.AUTH_STATE_CHANGED, handleAuthStateChanged);
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
      setSteamPrefillEnabled(false);
      setSteamPrefillExpiresAt(null);
      setEpicPrefillEnabled(false);
      setEpicPrefillExpiresAt(null);
      setBattlenetPrefillEnabled(false);
      setBattlenetPrefillExpiresAt(null);
      setRiotPrefillEnabled(false);
      setRiotPrefillExpiresAt(null);
      setXboxPrefillEnabled(false);
      setXboxPrefillExpiresAt(null);
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

    const handlePrefillPermissionChanged = (data: {
      sessionId?: string;
      enabled?: boolean;
      prefillExpiresAt?: string;
      service?: string;
    }) => {
      if (data.sessionId && data.sessionId === sessionIdRef.current) {
        const isEnabled = data.enabled ?? false;
        const expiresAt = data.prefillExpiresAt ?? null;
        if (data.service === 'epic') {
          setEpicPrefillEnabled(isEnabled);
          setEpicPrefillExpiresAt(expiresAt);
        } else if (data.service === 'battlenet') {
          setBattlenetPrefillEnabled(isEnabled);
          setBattlenetPrefillExpiresAt(expiresAt);
        } else if (data.service === 'riot') {
          setRiotPrefillEnabled(isEnabled);
          setRiotPrefillExpiresAt(expiresAt);
        } else if (data.service === 'xbox') {
          setXboxPrefillEnabled(isEnabled);
          setXboxPrefillExpiresAt(expiresAt);
        } else {
          // 'steam' or legacy (no service field) - default to steam
          setSteamPrefillEnabled(isEnabled);
          setSteamPrefillExpiresAt(expiresAt);
        }
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

  // Join the AuthenticatedUsersGroup when SignalR is connected and has a session (admin or guest).
  // Also refresh auth state on reconnect to pick up any changes missed while disconnected
  // (e.g. prefill permission grants that arrived via SignalR while the connection was down).
  useEffect(() => {
    if (signalR.isConnected && hasSession) {
      // Best-effort group join for live session-revocation pushes - HTTP-based auth/session
      // state (fetchAuth) still works if this fails. Deliberately silent; not user-actionable.
      signalR.invoke('JoinAuthenticatedGroupAsync').catch((err: unknown) => {
        console.error('[Auth] Failed to join AuthenticatedUsersGroup:', err);
      });
      fetchAuth();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.isConnected, signalR.invoke, hasSession]);

  // Derive combined prefillEnabled as OR of all services (for backward compat - nav tab visibility)
  const prefillEnabled =
    steamPrefillEnabled ||
    epicPrefillEnabled ||
    battlenetPrefillEnabled ||
    riotPrefillEnabled ||
    xboxPrefillEnabled;

  // Calculate time remaining - use the earliest expiring active service
  const calcTimeRemaining = (expiresAt: string | null): number | null => {
    if (!expiresAt) return null;
    const normalized =
      expiresAt.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(expiresAt) ? expiresAt : expiresAt + 'Z';
    return Math.max(0, Math.floor((new Date(normalized).getTime() - Date.now()) / 1000 / 60));
  };

  const steamTimeRemaining = steamPrefillEnabled ? calcTimeRemaining(steamPrefillExpiresAt) : null;
  const epicTimeRemaining = epicPrefillEnabled ? calcTimeRemaining(epicPrefillExpiresAt) : null;
  const battlenetTimeRemaining = battlenetPrefillEnabled
    ? calcTimeRemaining(battlenetPrefillExpiresAt)
    : null;
  const riotTimeRemaining = riotPrefillEnabled ? calcTimeRemaining(riotPrefillExpiresAt) : null;
  const xboxTimeRemaining = xboxPrefillEnabled ? calcTimeRemaining(xboxPrefillExpiresAt) : null;

  // prefillTimeRemaining: minimum non-null remaining time across active services
  const prefillTimeRemaining = (() => {
    const values = [
      steamTimeRemaining,
      epicTimeRemaining,
      battlenetTimeRemaining,
      riotTimeRemaining,
      xboxTimeRemaining
    ].filter((v): v is number => v !== null);
    return values.length > 0 ? Math.min(...values) : null;
  })();

  return (
    <AuthContext.Provider
      value={{
        isAdmin,
        hasSession,
        authMode,
        sessionType,
        sessionId,
        sessionExpiresAt,
        authenticationEnabled,
        isLoading,
        login,
        startGuestSession,
        logout,
        refreshAuth,
        setAuthMode,
        prefillEnabled,
        prefillTimeRemaining,
        steamPrefillEnabled,
        epicPrefillEnabled,
        battlenetPrefillEnabled,
        riotPrefillEnabled,
        xboxPrefillEnabled,
        isBanned: false
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};
