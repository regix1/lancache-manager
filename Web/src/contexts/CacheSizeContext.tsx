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
  const [hasFetched, setHasFetched] = useState(false);
  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchCacheSize = useCallback(
    async (force?: boolean) => {
      // Skip if already loading
      if (isLoading) return;

      // Skip if page is hidden - will retry when visible
      if (document.hidden) return;

      // Cancel any in-flight request
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }

      setIsLoading(true);
      setError(null);

      try {
        const size = await ApiService.getCacheSize(undefined, force);
        setCacheSize(size);
        setHasFetched(true);
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
    setHasFetched(false);
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

  // Auto-fetch on first access if not already fetched
  const contextValue: CacheSizeContextType = {
    cacheSize,
    isLoading,
    error,
    fetchCacheSize: useCallback(
      async (force?: boolean) => {
        // If we haven't fetched yet and this is the first call, fetch automatically
        if (!hasFetched && !isLoading) {
          await fetchCacheSize(force);
        } else if (hasFetched) {
          // Manual refresh - always fetch
          await fetchCacheSize(force);
        }
      },
      [hasFetched, isLoading, fetchCacheSize]
    ),
    clearCacheSize
  };

  return <CacheSizeContext.Provider value={contextValue}>{children}</CacheSizeContext.Provider>;
};
