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
  const intervalRef = useRef<NodeJS.Timeout | null>(null);
  const lastFetchTime = useRef<number>(0);
  const lastSignalRRefreshTime = useRef<number>(0);
  const isEffectActive = useRef<boolean>(true);
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getPollingIntervalRef = useRef(getPollingInterval);
  const mockModeRef = useRef(mockMode);

  // Update refs on each render (no useEffect needed)
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getPollingIntervalRef.current = getPollingInterval;
  mockModeRef.current = mockMode;

  // Fetch downloads data
  const fetchDownloads = async () => {
    if (mockModeRef.current) return;

    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const now = Date.now();
    const debounceTime = Math.min(1000, Math.max(250, getPollingIntervalRef.current() / 4));

    if (!isInitialLoad.current && now - lastFetchTime.current < debounceTime) {
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
    } catch (err: any) {
      if (!hasData.current && err.name !== 'AbortError') {
        setError('Failed to fetch downloads');
      }
    }
  };

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
    } catch (err: any) {
      if (!hasData.current) {
        if (err.name === 'AbortError') {
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

  // Subscribe to SignalR DownloadsRefresh for real-time updates
  useEffect(() => {
    if (mockMode) return;

    const handleDownloadsRefresh = () => {
      const now = Date.now();
      const timeSinceLastRefresh = now - lastSignalRRefreshTime.current;
      const pollingInterval = getPollingIntervalRef.current();

      if (timeSinceLastRefresh < pollingInterval) {
        return;
      }

      lastSignalRRefreshTime.current = now;
      fetchDownloads();
    };

    signalR.on('DownloadsRefresh', handleDownloadsRefresh);

    return () => {
      signalR.off('DownloadsRefresh', handleDownloadsRefresh);
    };
  }, [mockMode, signalR]);

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

  // Initial load and polling interval
  useEffect(() => {
    if (!mockMode) {
      isEffectActive.current = true;

      // Clear existing interval
      if (intervalRef.current) clearInterval(intervalRef.current);

      // Initial load
      refreshDownloads().then(() => {
        if (!isEffectActive.current) return;

        // Clear existing interval (race condition handling)
        if (intervalRef.current) clearInterval(intervalRef.current);

        // Set up interval - use ref to get current polling interval
        const pollingInterval = getPollingIntervalRef.current();
        intervalRef.current = setInterval(fetchDownloads, pollingInterval);
      });
    }

    return () => {
      isEffectActive.current = false;
      if (intervalRef.current) clearInterval(intervalRef.current);
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, refreshDownloads]);

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
