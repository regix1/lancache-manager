import React, { createContext, useContext, useState, useEffect, useRef, useCallback, useMemo } from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import MockDataService from '../../test/mockData.service';
import { useTimeFilter } from '../TimeFilterContext';
import { useRefreshRate } from '../RefreshRateContext';
import { useSignalR } from '../SignalRContext';
import { SIGNALR_REFRESH_EVENTS } from '../SignalRContext/types';
import type { CacheInfo, ClientStatWithGroup, ServiceStat, DashboardStats } from '../../types';
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
  const { getRefreshInterval } = useRefreshRate();
  const signalR = useSignalR();

  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(null);
  const [clientStats, setClientStats] = useState<ClientStatWithGroup[]>([]);
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
  const pendingRefreshRef = useRef<NodeJS.Timeout | null>(null);

  // IMPORTANT: These refs are updated on every render BEFORE effects run
  // This ensures that any function reading from these refs gets the current value
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getRefreshIntervalRef = useRef(getRefreshInterval);
  const mockModeRef = useRef(mockMode);

  // Update refs synchronously on every render
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getRefreshIntervalRef.current = getRefreshInterval;
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
          setDashboardStats(dashboard.value);
          hasData.current = true;
        }
      }
      setError(null);
      if (showLoading) {
        setLoading(false);
      }
    } catch (err: unknown) {
      if (!hasData.current && !isAbortError(err)) {
        if (isAbortError(err)) {
          setError('Request timeout - the server may be busy');
        } else {
          setError('Failed to fetch stats from API');
        }
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

  // Public refresh function for manual refreshes
  const refreshStats = useCallback(async () => {
    await fetchStats({ showLoading: true });
  }, [fetchStats]);

  // Subscribe to SignalR events for real-time updates
  // IMPORTANT: Handlers read from refs, NOT closures - no stale data possible
  useEffect(() => {
    if (mockMode) return;

    // Debounced handler that respects user's refresh rate setting
    // This replaces polling - SignalR events are the only source of updates
    const handleRefreshEvent = () => {
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
          fetchStats();
        }
        pendingRefreshRef.current = null;
      }, 100);
    };

    // Handler for database reset completion - always refresh immediately
    const handleDatabaseResetProgress = (payload: { status?: string }) => {
      const status = (payload.status || '').toLowerCase();
      if (status === 'completed' || status === 'complete' || status === 'done') {
        setTimeout(() => fetchStats(), 500);
      }
    };

    // Subscribe to all refresh events using centralized array
    SIGNALR_REFRESH_EVENTS.forEach(event => signalR.on(event, handleRefreshEvent));
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      SIGNALR_REFRESH_EVENTS.forEach(event => signalR.off(event, handleRefreshEvent));
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      if (pendingRefreshRef.current) {
        clearTimeout(pendingRefreshRef.current);
        pendingRefreshRef.current = null;
      }
    };
  }, [mockMode, signalR, fetchStats]);

  // Page visibility - refresh when tab becomes visible
  useEffect(() => {
    if (mockMode) return;

    const handleVisibilityChange = () => {
      if (!document.hidden) {
        // Tab became visible - trigger immediate refresh
        // Reset the last refresh time to allow immediate fetch
        lastSignalRRefresh.current = 0;
        fetchStats();
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [mockMode, fetchStats]);

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

  // Handle time range changes - fetch new data
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      // Don't clear stats - let new data atomically replace old data
      // This allows AnimatedValue to smoothly transition from old values to new values
      // The Dashboard's periodMatchesTimeRange validation prevents showing mismatched data
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

  // Memoize context value to prevent unnecessary re-renders of consumers
  const value = useMemo(() => ({
    cacheInfo,
    clientStats,
    serviceStats,
    dashboardStats,
    loading,
    error,
    connectionStatus,
    refreshStats,
    updateStats
  }), [cacheInfo, clientStats, serviceStats, dashboardStats, loading, error, connectionStatus, refreshStats, updateStats]);

  return <StatsContext.Provider value={value}>{children}</StatsContext.Provider>;
};
