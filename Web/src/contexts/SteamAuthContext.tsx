import React, { useEffect, useState, useCallback, useRef, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useAuth } from '@contexts/useAuth';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getErrorMessage } from '@utils/error';
import type { SteamAutoLogoutEvent, SteamSessionErrorEvent } from '@contexts/SignalRContext/types';
import { SteamAuthContext, type SteamAuthMode } from './SteamAuthContext.types';
import type { IntegrationAccess } from '../types';

interface SteamAuthenticationState extends IntegrationAccess {
  mode: SteamAuthMode;
  username?: string;
  isAuthenticated: boolean;
}

interface SteamAuthProviderProps {
  children: ReactNode;
}

export const SteamAuthProvider: React.FC<SteamAuthProviderProps> = ({ children }) => {
  const signalR = useSignalR();
  const {
    authMode,
    authenticationEnabled,
    accountId,
    sessionId,
    isLoading: authLoading
  } = useAuth();
  const isAdmin =
    authenticationEnabled === false ||
    (authMode === 'authenticated' && Boolean(accountId && sessionId));
  const identity = JSON.stringify([authenticationEnabled, accountId, sessionId, authMode]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const requestRef = useRef(0);
  const [statusIdentity, setStatusIdentity] = useState<string | null>(null);
  const [access, setAccess] = useState<IntegrationAccess | null>(null);
  const [steamAuthMode, setSteamAuthMode] = useState<SteamAuthMode>('anonymous');
  const [username, setUsername] = useState<string>('');
  const [isLoading, setIsLoading] = useState(true);
  const [revision, setRevision] = useState(0);
  const [autoLogoutMessage, setAutoLogoutMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const fetchSteamAuth = useCallback(async () => {
    if (authLoading || !isAdmin || identityRef.current !== identity) return;
    const request = ++requestRef.current;
    const current = () => identityRef.current === identity && requestRef.current === request;
    try {
      const response = await fetch('/api/steam-auth/status', ApiService.getFetchOptions());
      const authState = await ApiService.handleResponse<SteamAuthenticationState>(response);
      if (!current()) return;
      ApiService.assertIntegrationAccess(authState, 'login', response.status);
      setError(null);
      setStatusIdentity(identity);
      if (authState) {
        setAccess(authState);
        setStatusIdentity(identity);
        const authenticated =
          authState.canManage === true &&
          authState.mode === 'authenticated' &&
          authState.isAuthenticated === true &&
          Boolean(authState.username?.trim());
        setSteamAuthMode(authenticated ? 'authenticated' : 'anonymous');
        setUsername(authenticated ? authState.username! : '');
      } else {
        setAccess(null);
        setSteamAuthMode('anonymous');
        setUsername('');
      }
    } catch (err) {
      if (!current()) return;
      setStatusIdentity(identity);
      setAccess(null);
      setSteamAuthMode('anonymous');
      setUsername('');
      setError(getErrorMessage(err));
      // Background poll (mount + SteamAutoLogout/SteamSessionError SignalR recovery). Steam
      // auth state still resets to 'anonymous', because that value gates Steam-prefill UI and an
      // unread status must not leave a signed-in account usable; the Steam card shows this
      // reason in its error box.
      console.error('[SteamAuth] Failed to fetch Steam auth status:', err);
    } finally {
      if (current()) {
        setIsLoading(false);
        setRevision((current) => current + 1);
      }
    }
  }, [identity, isAdmin, authLoading]);

  const refreshSteamAuth = async () => {
    await fetchSteamAuth();
  };

  const clearAutoLogoutMessage = useCallback(() => {
    setAutoLogoutMessage(null);
  }, []);

  // Listen for SteamAutoLogout SignalR events (admin-only)
  useEffect(() => {
    if (!isAdmin) return;

    const handleSteamAutoLogout = async (_event: SteamAutoLogoutEvent) => {
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
    requestRef.current += 1;
    setAccess(null);
    setStatusIdentity(null);
    setSteamAuthMode('anonymous');
    setUsername('');
    setAutoLogoutMessage(null);
    if (authLoading) return;
    if (!isAdmin) {
      setIsLoading(false);
      return;
    }
    setIsLoading(true);
    fetchSteamAuth();
    return () => {
      requestRef.current += 1;
    };
  }, [authLoading, isAdmin, fetchSteamAuth]);

  // SteamAutoLogout and SteamSessionError are the only things that move this state, and neither is
  // delivered while the socket is down, so a session dropped server-side would keep offering Steam
  // prefill until the page reloaded. Admin-only, matching the two handlers and the initial fetch:
  // /steam-auth/status is not for guests, and this provider is mounted for every session.
  useReconnectRefetch(signalR.isConnected, () => {
    if (isAdmin) {
      void fetchSteamAuth();
    }
  });

  return (
    <SteamAuthContext.Provider
      value={{
        steamAuthMode:
          statusIdentity === identity && isAdmin && !authLoading ? steamAuthMode : 'anonymous',
        username: statusIdentity === identity && isAdmin && !authLoading ? username : '',
        access: statusIdentity === identity && isAdmin && !authLoading ? access : null,
        isLoading: authLoading || (isAdmin && (statusIdentity !== identity || isLoading)),
        revision,
        autoLogoutMessage,
        error: statusIdentity === identity && isAdmin && !authLoading ? error : null,
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
