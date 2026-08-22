import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import ApiService, {
  type ClientHostnamesResponse,
  type ClientHostnameSettings
} from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getErrorMessage } from '@utils/error';
import { ClientHostnameContext } from './ClientHostnameContext.types';

// What the panel shows before the server has answered, and after an older server that does not
// send settings at all. Discovery on, guests not shown names: the same defaults the server holds.
const DEFAULT_SETTINGS: ClientHostnameSettings = {
  resolver: null,
  guestAccess: false,
  routerLookup: true,
  dockerLookup: true
};

interface ClientHostnameProviderProps {
  children: ReactNode;
}

export const ClientHostnameProvider: React.FC<ClientHostnameProviderProps> = ({ children }) => {
  const { authMode, isLoading: authLoading } = useAuth();
  const { on, off, isConnected } = useSignalR();
  const [hostnameLookup, setHostnameLookup] = useState<ClientHostnamesResponse>({
    enabled: false,
    hostnames: {},
    reason: 'none',
    settings: DEFAULT_SETTINGS
  });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // A partial result is actionable but can remain true for devices that intentionally have no PTR.
  // Keep dismissal only for this mounted app lifetime; a reload starts a fresh app and shows it again.
  const [someUnnamedDismissed, setSomeUnnamedDismissed] = useState(false);
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

  // Every signed-in viewer asks, guest included. The server decides what a guest is told: it
  // answers one as though the lookup were off until an admin allows guests to see client names,
  // so the map is simply empty rather than the request being an error the page has to explain.
  useEffect(() => {
    if (authLoading) return;
    if (authMode === 'authenticated' || authMode === 'guest') {
      refreshHostnames();
    } else {
      // Retiring the attempt in flight means its own finally will not clear the spinner, and
      // nothing replaces it here, so this branch owns that.
      attemptRef.current++;
      setLoading(false);
      setHostnameLookup((previous) => ({
        ...previous,
        enabled: false,
        hostnames: {},
        reason: 'none'
      }));
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
        setHostnameLookup((previous) => ({
          ...previous,
          enabled: false,
          hostnames: {},
          reason: 'none'
        }));
        return;
      }
      setHostnameLookup((previous) => ({ ...previous, enabled: true }));
      await refreshHostnames();
    },
    [refreshHostnames]
  );

  // The server refuses anything outside the private ranges, so the rejection reaches the caller as
  // an error rather than being swallowed into a silently unchanged field.
  const setSettings = useCallback(
    async (settings: ClientHostnameSettings): Promise<void> => {
      const result = await ApiService.setClientHostnameSettings(settings);
      setHostnameLookup((previous) => ({ ...previous, settings: result }));
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
  const dismissSomeUnnamed = useCallback(() => setSomeUnnamedDismissed(true), []);

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
        someUnnamedDismissed,
        settings: hostnameLookup.settings ?? DEFAULT_SETTINGS,
        loading,
        error,
        getHostnameForIp,
        refreshHostnames,
        setEnabled,
        setSettings,
        dismissSomeUnnamed
      }}
    >
      {children}
    </ClientHostnameContext.Provider>
  );
};
