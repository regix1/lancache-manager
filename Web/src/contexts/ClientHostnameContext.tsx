import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import ApiService, { type ClientHostnamesResponse } from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getErrorMessage } from '@utils/error';
import { ClientHostnameContext } from './ClientHostnameContext.types';

interface ClientHostnameProviderProps {
  children: ReactNode;
}

export const ClientHostnameProvider: React.FC<ClientHostnameProviderProps> = ({ children }) => {
  const { authMode, isLoading: authLoading } = useAuth();
  const { on, off, isConnected } = useSignalR();
  const [hostnameLookup, setHostnameLookup] = useState<ClientHostnamesResponse>({
    enabled: false,
    hostnames: {},
    reason: 'none'
  });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // A hostname fetch waits on DNS, so it can take seconds and two of them can overlap and finish
  // out of order. Only the newest attempt may write, otherwise an older answer reinstates names the
  // toggle has just cleared and they stay on screen until something else refreshes.
  const attemptRef = useRef(0);

  // Fetch the whole address-to-name map in one request. Rows read it from memory, so no row ever
  // waits on a lookup and a missing entry simply leaves the raw address showing.
  const refreshHostnames = useCallback(async () => {
    const attempt = ++attemptRef.current;
    setLoading(true);
    setError(null);
    try {
      const result = await ApiService.getClientHostnames();
      if (attempt !== attemptRef.current) return;
      // Fall back to the no-op default if an older server has not started sending the reason yet,
      // rather than letting `undefined` reach the render or the reason util.
      setHostnameLookup({
        ...result,
        reason: result.reason ?? 'none'
      });
    } catch (err: unknown) {
      if (attempt !== attemptRef.current) return;
      setError(getErrorMessage(err));
      console.error('Failed to fetch client hostnames:', err);
    } finally {
      if (attempt === attemptRef.current) {
        setLoading(false);
      }
    }
  }, []);

  // Load the mapping for any signed-in viewer (admin or guest), matching the nickname source that
  // shares the label precedence. Changing the setting stays AdminOnly server-side.
  useEffect(() => {
    if (authLoading) return;
    if (authMode === 'authenticated' || authMode === 'guest') {
      refreshHostnames();
    } else {
      // Retiring the attempt in flight means its own finally will not clear the spinner, and
      // nothing replaces it here, so this branch owns that.
      attemptRef.current++;
      setLoading(false);
      setHostnameLookup({ enabled: false, hostnames: {}, reason: 'none' });
    }
  }, [authLoading, authMode, refreshHostnames]);

  const setEnabled = useCallback(
    async (enabled: boolean): Promise<void> => {
      const result = await ApiService.setClientHostnameLookup(enabled);
      if (!result.enabled) {
        // Dropping the names locally is enough - the rows fall back to raw addresses on the next
        // render and no table has to be refetched. Retiring any fetch still in flight keeps an
        // older response from putting the names straight back, and because that retired fetch
        // will no longer clear the spinner itself, this branch clears it.
        attemptRef.current++;
        setLoading(false);
        setHostnameLookup({ enabled: false, hostnames: {}, reason: 'none' });
        return;
      }
      setHostnameLookup((previous) => ({ ...previous, enabled: true }));
      await refreshHostnames();
    },
    [refreshHostnames]
  );

  const getHostnameForIp = useCallback(
    (clientIp: string): string | null => {
      return hostnameLookup.hostnames[clientIp] ?? null;
    },
    [hostnameLookup]
  );

  // The handler outlives each render, and authMode changes while it is subscribed, so it reads the
  // mode from a ref rather than closing over the value it was created with.
  const authModeRef = useRef(authMode);
  useEffect(() => {
    authModeRef.current = authMode;
  }, [authMode]);

  // A ClientHostnamesChanged raised while the socket was down is never delivered, so the labels stay
  // as they were until something else refreshes them. Same signed-in check as the handler below.
  useReconnectRefetch(isConnected, () => {
    if (authModeRef.current !== 'authenticated' && authModeRef.current !== 'guest') return;
    void refreshHostnames();
  });

  // An admin flipping the setting changes every viewer's labels, so refresh for any signed-in
  // viewer rather than only the one who made the change.
  useEffect(() => {
    const handleHostnamesChanged = () => {
      if (authModeRef.current !== 'authenticated' && authModeRef.current !== 'guest') return;
      refreshHostnames();
    };

    on('ClientHostnamesChanged', handleHostnamesChanged);

    return () => {
      off('ClientHostnamesChanged', handleHostnamesChanged);
    };
  }, [on, off, refreshHostnames]);

  return (
    <ClientHostnameContext.Provider
      value={{
        enabled: hostnameLookup.enabled,
        reason: hostnameLookup.reason,
        loading,
        error,
        getHostnameForIp,
        refreshHostnames,
        setEnabled
      }}
    >
      {children}
    </ClientHostnameContext.Provider>
  );
};
