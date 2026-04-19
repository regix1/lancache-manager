import { useCallback, useEffect, useRef, useState } from 'react';
import type { UnifiedNotification } from '@contexts/notifications/types';

/**
 * Tracks optimistic "pending" state for operations that follow the pattern:
 *   user clicks → POST 202 Accepted → wait for *Started SignalR event → registry opens notification
 *
 * Usage:
 *   const { isPending, markStarting, clearOnNotification } = useOptimisticPending<string>();
 *
 *   // On click:
 *   markStarting(key);               // sets pending + arms 5 s safety timeout
 *
 *   // In a useEffect watching notifications:
 *   clearOnNotification(key, notifications, (n, k) => n.type === 'foo' && n.details?.service === k);
 */
export function useOptimisticPending<K extends string>(): {
  isPending: (key: K) => boolean;
  anyPending: boolean;
  markStarting: (key: K) => void;
  clearPending: (key: K) => void;
  clearOnNotification: (
    key: K,
    notifications: UnifiedNotification[],
    matches: (n: UnifiedNotification, key: K) => boolean
  ) => void;
} {
  const [pendingKeys, setPendingKeys] = useState<Set<K>>(new Set());
  const timeoutRefs = useRef<Map<K, ReturnType<typeof setTimeout>>>(new Map());

  const clearKey = useCallback((key: K) => {
    setPendingKeys((prev) => {
      if (!prev.has(key)) return prev;
      const next = new Set(prev);
      next.delete(key);
      return next;
    });
    const existing = timeoutRefs.current.get(key);
    if (existing !== undefined) {
      clearTimeout(existing);
      timeoutRefs.current.delete(key);
    }
  }, []);

  const markStarting = useCallback(
    (key: K, safetyTimeoutMs = 5000) => {
      // Clear any existing timeout for this key before re-arming
      const existing = timeoutRefs.current.get(key);
      if (existing !== undefined) {
        clearTimeout(existing);
      }

      setPendingKeys((prev) => {
        const next = new Set(prev);
        next.add(key);
        return next;
      });

      const handle = setTimeout(() => {
        clearKey(key);
      }, safetyTimeoutMs);
      timeoutRefs.current.set(key, handle);
    },
    [clearKey]
  );

  const clearOnNotification = useCallback(
    (
      key: K,
      notifications: UnifiedNotification[],
      matches: (n: UnifiedNotification, key: K) => boolean
    ) => {
      if (pendingKeys.has(key) && notifications.some((n) => matches(n, key))) {
        clearKey(key);
      }
    },
    [pendingKeys, clearKey]
  );

  const isPending = useCallback((key: K) => pendingKeys.has(key), [pendingKeys]);

  const anyPending = pendingKeys.size > 0;

  // Cleanup all timeouts on unmount
  useEffect(() => {
    return () => {
      // eslint-disable-next-line react-hooks/exhaustive-deps
      timeoutRefs.current.forEach((handle) => clearTimeout(handle));
    };
  }, []);

  return { isPending, anyPending, markStarting, clearPending: clearKey, clearOnNotification };
}
