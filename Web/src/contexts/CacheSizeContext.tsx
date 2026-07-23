import React, { useState, useCallback, useRef, useEffect, type ReactNode } from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import type { CacheSizeInfo } from '@/types';
import { useSignalR } from './SignalRContext/useSignalR';
import { useAuth } from '@contexts/useAuth';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { CacheSizeContext, type CacheSizeContextType } from './CacheSizeContext.types';

interface CacheSizeProviderProps {
  children: ReactNode;
}

export const CacheSizeProvider: React.FC<CacheSizeProviderProps> = ({ children }) => {
  const { on, off, isConnected } = useSignalR();
  const { isAdmin } = useAuth();
  const [cacheSize, setCacheSize] = useState<CacheSizeInfo | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [hasFetched, setHasFetched] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  // A completion event can land while the single-flight read is still open. Remember that a
  // refetch was requested and run exactly one once the in-flight read settles, so the fresh
  // persisted result is never dropped by the single-flight guard.
  const pendingRefetchRef = useRef(false);
  // Stable handle to the latest fetch so the settle path can trigger the queued refetch without
  // widening the callback's dependency list.
  const fetchRef = useRef<(force?: boolean) => Promise<void>>(() => Promise.resolve());

  const fetchCacheSize = useCallback(async (force?: boolean) => {
    // Force refresh supersedes an in-flight read; non-force requests stay single-flight but
    // queue one refetch so a completion event that arrives mid-flight still reloads the result.
    if (!force) {
      if (abortControllerRef.current) {
        pendingRefetchRef.current = true;
        return;
      }
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
      // Mark fetched even for a scanning/queued response. hasFetched gates only the CONSUMER's
      // one-shot mount fetch; leaving it false while a scan runs makes that effect re-fire every
      // time isLoading toggles back, spinning GET /cache/size for the scan's whole duration. The
      // authoritative result is still delivered by CacheScanComplete (and the reconnect resync),
      // which call fetchCacheSize directly and do not consult hasFetched. Keep the last size.
      setHasFetched(true);
      if ('scanning' in size || 'queued' in size) {
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
        if (pendingRefetchRef.current) {
          // Drain the single queued refetch now that the guard is clear.
          pendingRefetchRef.current = false;
          void fetchRef.current();
        }
      }
    }
  }, []);

  // Keep a stable handle to the latest fetch for the settle-path drain above.
  useEffect(() => {
    fetchRef.current = fetchCacheSize;
  }, [fetchCacheSize]);

  // Explicit and scheduled scans emit this only after the new result has been persisted.
  // Keep the global cache-size state current even when the management cache panel is unmounted.
  useEffect(() => {
    const handleCacheScanComplete = () => {
      void fetchCacheSize();
    };

    on('CacheScanComplete', handleCacheScanComplete);
    return () => off('CacheScanComplete', handleCacheScanComplete);
  }, [on, off, fetchCacheSize]);

  // A reconnect can swallow the CacheScanComplete of a scan that finished while the socket was
  // down - resync the persisted size on a genuine reconnect. Admin-only: /cache/size 403s for
  // guests, and this provider is global (mounted for every session), so a guest reconnect - or an
  // admin->guest switch - must not fire it. First connect is already suppressed by the hook.
  useReconnectRefetch(isConnected, () => {
    if (isAdmin) {
      void fetchCacheSize();
    }
  });

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
