import { useEffect, useRef } from 'react';

/**
 * Hook that triggers a callback when the page becomes visible after being hidden.
 * This is useful for refreshing data when a user returns to the tab, as some browsers
 * pause background activities (including SignalR connections) when tabs are hidden.
 *
 * @param callback - Function to call when page becomes visible. Will be called after a 500ms delay
 *                   to allow connections (like SignalR) to reconnect first.
 * @param enabled - Whether the visibility change listener is enabled. Default: true
 *
 * @example
 * ```tsx
 * usePageVisibilityRefresh(() => {
 *   fetchData({ showLoading: false, forceRefresh: true });
 * }, !mockMode);
 * ```
 */
export function usePageVisibilityRefresh(
  callback: () => void,
  enabled: boolean = true
): void {
  // Store callback in ref to avoid needing it in dependencies
  const callbackRef = useRef(callback);
  callbackRef.current = callback;

  useEffect(() => {
    if (!enabled) return;

    const handleVisibilityChange = (): void => {
      if (!document.hidden) {
        // Page became visible - refresh data after a small delay
        // to let SignalR reconnect first
        setTimeout(() => {
          callbackRef.current();
        }, 500);
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);

    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [enabled]);
}
