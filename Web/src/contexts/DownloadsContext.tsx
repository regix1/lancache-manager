import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  useRef,
  useCallback,
  type ReactNode
} from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import MockDataService from '../test/mockData.service';
import { useTimeFilter } from './TimeFilterContext';
import { usePollingRate } from './PollingRateContext';
import { useSignalR } from './SignalRContext';
import type { Download } from '../types';

interface DownloadsContextType {
  activeDownloads: Download[];
  latestDownloads: Download[];
  loading: boolean;
  error: string | null;
  refreshDownloads: () => Promise<void>;
  updateDownloads: (updater: {
    activeDownloads?: (prev: Download[]) => Download[];
    latestDownloads?: (prev: Download[]) => Download[];
  }) => void;
}

const DownloadsContext = createContext<DownloadsContextType | undefined>(undefined);

export const useDownloads = () => {
  const context = useContext(DownloadsContext);
  if (!context) {
    throw new Error('useDownloads must be used within DownloadsProvider');
  }
  return context;
};

interface DownloadsProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

export const DownloadsProvider: React.FC<DownloadsProviderProps> = ({
  children,
  mockMode = false
}) => {
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate } = useTimeFilter();
  const { getPollingInterval } = usePollingRate();
  const signalR = useSignalR();

  const [activeDownloads, setActiveDownloads] = useState<Download[]>([]);
  const [latestDownloads, setLatestDownloads] = useState<Download[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [lastCustomDates, setLastCustomDates] = useState<{ start: Date | null; end: Date | null }>({
    start: null,
    end: null
  });

  const isInitialLoad = useRef(true);
  const hasData = useRef(false);
  const fetchInProgress = useRef(false);
  const abortControllerRef = useRef<AbortController | null>(null);
  const lastFetchTime = useRef<number>(0);
  const lastSignalRRefresh = useRef<number>(0);
  const pollingIntervalRef = useRef<NodeJS.Timeout | null>(null);

  // IMPORTANT: These refs are updated on every render BEFORE effects run
  // This ensures that any function reading from these refs gets the current value
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getPollingIntervalRef = useRef(getPollingInterval);
  const mockModeRef = useRef(mockMode);

  // Update refs synchronously on every render
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getPollingIntervalRef.current = getPollingInterval;
  mockModeRef.current = mockMode;

  // Single unified fetch function that ALWAYS reads current timeRange from ref
  // This eliminates all stale closure issues - no timeRange is captured in closures
  const fetchDownloads = useCallback(async (options: { showLoading?: boolean; isInitial?: boolean } = {}) => {
    if (mockModeRef.current) return;

    const { showLoading = false, isInitial = false } = options;

    // Debounce rapid calls (min 250ms between fetches)
    const now = Date.now();
    if (!isInitial && now - lastFetchTime.current < 250) {
      return;
    }
    lastFetchTime.current = now;

    // Prevent concurrent fetches (except for initial load)
    if (fetchInProgress.current && !isInitial) {
      return;
    }
    fetchInProgress.current = true;

    // Read current values from refs - these are always up-to-date
    const { startTime, endTime } = getTimeRangeParamsRef.current();

    // Abort any in-flight request
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    abortControllerRef.current = new AbortController();

    try {
      if (showLoading) {
        setLoading(true);
      }

      const timeout = 10000;
      const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

      const [active, latest] = await Promise.allSettled([
        ApiService.getActiveDownloads(abortControllerRef.current.signal),
        ApiService.getLatestDownloads(
          abortControllerRef.current.signal,
          'unlimited',
          startTime,
          endTime
        )
      ]);

      clearTimeout(timeoutId);

      if (active.status === 'fulfilled' && active.value !== undefined) {
        setActiveDownloads(active.value);
      }
      if (latest.status === 'fulfilled' && latest.value !== undefined) {
        setLatestDownloads(latest.value);
        hasData.current = true;
      }

      setError(null);
    } catch (err: unknown) {
      if (!hasData.current && !isAbortError(err)) {
        if (isAbortError(err)) {
          setError('Request timeout - the server may be busy');
        } else {
          setError('Failed to fetch downloads from API');
        }
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

  // Public refresh function for manual refreshes
  const refreshDownloads = useCallback(async () => {
    await fetchDownloads({ showLoading: true });
  }, [fetchDownloads]);

  // Subscribe to SignalR events for real-time updates
  // IMPORTANT: Handlers read from refs, NOT closures - no stale data possible
  useEffect(() => {
    if (mockMode) return;

    // Handler that respects polling rate (or instant if Live mode)
    const handleRefreshEvent = () => {
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

    // Handler for database reset completion - always refresh immediately
    const handleDatabaseResetProgress = (payload: { status?: string }) => {
      const status = (payload.status || '').toLowerCase();
      if (status === 'completed' || status === 'complete' || status === 'done') {
        setTimeout(() => fetchDownloads(), 500);
      }
    };

    // Immediate fetch handler for user-initiated actions
    const handleImmediateRefresh = () => fetchDownloads();

    // Events that trigger data refresh (throttled by polling rate, or instant if Live)
    const refreshEvents = ['DownloadsRefresh', 'FastProcessingComplete'];

    // Events that should always trigger immediate refresh (user-initiated actions)
    const immediateRefreshEvents = [
      'DepotMappingComplete',
      'LogRemovalComplete',
      'CorruptionRemovalComplete',
      'ServiceRemovalComplete',
      'GameDetectionComplete',
      'GameRemovalComplete',
      'CacheClearComplete'
    ];

    refreshEvents.forEach(event => signalR.on(event, handleRefreshEvent));
    immediateRefreshEvents.forEach(event => signalR.on(event, handleImmediateRefresh));
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      refreshEvents.forEach(event => signalR.off(event, handleRefreshEvent));
      immediateRefreshEvents.forEach(event => signalR.off(event, handleImmediateRefresh));
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
    };
  }, [mockMode, signalR, fetchDownloads]);

  // Load mock data when mock mode is enabled
  useEffect(() => {
    if (mockMode) {
      setLoading(true);

      const mockData = MockDataService.generateMockData('unlimited');
      setActiveDownloads(mockData.activeDownloads);
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

  // Polling interval - fetch data at user-configured rate
  // Skipped in Live mode (0) since SignalR handles real-time updates
  const currentPollingInterval = getPollingInterval();
  useEffect(() => {
    if (mockMode) return;

    // Clear any existing polling interval
    if (pollingIntervalRef.current) {
      clearInterval(pollingIntervalRef.current);
      pollingIntervalRef.current = null;
    }

    // Live mode (0) = no polling needed, SignalR handles updates
    if (currentPollingInterval === 0) {
      return;
    }

    // Set up polling at the user's configured rate
    const setupPolling = () => {
      pollingIntervalRef.current = setInterval(() => {
        fetchDownloads();
      }, currentPollingInterval);
    };

    // Start polling after initial load completes
    if (!isInitialLoad.current) {
      setupPolling();
    } else {
      // Wait for initial load to complete, then start polling
      const checkAndSetup = setInterval(() => {
        if (!isInitialLoad.current) {
          clearInterval(checkAndSetup);
          setupPolling();
        }
      }, 100);

      return () => {
        clearInterval(checkAndSetup);
        if (pollingIntervalRef.current) {
          clearInterval(pollingIntervalRef.current);
          pollingIntervalRef.current = null;
        }
      };
    }

    return () => {
      if (pollingIntervalRef.current) {
        clearInterval(pollingIntervalRef.current);
        pollingIntervalRef.current = null;
      }
    };
  }, [mockMode, fetchDownloads, currentPollingInterval]);

  // Handle time range changes - fetch new data when timeRange changes
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      fetchDownloads({ showLoading: true });
    }
  }, [timeRange, mockMode, fetchDownloads]);

  // Debounced custom date changes
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
  }, [customStartDate, customEndDate, timeRange, mockMode, fetchDownloads]);

  const updateDownloads = useCallback((updater: {
    activeDownloads?: (prev: Download[]) => Download[];
    latestDownloads?: (prev: Download[]) => Download[];
  }) => {
    if (updater.activeDownloads) {
      setActiveDownloads(updater.activeDownloads);
    }
    if (updater.latestDownloads) {
      setLatestDownloads(updater.latestDownloads);
    }
  }, []);

  const value = {
    activeDownloads,
    latestDownloads,
    loading,
    error,
    refreshDownloads,
    updateDownloads
  };

  return <DownloadsContext.Provider value={value}>{children}</DownloadsContext.Provider>;
};
