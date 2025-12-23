import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  useRef,
  useCallback,
  useMemo
} from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import MockDataService from '../../test/mockData.service';
import { useTimeFilter } from '../TimeFilterContext';
import { usePollingRate } from '../PollingRateContext';
import { useSignalR } from '../SignalRContext';
import type { NewDownloadsPayload } from '../SignalRContext/types';
import { SIGNALR_REFRESH_EVENTS } from '../SignalRContext/types';
import type { DownloadsContextType, DownloadsProviderProps } from './types';

// Re-export types for consumers
export type { DownloadsContextType, DownloadsProviderProps } from './types';

// ============================================
// CONTEXT & HOOK
// ============================================

const DownloadsContext = createContext<DownloadsContextType | undefined>(undefined);

export const useDownloads = () => {
  const context = useContext(DownloadsContext);
  if (!context) {
    throw new Error('useDownloads must be used within DownloadsProvider');
  }
  return context;
};

// ============================================
// PROVIDER
// ============================================

export const DownloadsProvider: React.FC<DownloadsProviderProps> = ({
  children,
  mockMode = false
}) => {
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate } = useTimeFilter();
  const { pollingRate, getPollingInterval } = usePollingRate();
  const signalR = useSignalR();

  // ============================================
  // STATE
  // ============================================

  const [latestDownloads, setLatestDownloads] = useState<import('../../types').Download[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastCustomDates, setLastCustomDates] = useState<{ start: Date | null; end: Date | null }>({
    start: null,
    end: null
  });

  // ============================================
  // REFS (for avoiding stale closures)
  // ============================================

  const isInitialLoad = useRef(true);
  const hasData = useRef(false);
  const fetchInProgress = useRef(false);
  const abortControllerRef = useRef<AbortController | null>(null);
  const lastFetchTime = useRef<number>(0);
  const pollingIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const lastSignalRRefresh = useRef<number>(0);

  // Sync refs - updated on every render BEFORE effects run
  // This ensures functions reading from these refs get current values
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getPollingIntervalRef = useRef(getPollingInterval);
  const mockModeRef = useRef(mockMode);

  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getPollingIntervalRef.current = getPollingInterval;
  mockModeRef.current = mockMode;

  // ============================================
  // CORE DATA FETCHING
  // ============================================

  const fetchDownloads = useCallback(async (options: { showLoading?: boolean; isInitial?: boolean } = {}) => {
    if (mockModeRef.current) return;

    const { showLoading = false, isInitial = false } = options;

    // Debounce rapid calls (min 250ms between fetches) - skip for initial load
    const now = Date.now();
    if (!isInitial && now - lastFetchTime.current < 250) {
      return;
    }
    lastFetchTime.current = now;

    // Abort any in-flight request BEFORE checking concurrent flag
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }

    // Prevent concurrent fetches (except for initial load which should always proceed)
    if (fetchInProgress.current && !isInitial) {
      return;
    }
    fetchInProgress.current = true;

    // Read current values from refs - these are always up-to-date
    const currentTimeRange = currentTimeRangeRef.current;
    const { startTime, endTime } = getTimeRangeParamsRef.current();

    abortControllerRef.current = new AbortController();

    try {
      if (showLoading) {
        setLoading(true);
      }

      const timeout = 10000;
      const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

      const latest = await ApiService.getLatestDownloads(
        abortControllerRef.current.signal,
        'unlimited',
        startTime,
        endTime
      );

      clearTimeout(timeoutId);

      // Only apply results if timeRange hasn't changed during fetch (stale data protection)
      if (latest !== undefined && currentTimeRangeRef.current === currentTimeRange) {
        setLatestDownloads(latest);
        hasData.current = true;
      }

      setError(null);
    } catch (err: unknown) {
      // Fixed: Proper error handling without dead code
      if (isAbortError(err)) {
        if (!hasData.current) {
          setError('Request timeout - the server may be busy');
        }
      } else if (!hasData.current) {
        setError('Failed to fetch downloads from API');
      }
    } finally {
      if (showLoading) {
        setLoading(false);
      }
      if (isInitial) {
        isInitialLoad.current = false;
      }
      fetchInProgress.current = false;
    }
  }, []);

  const refreshDownloads = useCallback(async () => {
    await fetchDownloads({ showLoading: true });
  }, [fetchDownloads]);

  // ============================================
  // SIGNALR EVENT HANDLERS
  // ============================================

  useEffect(() => {
    if (mockMode) return;

    // Handler for NewDownloads - merges new downloads into state without full refetch
    // Only active in Live mode - polling modes handle their own updates
    const handleNewDownloads = (payload: NewDownloadsPayload) => {
      if (pollingRate !== 'LIVE') return;
      if (!payload.downloads || payload.downloads.length === 0) return;

      const { startTime, endTime } = getTimeRangeParamsRef.current();

      // Filter new downloads by current time range
      const newDownloads = payload.downloads.filter(download => {
        if (!startTime && !endTime) return true;

        const downloadTime = new Date(download.startTimeUtc).getTime() / 1000;
        if (startTime && downloadTime < startTime) return false;
        if (endTime && downloadTime > endTime) return false;
        return true;
      });

      if (newDownloads.length === 0) return;

      setLatestDownloads(prev => {
        const existingIds = new Set(prev.map(d => d.id));
        const uniqueNewDownloads = newDownloads.filter(d => !existingIds.has(d.id));

        if (uniqueNewDownloads.length === 0) return prev;

        // Prepend new downloads and cap at 500 to prevent unbounded growth
        const merged = [...uniqueNewDownloads, ...prev].slice(0, 500);
        merged.sort((a, b) =>
          new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime()
        );

        hasData.current = true;
        return merged;
      });
    };

    // Handler that respects polling rate (or instant if Live mode)
    const handleDataRefresh = () => {
      const pollingInterval = getPollingIntervalRef.current();

      // Live mode (0) = instant updates, no throttling
      if (pollingInterval === 0) {
        fetchDownloads();
        return;
      }

      // Throttle based on polling interval
      const now = Date.now();
      const timeSinceLastRefresh = now - lastSignalRRefresh.current;
      if (timeSinceLastRefresh >= pollingInterval) {
        lastSignalRRefresh.current = now;
        fetchDownloads();
      }
    };

    // Handler for database reset completion
    const handleDatabaseResetProgress = (payload: { status?: string }) => {
      const status = (payload.status || '').toLowerCase();
      if (status === 'completed' || status === 'complete' || status === 'done') {
        setTimeout(() => fetchDownloads(), 500);
      }
    };

    // Subscribe to events
    signalR.on('NewDownloads', handleNewDownloads);
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    SIGNALR_REFRESH_EVENTS.forEach(event => signalR.on(event, handleDataRefresh));

    return () => {
      signalR.off('NewDownloads', handleNewDownloads);
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      SIGNALR_REFRESH_EVENTS.forEach(event => signalR.off(event, handleDataRefresh));
    };
  }, [mockMode, signalR, fetchDownloads, pollingRate]);

  // ============================================
  // EFFECTS
  // ============================================

  // Mock data loading
  useEffect(() => {
    if (mockMode) {
      setLoading(true);
      const mockData = MockDataService.generateMockData('unlimited');
      setLatestDownloads(mockData.latestDownloads);
      setError(null);
      setLoading(false);
      hasData.current = true;
      isInitialLoad.current = false;
    }
  }, [mockMode]);

  // Initial load
  useEffect(() => {
    if (!mockMode) {
      fetchDownloads({ showLoading: true, isInitial: true });
    }

    return () => {
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, fetchDownloads]);

  // Polling interval
  const currentPollingInterval = getPollingInterval();
  useEffect(() => {
    if (mockMode || currentPollingInterval === 0) return;

    // Clear any existing polling interval
    if (pollingIntervalRef.current) {
      clearInterval(pollingIntervalRef.current);
      pollingIntervalRef.current = null;
    }

    // Don't start polling until initial load is complete
    if (isInitialLoad.current) return;

    pollingIntervalRef.current = setInterval(() => fetchDownloads(), currentPollingInterval);

    return () => {
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
        pollingIntervalRef.current = null;
      }
    };
  }, [mockMode, fetchDownloads, currentPollingInterval, loading]);

  // Time range changes
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      fetchDownloads({ showLoading: true });
    }
  }, [timeRange, mockMode, fetchDownloads]);

  // Custom date changes (debounced)
  useEffect(() => {
    if (timeRange === 'custom' && !mockMode) {
      if (customStartDate && customEndDate) {
        const datesChanged =
          lastCustomDates.start?.getTime() !== customStartDate.getTime() ||
          lastCustomDates.end?.getTime() !== customEndDate.getTime();

        if (datesChanged) {
          setLoading(true);
          const debounceTimer = setTimeout(() => {
            setLastCustomDates({ start: customStartDate, end: customEndDate });
            fetchDownloads({ showLoading: true });
          }, 50);

          return () => clearTimeout(debounceTimer);
        }
      }
    } else if (timeRange !== 'custom') {
      setLastCustomDates({ start: null, end: null });
    }
  }, [customStartDate, customEndDate, timeRange, mockMode, fetchDownloads, lastCustomDates]);

  // ============================================
  // CONTEXT VALUE
  // ============================================

  const updateDownloads = useCallback((updater: {
    latestDownloads?: (prev: import('../../types').Download[]) => import('../../types').Download[];
  }) => {
    if (updater.latestDownloads) {
      setLatestDownloads(updater.latestDownloads);
    }
  }, []);

  // Memoize context value to prevent unnecessary re-renders of consumers
  const value = useMemo<DownloadsContextType>(() => ({
    latestDownloads,
    loading,
    error,
    refreshDownloads,
    updateDownloads
  }), [latestDownloads, loading, error, refreshDownloads, updateDownloads]);

  return <DownloadsContext.Provider value={value}>{children}</DownloadsContext.Provider>;
};
