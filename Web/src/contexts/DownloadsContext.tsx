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
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getPollingIntervalRef = useRef(getPollingInterval);
  const mockModeRef = useRef(mockMode);
  const pollingIntervalRef = useRef<NodeJS.Timeout | null>(null);

  // Update refs on each render (no useEffect needed)
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getPollingIntervalRef.current = getPollingInterval;
  mockModeRef.current = mockMode;

  // Fetch downloads data (called by SignalR events)
  const fetchDownloads = useCallback(async () => {
    if (mockModeRef.current) return;

    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const now = Date.now();

    // Debounce rapid SignalR events (min 250ms between fetches)
    if (now - lastFetchTime.current < 250) {
      return;
    }
    lastFetchTime.current = now;

    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    abortControllerRef.current = new AbortController();

    try {
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

      if (active.status === 'fulfilled' && active.value !== undefined) {
        setActiveDownloads(active.value);
      }
      if (latest.status === 'fulfilled' && latest.value !== undefined) {
        setLatestDownloads(latest.value);
        hasData.current = true;
      }

      clearTimeout(timeoutId);
      setError(null);
    } catch (err: unknown) {
      if (!hasData.current && !isAbortError(err)) {
        setError('Failed to fetch downloads');
      }
    }
  }, []);

  // Combined refresh for initial load or manual refresh
  const refreshDownloads = useCallback(async () => {
    if (mockModeRef.current) return;

    const { startTime, endTime } = getTimeRangeParamsRef.current();

    if (fetchInProgress.current && !isInitialLoad.current) {
      return;
    }

    fetchInProgress.current = true;

    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    abortControllerRef.current = new AbortController();

    try {
      if (isInitialLoad.current) {
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

      if (active.status === 'fulfilled' && active.value !== undefined) {
        setActiveDownloads(active.value);
      }
      if (latest.status === 'fulfilled' && latest.value !== undefined) {
        setLatestDownloads(latest.value);
        hasData.current = true;
      }

      clearTimeout(timeoutId);
      setError(null);
    } catch (err: unknown) {
      if (!hasData.current) {
        if (isAbortError(err)) {
          setError('Request timeout - the server may be busy');
        } else {
          setError('Failed to fetch downloads from API');
        }
      }
    } finally {
      setLoading(false);
      if (isInitialLoad.current) {
        isInitialLoad.current = false;
      }
      fetchInProgress.current = false;
    }
  }, []);

  // Subscribe to SignalR events for real-time updates
  // These events respect the user's polling rate preference
  useEffect(() => {
    if (mockMode) return;

    // Track last SignalR-triggered refresh to respect polling rate
    let lastSignalRRefresh = 0;

    // Throttled handler that respects user's polling rate
    const throttledFetchDownloads = () => {
      const now = Date.now();
      const pollingInterval = getPollingIntervalRef.current();
      const timeSinceLastRefresh = now - lastSignalRRefresh;

      // Only fetch if enough time has passed according to polling rate
      if (timeSinceLastRefresh >= pollingInterval) {
        lastSignalRRefresh = now;
        fetchDownloads();
      }
    };

    // Handler for database reset completion - always refresh immediately
    const handleDatabaseResetProgress = (payload: { status?: string }) => {
      const status = (payload.status || '').toLowerCase();
      if (status === 'completed' || status === 'complete' || status === 'done') {
        // Delay slightly to let backend finish cleanup
        setTimeout(() => fetchDownloads(), 500);
      }
    };

    // Events that trigger data refresh (throttled by polling rate)
    const refreshEvents = [
      'DownloadsRefresh',
      'FastProcessingComplete'
    ];

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

    refreshEvents.forEach(event => signalR.on(event, throttledFetchDownloads));
    immediateRefreshEvents.forEach(event => signalR.on(event, fetchDownloads));
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      refreshEvents.forEach(event => signalR.off(event, throttledFetchDownloads));
      immediateRefreshEvents.forEach(event => signalR.off(event, fetchDownloads));
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
    };
  }, [mockMode, signalR, fetchDownloads]);

  // Load mock data when mock mode is enabled
  useEffect(() => {
    if (mockMode) {
      setLoading(true);

      // Generate mock data immediately
      const mockData = MockDataService.generateMockData('unlimited');
      setActiveDownloads(mockData.activeDownloads);
      setLatestDownloads(mockData.latestDownloads);
      setError(null);
      setLoading(false);
      hasData.current = true;
      isInitialLoad.current = false;
    }
  }, [mockMode]);

  // Initial load only - SignalR handles real-time updates
  useEffect(() => {
    if (!mockMode) {
      refreshDownloads();
    }

    return () => {
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, refreshDownloads]);

  // Polling interval - fetch data at user-configured rate
  useEffect(() => {
    if (mockMode) return;

    // Clear any existing polling interval
    if (pollingIntervalRef.current) {
      clearInterval(pollingIntervalRef.current);
      pollingIntervalRef.current = null;
    }

    // Set up polling at the user's configured rate
    const setupPolling = () => {
      const interval = getPollingIntervalRef.current();
      pollingIntervalRef.current = setInterval(() => {
        fetchDownloads();
      }, interval);
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
  }, [mockMode, fetchDownloads]);

  // Handle time range changes
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      setLoading(true);
      refreshDownloads();
    }
  }, [timeRange, mockMode, refreshDownloads]);

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
            refreshDownloads();
          }, 50);

          return () => clearTimeout(debounceTimer);
        }
      }
    } else if (timeRange !== 'custom') {
      setLastCustomDates({ start: null, end: null });
    }
  }, [customStartDate, customEndDate, timeRange, mockMode, refreshDownloads]);

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
