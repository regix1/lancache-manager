import { useState, useEffect, useCallback, useRef } from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/AuthContext';

export interface SteamWebApiStatus {
  version: string;
  isV2Available: boolean;
  isV1Available: boolean;
  hasApiKey: boolean;
  isFullyOperational: boolean;
  message: string;
  lastChecked: string;
}

export const useSteamWebApiStatus = () => {
  const [status, setStatus] = useState<SteamWebApiStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { isAuthenticated, authMode, isLoading: authLoading } = useAuth();
  const hasAccess = isAuthenticated || authMode === 'guest';
  const hasFailedAuth = useRef(false);

  const fetchStatus = useCallback(async (forceRefresh: boolean = false, skipLoading: boolean = false) => {
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

      if (response.status === 401) {
        // Auth failed - silently set status to null and stop retrying
        hasFailedAuth.current = true;
        setStatus(null);
        if (!skipLoading) {
          setLoading(false);
        }
        return;
      }

      if (!response.ok) {
        throw new Error('Failed to fetch Steam Web API status');
      }

      const data = await response.json();
      setStatus(data);
    } catch (err: unknown) {
      const errorMessage = err instanceof Error ? err.message : 'Failed to fetch Steam Web API status';
      setError(errorMessage);
      console.error('[SteamWebApiStatus] Error:', err);
    } finally {
      if (!skipLoading) {
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    // Reset auth failure flag when auth state changes
    hasFailedAuth.current = false;

    // Only fetch when auth is ready and user has access
    if (authLoading || !hasAccess) {
      return;
    }

    // Initial fetch
    fetchStatus();

    // No automatic polling - rely on optimistic updates and manual refresh
    // This prevents flickering and unnecessary API calls
  }, [fetchStatus, authLoading, hasAccess]);

  const refresh = useCallback(() => fetchStatus(true, true), [fetchStatus]);

  const updateStatus = useCallback((updater: (prev: SteamWebApiStatus | null) => SteamWebApiStatus | null) => {
    setStatus(updater);
  }, []);

  return { status, loading, error, refresh, updateStatus };
};
