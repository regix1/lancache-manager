import { useState, useCallback } from 'react';

/**
 * Hook for managing loading state in manager components.
 * Tracks both current loading state and whether initial load has completed.
 *
 * @returns {object} Loading state and control functions
 * - isLoading: Whether a loading operation is in progress
 * - hasInitiallyLoaded: Whether the component has completed its first load
 * - setLoading: Function to update the loading state
 * - markLoaded: Function to mark initial load complete and clear loading state
 */
export const useManagerLoading = (initialLoading = false) => {
  const [isLoading, setIsLoading] = useState(initialLoading);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [hasInitiallyLoaded, setHasInitiallyLoaded] = useState(false);

  const setLoading = useCallback((loading: boolean) => {
    setIsLoading(loading);
  }, []);

  const beginLoad = useCallback(
    (refresh = false) => {
      if (refresh && hasInitiallyLoaded) {
        setIsRefreshing(true);
      } else {
        setIsLoading(true);
      }
    },
    [hasInitiallyLoaded]
  );

  const markLoaded = useCallback(() => {
    setHasInitiallyLoaded(true);
    setIsLoading(false);
    setIsRefreshing(false);
  }, []);

  const markFailed = useCallback(() => {
    setIsLoading(false);
    setIsRefreshing(false);
  }, []);

  return {
    isLoading,
    isRefreshing,
    hasInitiallyLoaded,
    setLoading,
    beginLoad,
    markLoaded,
    markFailed
  };
};
