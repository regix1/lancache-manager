import React, { createContext, useContext, useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { unstable_batchedUpdates } from 'react-dom';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import MockDataService from '../../test/mockData.service';
import { useTimeFilter } from '../TimeFilterContext';
import { useRefreshRate } from '../RefreshRateContext';
import { useSignalR } from '../SignalRContext';
import { useAuth } from '../AuthContext';
import { SIGNALR_REFRESH_EVENTS } from '../SignalRContext/types';
import type { CacheInfo, ClientStat, ServiceStat, DashboardStats, Download } from '../../types';
import type { DashboardDataContextType, DashboardDataProviderProps } from './types';

const DashboardDataContext = createContext<DashboardDataContextType | undefined>(undefined);

export const useDashboardData = () => {
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useDashboardData must be used within DashboardDataProvider');
  }
  return context;
};

// Compatibility exports for gradual migration
export const useStats = () => {
  const context = useDashboardData();
  return {
    cacheInfo: context.cacheInfo,
    clientStats: context.clientStats,
    serviceStats: context.serviceStats,
    dashboardStats: context.dashboardStats,
    loading: context.loading,
    error: context.error,
    connectionStatus: context.connectionStatus,
    refreshStats: context.refreshData,
    updateStats: (updater: {
      cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
      clientStats?: (prev: ClientStat[]) => ClientStat[];
      serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
      dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
    }) => context.updateData(updater)
  };
};

export const useDownloads = () => {
  const context = useDashboardData();
  return {
    latestDownloads: context.latestDownloads,
    loading: context.loading,
    error: context.error,
    refreshDownloads: async () => context.refreshData(true),
    updateDownloads: (updater: {
      latestDownloads?: (prev: Download[]) => Download[];
    }) => context.updateData(updater)
  };
};

export const DashboardDataProvider: React.FC<DashboardDataProviderProps> = ({ children, mockMode = false }) => {
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate, selectedEventIds } = useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  const signalR = useSignalR();
  const { isAuthenticated, authMode, isLoading: authLoading } = useAuth();
  const hasAccess = isAuthenticated || authMode === 'guest';

  // State
  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(null);
  const [clientStats, setClientStats] = useState<ClientStat[]>([]);
  const [serviceStats, setServiceStats] = useState<ServiceStat[]>([]);
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(null);
  const [latestDownloads, setLatestDownloads] = useState<Download[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [connectionStatus, setConnectionStatus] = useState('checking');

  const [lastCustomDates, setLastCustomDates] = useState<{ start: Date | null; end: Date | null }>({
    start: null,
    end: null
  });

  // Refs for tracking state
  const isInitialLoad = useRef(true);
  const hasData = useRef(false);
  const fetchInProgress = useRef(false);
  const abortControllerRef = useRef<AbortController | null>(null);
  const lastFetchTime = useRef<number>(0);
  const lastSignalRRefresh = useRef<number>(0);
  const prevEventIdsRef = useRef<string>(JSON.stringify(selectedEventIds));
  const currentRequestIdRef = useRef(0);

  // IMPORTANT: These refs are updated on every render BEFORE effects run
  // This ensures that any function reading from these refs gets the current value
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getRefreshIntervalRef = useRef(getRefreshInterval);
  const mockModeRef = useRef(mockMode);
  const selectedEventIdsRef = useRef<number[]>(selectedEventIds);
  const authLoadingRef = useRef(authLoading);
  const hasAccessRef = useRef(hasAccess);

  // Update refs synchronously on every render
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getRefreshIntervalRef.current = getRefreshInterval;
  mockModeRef.current = mockMode;
  selectedEventIdsRef.current = selectedEventIds;
  authLoadingRef.current = authLoading;
  hasAccessRef.current = hasAccess;

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

  // Single unified fetch function that fetches all data in parallel
  const fetchAllData = useCallback(async (options: { showLoading?: boolean; isInitial?: boolean; forceRefresh?: boolean; trigger?: string } = {}) => {
    if (mockModeRef.current) return;
    if (authLoadingRef.current || !hasAccessRef.current) return;

    const { showLoading = false, isInitial = false, forceRefresh = false, trigger = 'unknown' } = options;

    // Debounce rapid calls (min 250ms between fetches) - skip for initial load or force refresh
    const now = Date.now();
    if (!isInitial && !forceRefresh && now - lastFetchTime.current < 250) {
      return;
    }
    lastFetchTime.current = now;

    // Abort any in-flight request BEFORE checking concurrent flag
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }

    // Prevent concurrent fetches (except for initial load or force refresh)
    if (fetchInProgress.current && !isInitial && !forceRefresh) {
      return;
    }
    fetchInProgress.current = true;

    // Generate unique request ID - only this request can modify state
    const thisRequestId = ++currentRequestIdRef.current;

    // Read current values from refs - these are always up-to-date
    // IMPORTANT: Capture these at fetch start to detect stale data when fetch completes
    const currentTimeRange = currentTimeRangeRef.current;
    const currentEventIds = [...selectedEventIdsRef.current]; // Copy to detect changes
    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const eventIds = currentEventIds.length > 0 ? currentEventIds : undefined;
    const cacheBust = forceRefresh ? Date.now() : undefined;

    abortControllerRef.current = new AbortController();
    const signal = abortControllerRef.current.signal;

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

      // Fetch all data in parallel using Promise.allSettled
      const [cache, clients, services, dashboard, downloads] = await Promise.allSettled([
        ApiService.getCacheInfo(signal),
        ApiService.getClientStats(signal, startTime, endTime, eventIds, undefined, cacheBust),
        ApiService.getServiceStats(signal, startTime, endTime, eventIds, cacheBust),
        ApiService.getDashboardStats(signal, startTime, endTime, eventIds, cacheBust),
        ApiService.getLatestDownloads(signal, 'unlimited', startTime, endTime, eventIds, cacheBust)
      ]);

      clearTimeout(timeoutId);

      // CRITICAL: Check if we're still the current request before modifying ANY state
      if (currentRequestIdRef.current !== thisRequestId) {
        return; // A newer request has started, don't touch state
      }

      // Only apply results if filters haven't changed during fetch (prevents stale data)
      const timeRangeStillValid = currentTimeRangeRef.current === currentTimeRange;
      const eventIdsStillValid = JSON.stringify(selectedEventIdsRef.current) === JSON.stringify(currentEventIds);
      const filtersStillValid = timeRangeStillValid && eventIdsStillValid;

      // Batch all state updates to prevent multiple re-renders
      unstable_batchedUpdates(() => {
        // Cache info is not time-range dependent, always apply
        if (cache.status === 'fulfilled' && cache.value !== undefined) {
          setCacheInfo(cache.value);
        }

        // All other data depends on time range AND event filter
        if (filtersStillValid) {
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
          if (downloads.status === 'fulfilled' && downloads.value !== undefined) {
            setLatestDownloads(downloads.value);
          }
          setError(null);
          if (showLoading) {
            setLoading(false);
          }
        }
        // If filters are invalid, DON'T change loading - let the next correct fetch handle it
      });
    } catch (err: unknown) {
      // Check if we're still the current request before setting error state
      if (currentRequestIdRef.current !== thisRequestId) {
        return; // A newer request has started, don't touch state
      }
      if (!hasData.current && !isAbortError(err)) {
        setError('Failed to fetch dashboard data from API');
      }
      if (showLoading) {
        setLoading(false);
      }
    } finally {
      // Only update fetchInProgress if we're still the current request
      if (currentRequestIdRef.current === thisRequestId) {
        if (isInitial) {
          isInitialLoad.current = false;
        }
        fetchInProgress.current = false;
      }
    }
  }, []);

  // Public refresh function for manual refreshes
  const refreshData = useCallback(async (forceRefresh: boolean = false) => {
    await fetchAllData({ showLoading: true, forceRefresh });
  }, [fetchAllData]);

  // Subscribe to SignalR events for real-time updates - SINGLE subscription
  useEffect(() => {
    if (mockMode) return;

    // Throttled handler that respects user's refresh rate setting (500ms minimum in live mode)
    const handleRefreshEvent = (eventName?: string) => {
      const currentRange = currentTimeRangeRef.current;
      const maxRefreshRate = getRefreshIntervalRef.current();
      const now = Date.now();
      const timeSinceLastRefresh = now - lastSignalRRefresh.current;

      // User's setting controls max refresh rate
      // LIVE mode (0) = 500ms minimum to prevent UI thrashing
      const minInterval = maxRefreshRate === 0 ? 500 : maxRefreshRate;

      // For historical ranges (not 'live'), skip SignalR refreshes to prevent flickering
      const isLiveMode = currentRange === 'live';

      // Only refresh in live mode - historical ranges should not react to real-time events
      if (isLiveMode && timeSinceLastRefresh >= minInterval) {
        lastSignalRRefresh.current = now;
        fetchAllData({ trigger: `signalr:${eventName || 'unknown'}` });
      }
    };

    // Handler for database reset completion - always refresh immediately
    const handleDatabaseResetProgress = (event: { status?: string }) => {
      const status = (event.status || '').toLowerCase();
      if (status === 'completed') {
        setTimeout(() => fetchAllData({ trigger: 'signalr:DatabaseResetCompleted' }), 500);
      }
    };

    // Create stable handler references for proper cleanup
    const eventHandlers: Record<string, () => void> = {};
    SIGNALR_REFRESH_EVENTS.forEach(event => {
      eventHandlers[event] = () => handleRefreshEvent(event);
      signalR.on(event, eventHandlers[event]);
    });
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      // Use the same handler references for cleanup
      SIGNALR_REFRESH_EVENTS.forEach(event => {
        signalR.off(event, eventHandlers[event]);
      });
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
    };
  }, [mockMode, signalR, fetchAllData]);

  // Load mock data when mock mode is enabled
  useEffect(() => {
    if (mockMode) {
      const mockData = MockDataService.generateMockData('unlimited');

      // Batch all state updates to prevent multiple re-renders
      unstable_batchedUpdates(() => {
        setLoading(true);
        setConnectionStatus('connected');
        setCacheInfo(mockData.cacheInfo);
        setClientStats(mockData.clientStats);
        setServiceStats(mockData.serviceStats);
        setDashboardStats(mockData.dashboardStats);
        setLatestDownloads(mockData.latestDownloads);
        setError(null);
        setLoading(false);
      });

      hasData.current = true;
      isInitialLoad.current = false;
    }
  }, [mockMode]);

  // Initial load
  useEffect(() => {
    if (!mockMode && !authLoading && hasAccess) {
      fetchAllData({ showLoading: true, isInitial: true, trigger: 'initial' });
    }

    return () => {
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, authLoading, hasAccess, fetchAllData]);

  // Handle time range changes - fetch new data
  useEffect(() => {
    if (!mockMode && hasAccess && !isInitialLoad.current) {
      // Use forceRefresh to bypass debounce - time range changes should always trigger immediate fetch
      // Only show loading if we don't have existing data to prevent UI flashing
      fetchAllData({ showLoading: !hasData.current, forceRefresh: true, trigger: `timeRangeChange:${timeRange}` });
    }
  }, [timeRange, mockMode, hasAccess, fetchAllData]);

  // Event filter changes - refetch when event filter is changed
  useEffect(() => {
    const currentEventIdsKey = JSON.stringify(selectedEventIds);
    if (!mockMode && hasAccess && prevEventIdsRef.current !== currentEventIdsKey) {
      prevEventIdsRef.current = currentEventIdsKey;
      // Keep previous data visible during fetch - don't clear immediately
      // Only show loading if we don't have existing data to prevent UI flashing
      fetchAllData({ showLoading: !hasData.current, forceRefresh: true, trigger: 'eventFilterChange' });
    }
  }, [selectedEventIds, mockMode, hasAccess, fetchAllData]);

  // Custom date changes - immediate fetch, no debounce
  useEffect(() => {
    if (timeRange === 'custom' && !mockMode && hasAccess) {
      if (customStartDate && customEndDate) {
        const datesChanged =
          lastCustomDates.start?.getTime() !== customStartDate.getTime() ||
          lastCustomDates.end?.getTime() !== customEndDate.getTime();

        if (datesChanged) {
          setLastCustomDates({ start: customStartDate, end: customEndDate });
          // Only show loading if we don't have existing data to prevent UI flashing
          fetchAllData({ showLoading: !hasData.current, forceRefresh: true, trigger: 'customDateChange' });
        }
      }
    } else if (timeRange !== 'custom') {
      setLastCustomDates({ start: null, end: null });
    }
  }, [customStartDate, customEndDate, timeRange, mockMode, hasAccess, fetchAllData]);

  const updateData = useCallback((updater: {
    cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
    clientStats?: (prev: ClientStat[]) => ClientStat[];
    serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
    dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
    latestDownloads?: (prev: Download[]) => Download[];
  }) => {
    // Batch all state updates to prevent multiple re-renders
    unstable_batchedUpdates(() => {
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
      if (updater.latestDownloads) {
        setLatestDownloads(updater.latestDownloads);
      }
    });
  }, []);

  // Memoize context value to prevent unnecessary re-renders of consumers
  const value = useMemo(() => ({
    cacheInfo,
    clientStats,
    serviceStats,
    dashboardStats,
    latestDownloads,
    loading,
    error,
    connectionStatus,
    refreshData,
    updateData
  }), [
    cacheInfo,
    clientStats,
    serviceStats,
    dashboardStats,
    latestDownloads,
    loading,
    error,
    connectionStatus,
    refreshData,
    updateData
  ]);

  return <DashboardDataContext.Provider value={value}>{children}</DashboardDataContext.Provider>;
};
