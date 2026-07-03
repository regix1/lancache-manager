import React, { useState, useCallback, useRef, useEffect, type ReactNode } from 'react';
import ApiService from '@services/api.service';
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

      // Cancel any in-flight request
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }

      setIsLoading(true);
      setError(null);

      try {
        const size = await ApiService.getCacheSize(undefined, force);
        if ('scanning' in size) {
          // A scan is already running elsewhere (e.g. the scheduled service holds the
          // scan lock) - a waiting state, not an error. Keep showing whatever cache size
          // is already loaded instead of clearing it or surfacing an error toast.
          return;
        }
        setCacheSize(size);
      } catch (err) {
        // Don't log or set error for aborted requests
        if (err instanceof Error && err.name === 'AbortError') {
          return;
        }

        console.error('[CacheSize] Failed to fetch cache size:', err);
        setError(err instanceof Error ? err.message : 'Failed to calculate cache size');
      } finally {
        setIsLoading(false);
        abortControllerRef.current = null;
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
