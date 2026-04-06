import { useState, useEffect, useRef, useCallback } from 'react';
import { getCachedValue, setCachedValue } from '@utils/idbCache';
import { useMockMode } from '@contexts/useMockMode';
import { useTimeFilter } from '@contexts/useTimeFilter';

interface UseWidgetDataOptions<T> {
  /** IDB cache key for persisting data across page reloads */
  cacheKey: string;
  /** Function to fetch data from the API. Receives abort signal and time range params. */
  fetchFn: (
    signal: AbortSignal,
    params: { startTime?: number; endTime?: number; eventId?: number }
  ) => Promise<T>;
  /** Function to generate mock data (called when mock mode is active) */
  mockFn: () => T;
  /** Additional dependencies that should trigger a re-fetch */
  deps?: unknown[];
}

interface UseWidgetDataResult<T> {
  /** The current data (or null if not yet loaded) */
  data: T | null;
  /** Whether data is being loaded for the first time (no previous data available) */
  loading: boolean;
  /** Error message if the fetch failed */
  error: string | null;
  /** Display data: current data or previous data preserved during loading */
  displayData: T | null;
}

/**
 * Shared hook that extracts the common data-fetching pattern used by dashboard widgets.
 *
 * Handles:
 * - Initial state from IDB cache (instant display on mount)
 * - AbortController for fetch cancellation
 * - Previous data preservation during re-fetches (no loading flicker)
 * - Mock mode support
 * - IDB cache persistence
 * - Time filter integration
 */
export function useWidgetData<T>(options: UseWidgetDataOptions<T>): UseWidgetDataResult<T> {
  const { cacheKey, fetchFn, mockFn, deps = [] } = options;
  const { timeRange, getTimeRangeParams, selectedEventIds } = useTimeFilter();
  const { mockMode } = useMockMode();

  const [data, setData] = useState<T | null>(() => getCachedValue<T>(cacheKey) ?? null);
  const [loading, setLoading] = useState(() => getCachedValue(cacheKey) === undefined);
  const [error, setError] = useState<string | null>(null);
  const prevDataRef = useRef<T | null>(getCachedValue<T>(cacheKey) ?? null);

  const fetchFnRef = useRef(fetchFn);
  fetchFnRef.current = fetchFn;

  const mockFnRef = useRef(mockFn);
  mockFnRef.current = mockFn;

  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  getTimeRangeParamsRef.current = getTimeRangeParams;

  const stableDeps = useCallback(() => deps, deps); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (mockMode) {
      setLoading(true);
      const mockData = mockFnRef.current();
      setData(mockData);
      setError(null);
      setLoading(false);
      return;
    }

    const controller = new AbortController();

    // Store current data as previous (keep visible during fetch)
    if (data) {
      prevDataRef.current = data;
    }

    const fetchData = async () => {
      try {
        // Only show loading when there's no prior data
        if (!prevDataRef.current) setLoading(true);
        setError(null);
        const { startTime, endTime } = getTimeRangeParamsRef.current();
        const eventId = selectedEventIds.length > 0 ? selectedEventIds[0] : undefined;
        const response = await fetchFnRef.current(controller.signal, {
          startTime,
          endTime,
          eventId
        });
        setData(response);
        setCachedValue(cacheKey, response);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError('Failed to load data');
          console.error(`useWidgetData[${cacheKey}] fetch error:`, err);
          // Don't clear data on error - keep previous data visible
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    };

    fetchData();

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [timeRange, mockMode, selectedEventIds, cacheKey, stableDeps]);

  // Use displayData to preserve previous values during loading
  const displayData = data || prevDataRef.current;

  return { data, loading, error, displayData };
}
