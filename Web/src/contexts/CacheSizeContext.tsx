import React, { useState, useCallback, useRef, useEffect, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import type { CacheSizeInfo } from '@/types';
import { CacheSizeContext, type CacheSizeContextType } from './CacheSizeContext.types';

interface CacheSizeProviderProps {
  children: ReactNode;
}

export const CacheSizeProvider: React.FC<CacheSizeProviderProps> = ({ children }) => {
  const [cacheSize, setCacheSize] = useState<CacheSizeInfo | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchCacheSize = useCallback(
    async (force?: boolean) => {
      // Force refresh always runs; non-force requests skip when already in flight or tab hidden.
      if (!force) {
        if (isLoading) return;
        if (document.hidden) return;
      }

      // Cancel any in-flight request, then arm a fresh controller so this fetch is actually
      // abortable (a real cancel then rejects with an AbortError, filtered below).
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }
      const controller = new AbortController();
      abortControllerRef.current = controller;

      setIsLoading(true);
      setError(null);

      try {
        const size = await ApiService.getCacheSize(undefined, force, controller.signal);
        if ('scanning' in size) {
          // A scan is already running elsewhere (e.g. the scheduled service holds the
          // scan lock) - a waiting state, not an error. Keep showing whatever cache size
          // is already loaded instead of clearing it or surfacing an error toast.
          return;
        }
        setCacheSize(size);
      } catch (err) {
        // Cancels and transient client-side disconnects are expected outcomes, not failures, so
        // they must never surface a "Cache Size Error" notification. Both are swallowed silently;
        // this comment is the explicit "why" the error-handling standard requires for a deliberate
        // swallow (docs/error-handling-standard.md):
        //   - a real cancel (superseded request / unmount) rejects with an AbortError.
        //   - a mobile browser dropping the long-lived GET /cache/size request on navigation or
        //     backgrounding rejects the fetch with a network TypeError ("Failed to fetch" /
        //     "Load failed" / "NetworkError ...", depending on the browser).
        // The scan's authoritative progress lives in the separate SignalR `cache_size_scan` op, so
        // a dropped size fetch is a no-op here - keep the last loaded size and stay quiet.
        // The TypeError arm is narrowed to network-disconnect messages only, so a genuine
        // non-network TypeError (e.g. a coding bug) still surfaces via setError below.
        if (
          isAbortError(err) ||
          (err instanceof TypeError &&
            /failed to fetch|load failed|network ?error/i.test(err.message))
        ) {
          return;
        }

        console.error('[CacheSize] Failed to fetch cache size:', err);
        setError(err instanceof Error ? err.message : 'Failed to calculate cache size');
      } finally {
        setIsLoading(false);
        // Only clear the ref if this fetch still owns it - a newer request may have already
        // replaced the controller, and we must not orphan its abort handle.
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }
    },
    [isLoading]
  );

  // Clear cache size data (useful after cache clearing operations)
  const clearCacheSize = useCallback(() => {
    setCacheSize(null);
    setError(null);
  }, []);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }
    };
  }, []);

  const contextValue: CacheSizeContextType = {
    cacheSize,
    isLoading,
    error,
    fetchCacheSize,
    clearCacheSize
  };

  return <CacheSizeContext.Provider value={contextValue}>{children}</CacheSizeContext.Provider>;
};
