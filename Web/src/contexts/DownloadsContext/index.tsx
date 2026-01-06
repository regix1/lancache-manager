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
import { useRefreshRate } from '../RefreshRateContext';
import { useSignalR } from '../SignalRContext';
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
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate, selectedEventId } = useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
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
  const lastSignalRRefresh = useRef<number>(0);
  const pendingRefreshRef = useRef<NodeJS.Timeout | null>(null);

  // Sync refs - updated on every render BEFORE effects run
  // This ensures functions reading from these refs get current values
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getRefreshIntervalRef = useRef(getRefreshInterval);
  const mockModeRef = useRef(mockMode);
  const selectedEventIdRef = useRef<number | null>(selectedEventId);

  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getRefreshIntervalRef.current = getRefreshInterval;
  mockModeRef.current = mockMode;
  selectedEventIdRef.current = selectedEventId;

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
    const eventId = selectedEventIdRef.current ?? undefined;

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
        endTime,
        eventId
      );

      clearTimeout(timeoutId);

      // Only apply results if timeRange hasn't changed during fetch (stale data protection)
      if (latest !== undefined && currentTimeRangeRef.current === currentTimeRange) {
        setLatestDownloads(latest);
        setError(null);
        if (showLoading) {
          setLoading(false);
        }
        hasData.current = true;
      } else {
        setError(null);
        if (showLoading) {
          setLoading(false);
        }
      }
    } catch (err: unknown) {
      // Fixed: Proper error handling without dead code
      if (isAbortError(err)) {
        if (!hasData.current) {
          setError('Request timeout - the server may be busy');
        }
      } else if (!hasData.current) {
        setError('Failed to fetch downloads from API');
      }
      if (showLoading) {
        setLoading(false);
      }
    } finally {
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

    // Debounced handler that respects user's refresh rate setting
    // All data comes from the database via API to ensure consistency with stats
    const handleDataRefresh = () => {
      // Clear any pending refresh to debounce rapid events
      if (pendingRefreshRef.current) {
        clearTimeout(pendingRefreshRef.current);
      }

      // Debounce: wait 100ms for more events before processing
      pendingRefreshRef.current = setTimeout(() => {
        const maxRefreshRate = getRefreshIntervalRef.current();
        const now = Date.now();
        const timeSinceLastRefresh = now - lastSignalRRefresh.current;

        // User's setting controls max refresh rate
        // LIVE mode (0) = minimum 500ms to prevent UI thrashing
        const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

        if (timeSinceLastRefresh >= minInterval) {
          lastSignalRRefresh.current = now;
          fetchDownloads();
        }
        pendingRefreshRef.current = null;
      }, 100);
    };

    // Handler for database reset completion
    const handleDatabaseResetProgress = (event: { status?: string }) => {
      const status = (event.status || '').toLowerCase();
      if (status === 'completed' || status === 'complete' || status === 'done') {
        setTimeout(() => fetchDownloads(), 500);
      }
    };

    // Subscribe to events - all data comes from database via API for consistency
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    SIGNALR_REFRESH_EVENTS.forEach(event => signalR.on(event, handleDataRefresh));

    return () => {
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      SIGNALR_REFRESH_EVENTS.forEach(event => signalR.off(event, handleDataRefresh));
      if (pendingRefreshRef.current) {
        clearTimeout(pendingRefreshRef.current);
        pendingRefreshRef.current = null;
      }
    };
  }, [mockMode, signalR, fetchDownloads]);

  // ============================================
  // PAGE VISIBILITY - Refresh when tab becomes visible
  // ============================================


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

  // Time range changes
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      fetchDownloads({ showLoading: true });
    }
  }, [timeRange, mockMode, fetchDownloads]);

  // Event filter changes - refetch when event filter is changed
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      fetchDownloads({ showLoading: true });
    }
  }, [selectedEventId, mockMode, fetchDownloads]);

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
      if (lastCustomDates.start !== null || lastCustomDates.end !== null) {
        setLastCustomDates({ start: null, end: null });
      }
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
