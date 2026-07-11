import React, { useState, useCallback, useRef, useEffect, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import type { CacheSizeInfo } from '@/types';
import { useSignalR } from './SignalRContext/useSignalR';
import { CacheSizeContext, type CacheSizeContextType } from './CacheSizeContext.types';

interface CacheSizeProviderProps {
  children: ReactNode;
}

export const CacheSizeProvider: React.FC<CacheSizeProviderProps> = ({ children }) => {
  const { on, off } = useSignalR();
  const [cacheSize, setCacheSize] = useState<CacheSizeInfo | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [hasFetched, setHasFetched] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchCacheSize = useCallback(async (force?: boolean) => {
    // Force refresh supersedes an in-flight read; non-force requests stay single-flight.
    if (!force) {
      if (abortControllerRef.current) return;
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
      setHasFetched(true);
      if ('scanning' in size || 'queued' in size) {
        // A scan is running or the explicit refresh was started/queued. SignalR owns its
        // running/waiting card, and CacheScanComplete reloads the persisted result afterward.
        // Keep showing the last size instead of treating the accepted response as data/error.
        return;
      }
      if ('available' in size) {
        // No persisted scan exists yet. This is the cache panel's normal empty state; only
        // the configured schedule or an explicit Refresh should launch the full-disk walk.
        setCacheSize(null);
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
      if (isAbortError(err)) {
        return;
      }
      if (
        err instanceof TypeError &&
        /failed to fetch|load failed|network ?error/i.test(err.message)
      ) {
        // Record the quiet attempt so a disconnected client cannot spin the mount effect into
        // an immediate retry loop. A later scheduled completion or explicit Refresh retries it.
        setHasFetched(true);
        return;
      }

      console.error('[CacheSize] Failed to fetch cache size:', err);
      setHasFetched(true);
      setError(err instanceof Error ? err.message : 'Failed to calculate cache size');
    } finally {
      // Only clear the ref if this fetch still owns it - a newer request may have already
      // replaced the controller. The superseded request must not mark that newer fetch idle.
      if (abortControllerRef.current === controller) {
        setIsLoading(false);
        abortControllerRef.current = null;
      }
    }
  }, []);

  // Explicit and scheduled scans emit this only after the new result has been persisted.
  // Keep the global cache-size state current even when the management cache panel is unmounted.
  useEffect(() => {
    const handleCacheScanComplete = () => {
      void fetchCacheSize();
    };

    on('CacheScanComplete', handleCacheScanComplete);
    return () => off('CacheScanComplete', handleCacheScanComplete);
  }, [on, off, fetchCacheSize]);

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

  const contextValue: CacheSizeContextType = {
    cacheSize,
    isLoading,
    hasFetched,
    error,
    fetchCacheSize,
    clearCacheSize
  };

  return <CacheSizeContext.Provider value={contextValue}>{children}</CacheSizeContext.Provider>;
};
