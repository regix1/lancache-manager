import { useState, useEffect, useCallback } from 'react';
import ApiService from '@services/api.service';

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

  const fetchStatus = useCallback(async (forceRefresh: boolean = false, skipLoading: boolean = false) => {
    try {
      if (!skipLoading) {
        setLoading(true);
      }
      setError(null);

      const response = await fetch(`/api/steamwebapi/status?forceRefresh=${forceRefresh}`, {
        headers: ApiService.getHeaders()
      });

      if (!response.ok) {
        throw new Error('Failed to fetch Steam Web API status');
      }

      const data = await response.json();
      setStatus(data);
    } catch (err: any) {
      setError(err.message || 'Failed to fetch Steam Web API status');
      console.error('[SteamWebApiStatus] Error:', err);
    } finally {
      if (!skipLoading) {
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    // Initial fetch
    fetchStatus();

    // No automatic polling - rely on optimistic updates and manual refresh
    // This prevents flickering and unnecessary API calls
  }, [fetchStatus]);

  const refresh = useCallback(() => fetchStatus(true, true), [fetchStatus]);

  const updateStatus = useCallback((updater: (prev: SteamWebApiStatus | null) => SteamWebApiStatus | null) => {
    setStatus(updater);
  }, []);

  return { status, loading, error, refresh, updateStatus };
};
