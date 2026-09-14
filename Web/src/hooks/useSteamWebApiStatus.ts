import { useState, useEffect, useCallback, useRef } from 'react';
import ApiService from '@services/api.service';
import { assertOk } from '@services/apiError';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getErrorMessage } from '@utils/error';

export interface SteamWebApiStatus {
  version: string;
  isV2Available: boolean;
  isV1Available: boolean;
  hasApiKey: boolean;
  isFullyOperational: boolean;
  message: string;
  lastChecked: string;
  canManage?: boolean;
  ownershipReason?: string | null;
}

export const useSteamWebApiStatusState = () => {
  const [status, setStatus] = useState<SteamWebApiStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const {
    authMode,
    authenticationEnabled,
    accountId,
    sessionId,
    isLoading: authLoading
  } = useAuth();
  const { isConnected } = useSignalR();
  const hasAccess =
    authenticationEnabled === false ||
    (authMode === 'authenticated' && Boolean(accountId && sessionId));
  const identity = JSON.stringify([authenticationEnabled, accountId, sessionId, authMode]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const requestRef = useRef(0);
  const [statusIdentity, setStatusIdentity] = useState<string | null>(null);
  const hasFailedAuth = useRef(false);

  const fetchStatus = useCallback(
    async (forceRefresh = false, skipLoading = false) => {
      if (authLoading || !hasAccess || identityRef.current !== identity) return;
      const request = ++requestRef.current;
      const current = () => identityRef.current === identity && requestRef.current === request;
      // A forced refresh is an explicit request, so it clears an earlier auth failure instead of
      // being dropped by it. The latch exists to stop the automatic retries from looping against a
      // 401, and without this a single early 401 left the status null for the rest of the session:
      // every later refresh returned here, and the callers that read isFullyOperational then saw a
      // key as missing while the server reported it working.
      if (forceRefresh) {
        hasFailedAuth.current = false;
      }

      // Don't retry if we've already failed auth
      if (hasFailedAuth.current) {
        return;
      }

      try {
        if (!skipLoading) {
          setLoading(true);
        }
        setError(null);

        const response = await fetch(
          `/api/steam-api-keys/status?forceRefresh=${forceRefresh}`,
          ApiService.getFetchOptions()
        );
        if (!current()) return;

        if (response.status === 401) {
          // Auth failed - silently set status to null and stop retrying
          hasFailedAuth.current = true;
          setStatus(null);
          setStatusIdentity(identity);
          setLoading(false);
          return;
        }

        await assertOk(response);

        const statusResponse: SteamWebApiStatus = await response.json();
        if (!current()) return;
        setStatus(statusResponse);
        setStatusIdentity(identity);
      } catch (err: unknown) {
        if (!current()) return;
        setStatus((previous) =>
          previous ? { ...previous, canManage: false, ownershipReason: 'status-unavailable' } : null
        );
        setStatusIdentity(identity);
        const errorMessage = getErrorMessage(err);
        setError(errorMessage);
        console.error('[SteamWebApiStatus] Error:', err);
      } finally {
        // Always clear loading - skipLoading only controls whether loading is
        // SET to true, not whether it's cleared. Prevents stuck loading state
        // when concurrent calls race with different skipLoading values.
        if (current()) setLoading(false);
      }
    },
    [identity, authLoading, hasAccess]
  );

  useEffect(() => {
    // Reset auth failure flag when auth state changes
    hasFailedAuth.current = false;
    requestRef.current += 1;
    setStatus(null);
    setStatusIdentity(null);
    setError(null);

    // Only fetch when auth is ready and user has access
    if (authLoading || !hasAccess) {
      return;
    }

    // Initial fetch
    fetchStatus();
    return () => {
      requestRef.current += 1;
    };

    // No automatic polling - rely on optimistic updates and manual refresh
    // This prevents flickering and unnecessary API calls
  }, [fetchStatus, authLoading, hasAccess]);

  const refresh = useCallback(() => fetchStatus(true, true), [fetchStatus]);

  // Nothing broadcasts a key change, so a key added or revoked while this tab was disconnected only
  // shows up by asking again. Admin-only, like the effect above: the endpoint 401s for anyone else.
  // Skipping the loading flag keeps the panel showing the last answer until the new one lands.
  useReconnectRefetch(isConnected, () => {
    if (hasAccess) {
      void fetchStatus(false, true);
    }
  });

  return {
    status: statusIdentity === identity && hasAccess ? status : null,
    loading: authLoading || (hasAccess && loading),
    error: statusIdentity === identity ? error : null,
    refresh
  };
};
