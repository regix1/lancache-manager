import React, { createContext, useContext, useState, useEffect, useRef, useCallback } from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import MockDataService from '../../test/mockData.service';
import { useTimeFilter } from '../TimeFilterContext';
import { usePollingRate } from '../PollingRateContext';
import { useSignalR } from '../SignalRContext';
import type { CacheInfo, ClientStat, ServiceStat, DashboardStats } from '../../types';
import type { StatsContextType, StatsProviderProps } from './types';

const StatsContext = createContext<StatsContextType | undefined>(undefined);

export const useStats = () => {
  const context = useContext(StatsContext);
  if (!context) {
    throw new Error('useStats must be used within StatsProvider');
  }
  return context;
};

export const StatsProvider: React.FC<StatsProviderProps> = ({ children, mockMode = false }) => {
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate } = useTimeFilter();
  const { getPollingInterval } = usePollingRate();
  const signalR = useSignalR();

  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(null);
  const [clientStats, setClientStats] = useState<ClientStat[]>([]);
  const [serviceStats, setServiceStats] = useState<ServiceStat[]>([]);
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [connectionStatus, setConnectionStatus] = useState('checking');

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
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getPollingIntervalRef = useRef(getPollingInterval);
  const mockModeRef = useRef(mockMode);

  // Update refs synchronously on every render
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getPollingIntervalRef.current = getPollingInterval;
  mockModeRef.current = mockMode;

  const getApiUrl = (): string => {
    if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
      return import.meta.env.VITE_API_URL;
    }
    return '';
  };

  const checkConnectionStatus = async () => {
    if (mockModeRef.current) {
      setConnectionStatus('connected');
      return true;
    }

    try {
      const apiUrl = getApiUrl();
      const response = await fetch(`${apiUrl}/health`, {
        signal: AbortSignal.timeout(5000)
      });
      if (response.ok) {
        setConnectionStatus('connected');
        return true;
      }
      setConnectionStatus('error');
      return false;
    } catch (err) {
      setConnectionStatus('disconnected');
      return false;
    }
  };

  // Single unified fetch function that ALWAYS reads current timeRange from ref
  // This eliminates all stale closure issues - no timeRange is captured in closures
  const fetchStats = useCallback(async (options: { showLoading?: boolean; isInitial?: boolean; forceRefresh?: boolean } = {}) => {
    if (mockModeRef.current) return;

    const { showLoading = false, isInitial = false, forceRefresh = false } = options;

    // Debounce rapid calls (min 250ms between fetches) - skip for initial load or force refresh
    const now = Date.now();
    if (!isInitial && !forceRefresh && now - lastFetchTime.current < 250) {
      return;
    }
    lastFetchTime.current = now;

    // Abort any in-flight request BEFORE checking concurrent flag
    // This ensures time range changes always trigger new fetches
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }

    // Prevent concurrent fetches (except for initial load or force refresh which should always proceed)
    if (fetchInProgress.current && !isInitial && !forceRefresh) {
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

      const isConnected = await checkConnectionStatus();
      if (!isConnected) {
        if (!hasData.current) {
          setError('Cannot connect to API server');
        }
        return;
      }

      const timeout = 10000;
      const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

      const [cache, clients, services, dashboard] = await Promise.allSettled([
        ApiService.getCacheInfo(abortControllerRef.current.signal),
        ApiService.getClientStats(abortControllerRef.current.signal, startTime, endTime),
        ApiService.getServiceStats(abortControllerRef.current.signal, startTime, endTime),
        ApiService.getDashboardStats(abortControllerRef.current.signal, startTime, endTime)
      ]);

      clearTimeout(timeoutId);

      // Only apply time-range-dependent results if timeRange hasn't changed during fetch
      const timeRangeStillValid = currentTimeRangeRef.current === currentTimeRange;

      // Cache info is not time-range dependent, always apply
      if (cache.status === 'fulfilled' && cache.value !== undefined) {
        setCacheInfo(cache.value);
      }
      // Client/service/dashboard stats are time-range dependent
      if (timeRangeStillValid) {
        if (clients.status === 'fulfilled' && clients.value !== undefined) {
          setClientStats(clients.value);
        }
        if (services.status === 'fulfilled' && services.value !== undefined) {
          setServiceStats(services.value);
        }
        if (dashboard.status === 'fulfilled' && dashboard.value !== undefined) {
          // DEBUG: Log the raw API response
          console.log('DEBUG StatsContext API response:', {
            currentTimeRange,
            startTime,
            endTime,
            'period.duration': dashboard.value.period?.duration,
            'period.bandwidthSaved': dashboard.value.period?.bandwidthSaved,
            'totalBandwidthSaved': dashboard.value.totalBandwidthSaved
          });
          setDashboardStats(dashboard.value);
          hasData.current = true;
        }
      }

      setError(null);
    } catch (err: unknown) {
      if (!hasData.current && !isAbortError(err)) {
        if (isAbortError(err)) {
          setError('Request timeout - the server may be busy');
        } else {
          setError('Failed to fetch stats from API');
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
  const refreshStats = useCallback(async () => {
    await fetchStats({ showLoading: true });
  }, [fetchStats]);

  // Subscribe to SignalR events for real-time updates
  // IMPORTANT: Handlers read from refs, NOT closures - no stale data possible
  useEffect(() => {
    if (mockMode) return;

    // Handler that respects polling rate (or instant if Live mode)
    const handleRefreshEvent = () => {
      const pollingInterval = getPollingIntervalRef.current();

      // Live mode (0) = instant updates, no throttling
      if (pollingInterval === 0) {
        fetchStats();
        return;
      }

      // Throttle based on polling interval
      const now = Date.now();
      const timeSinceLastRefresh = now - lastSignalRRefresh.current;
      if (timeSinceLastRefresh >= pollingInterval) {
        lastSignalRRefresh.current = now;
        fetchStats();
      }
    };

    // Handler for database reset completion - always refresh immediately
    const handleDatabaseResetProgress = (payload: { status?: string }) => {
      const status = (payload.status || '').toLowerCase();
      if (status === 'completed' || status === 'complete' || status === 'done') {
        setTimeout(() => fetchStats(), 500);
      }
    };

    // Immediate fetch handler for user-initiated actions
    const handleImmediateRefresh = () => fetchStats();

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
  }, [mockMode, signalR, fetchStats]);

  // Load mock data when mock mode is enabled
  useEffect(() => {
    if (mockMode) {
      setLoading(true);
      setConnectionStatus('connected');

      const mockData = MockDataService.generateMockData('unlimited');
      setCacheInfo(mockData.cacheInfo);
      setClientStats(mockData.clientStats);
      setServiceStats(mockData.serviceStats);
      setDashboardStats(mockData.dashboardStats);
      setError(null);
      setLoading(false);
      hasData.current = true;
      isInitialLoad.current = false;
    }
  }, [mockMode]);

  // Initial load
  useEffect(() => {
    if (!mockMode) {
      fetchStats({ showLoading: true, isInitial: true });
    }

    return () => {
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, fetchStats]);

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
        fetchStats();
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
  }, [mockMode, fetchStats, currentPollingInterval]);

  // Handle time range changes - fetch new data
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      // IMPORTANT: Clear stats immediately when time range changes to prevent showing
      // stale data with wrong time range label. This shows loading state until new data arrives.
      setDashboardStats(null);
      setClientStats([]);
      setServiceStats([]);

      // Use forceRefresh to bypass debounce - time range changes should always trigger immediate fetch
      fetchStats({ showLoading: true, forceRefresh: true });
    }
  }, [timeRange, mockMode, fetchStats]);

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
            fetchStats({ showLoading: true });
          }, 50);

          return () => clearTimeout(debounceTimer);
        }
      }
    } else if (timeRange !== 'custom') {
      setLastCustomDates({ start: null, end: null });
    }
  }, [customStartDate, customEndDate, timeRange, mockMode, fetchStats]);

  const updateStats = useCallback((updater: {
    cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
    clientStats?: (prev: ClientStat[]) => ClientStat[];
    serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
    dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
  }) => {
    if (updater.cacheInfo) {
      setCacheInfo(updater.cacheInfo);
    }
    if (updater.clientStats) {
      setClientStats(updater.clientStats);
    }
    if (updater.serviceStats) {
      setServiceStats(updater.serviceStats);
    }
    if (updater.dashboardStats) {
      setDashboardStats(updater.dashboardStats);
    }
  }, []);

  const value = {
    cacheInfo,
    clientStats,
    serviceStats,
    dashboardStats,
    loading,
    error,
    connectionStatus,
    refreshStats,
    updateStats
  };

  return <StatsContext.Provider value={value}>{children}</StatsContext.Provider>;
};
