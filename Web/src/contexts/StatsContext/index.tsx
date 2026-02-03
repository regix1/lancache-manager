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
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate, selectedEventIds, extendTimeAnchor } = useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  const signalR = useSignalR();
  const { isAuthenticated, authMode, isLoading: authLoading } = useAuth();
  const hasAccess = isAuthenticated || authMode === 'guest';

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
  // Track previous event IDs - initialize with current value to prevent double-fetch on mount
  const prevEventIdsRef = useRef<string>(JSON.stringify(selectedEventIds));
  // Request ID to prevent race conditions - only the most recent request can set state
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

  // Single unified fetch function that ALWAYS reads current timeRange from ref
  // This eliminates all stale closure issues - no timeRange is captured in closures
  const fetchStats = useCallback(async (options: { showLoading?: boolean; isInitial?: boolean; forceRefresh?: boolean; trigger?: string } = {}) => {
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
    // This ensures time range changes always trigger new fetches
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }

    // Prevent concurrent fetches (except for initial load or force refresh which should always proceed)
    if (fetchInProgress.current && !isInitial && !forceRefresh) {
      return;
    }
    fetchInProgress.current = true;

    // Generate unique request ID - only this request can modify state
    // This prevents race conditions when rapid filter changes cause overlapping requests
    const thisRequestId = ++currentRequestIdRef.current;

    // Read current values from refs - these are always up-to-date
    // IMPORTANT: Capture these at fetch start to detect stale data when fetch completes
    const currentTimeRange = currentTimeRangeRef.current;
    const currentEventIds = [...selectedEventIdsRef.current]; // Copy to detect changes
    const { startTime, endTime } = getTimeRangeParamsRef.current();
    // Support multiple event IDs - pass as array for API
    const eventIds = currentEventIds.length > 0 ? currentEventIds : undefined;
    const cacheBust = forceRefresh ? Date.now() : undefined;

    console.log(`%c[STATS FETCH] requestId=${thisRequestId}`, 'color: #2563eb; font-weight: bold', {
      trigger,
      timeRange: currentTimeRange,
      startTime,
      endTime,
      startDate: startTime ? new Date(startTime * 1000).toLocaleString() : 'none',
      endDate: endTime ? new Date(endTime * 1000).toLocaleString() : 'none',
      eventIds,
      forceRefresh,
      showLoading,
      isInitial,
      cacheBust: cacheBust ? 'yes' : 'no'
    });

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

      // Pass eventIds to filter to only tagged downloads when events are selected
      const [cache, clients, services, dashboard] = await Promise.allSettled([
        ApiService.getCacheInfo(abortControllerRef.current.signal),
        ApiService.getClientStats(abortControllerRef.current.signal, startTime, endTime, eventIds, undefined, cacheBust),
        ApiService.getServiceStats(abortControllerRef.current.signal, startTime, endTime, eventIds, cacheBust),
        ApiService.getDashboardStats(abortControllerRef.current.signal, startTime, endTime, eventIds, cacheBust)
      ]);

      clearTimeout(timeoutId);

      // CRITICAL: Check if we're still the current request before modifying ANY state
      // This prevents race conditions where an old (aborted) request sets loading=false
      // while a new request is still in progress
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
        // Client/service/dashboard stats depend on time range AND event filter
        if (filtersStillValid) {
          if (clients.status === 'fulfilled' && clients.value !== undefined) {
            setClientStats(clients.value);
          }
          if (services.status === 'fulfilled' && services.value !== undefined) {
            setServiceStats(services.value);
          }
          if (dashboard.status === 'fulfilled' && dashboard.value !== undefined) {
            console.log(`[STATS CONTEXT] New dashboard data for timeRange=${currentTimeRange}`, {
              period: dashboard.value.period?.duration,
              bandwidthSaved: dashboard.value.period?.bandwidthSaved,
              addedToCache: dashboard.value.period?.addedToCache,
              totalServed: dashboard.value.period?.totalServed,
              hitRatio: dashboard.value.period?.hitRatio,
              uniqueClients: dashboard.value.uniqueClients
            });
            setDashboardStats(dashboard.value);
            hasData.current = true;
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
        setError('Failed to fetch stats from API');
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
  const refreshStats = useCallback(async (forceRefresh: boolean = false) => {
    await fetchStats({ showLoading: true, forceRefresh });
  }, [fetchStats]);

  // Subscribe to SignalR events for real-time updates
  // IMPORTANT: Handlers read from refs, NOT closures - no stale data possible
  useEffect(() => {
    if (mockMode) return;

    // Throttled handler that respects user's refresh rate setting
    // This replaces polling - SignalR events are the only source of updates
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

      console.log(`%c[STATS SIGNALR] Event received`, 'color: #16a34a; font-weight: bold', {
        event: eventName || 'unknown',
        timeSinceLastRefresh,
        minInterval,
        currentTimeRange: currentRange,
        isLiveMode,
        willRefresh: isLiveMode && timeSinceLastRefresh >= minInterval
      });

      // Only refresh in live mode - historical ranges should not react to real-time events
      if (isLiveMode && timeSinceLastRefresh >= minInterval) {
        lastSignalRRefresh.current = now;
        fetchStats({ trigger: `signalr:${eventName || 'unknown'}` });
      }
    };

    // Handler for database reset completion - always refresh immediately
    const handleDatabaseResetProgress = (event: { status?: string }) => {
      const status = (event.status || '').toLowerCase();
      console.log(`%c[STATS SIGNALR] DatabaseResetProgress`, 'color: #16a34a; font-weight: bold', { status });
      if (status === 'completed') {
        setTimeout(() => fetchStats({ trigger: 'signalr:DatabaseResetCompleted' }), 500);
      }
    };

    // Subscribe to all refresh events using centralized array
    SIGNALR_REFRESH_EVENTS.forEach(event => signalR.on(event, () => handleRefreshEvent(event)));
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      SIGNALR_REFRESH_EVENTS.forEach(event => signalR.off(event, () => handleRefreshEvent(event)));
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
    };
  }, [mockMode, signalR, fetchStats]);

  // Page visibility - refresh when tab becomes visible
  // Mobile browsers pause SignalR when backgrounded, so we need to refresh on return
  useEffect(() => {
    if (mockMode) return;

    const handleVisibilityChange = () => {
      const isVisible = !document.hidden;
      const currentRange = currentTimeRangeRef.current;
      const isLiveMode = currentRange === 'live';

      console.log(`%c[STATS VISIBILITY] Tab visibility changed`, 'color: #ea580c; font-weight: bold', {
        isVisible,
        currentTimeRange: currentRange,
        isLiveMode,
        willRefresh: isVisible && isLiveMode
      });

      if (isVisible && isLiveMode) {
        // Page became visible - refresh data (only in live mode)
        setTimeout(() => {
          console.log(`%c[STATS VISIBILITY] Executing delayed refresh`, 'color: #ea580c; font-weight: bold', {
            currentTimeRange: currentTimeRangeRef.current
          });
          fetchStats({ showLoading: false, forceRefresh: true, trigger: 'visibility' });
        }, 500);
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
      const mockData = MockDataService.generateMockData('unlimited');

      // Batch all state updates to prevent multiple re-renders
      unstable_batchedUpdates(() => {
        setLoading(true);
        setConnectionStatus('connected');
        setCacheInfo(mockData.cacheInfo);
        setClientStats(mockData.clientStats);
        setServiceStats(mockData.serviceStats);
        setDashboardStats(mockData.dashboardStats);
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
      console.log(`%c[STATS EFFECT] Initial load triggered`, 'color: #0891b2; font-weight: bold');
      fetchStats({ showLoading: true, isInitial: true, trigger: 'initial' });
    }

    return () => {
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, authLoading, hasAccess, fetchStats]);

  // Handle time range changes - fetch new data
  useEffect(() => {
    if (!mockMode && hasAccess && !isInitialLoad.current) {
      console.log(`%c[STATS EFFECT] Time range changed`, 'color: #0891b2; font-weight: bold', {
        newTimeRange: timeRange
      });
      // Don't clear stats - let new data atomically replace old data
      // This allows AnimatedValue to smoothly transition from old values to new values
      // The Dashboard's periodMatchesTimeRange validation prevents showing mismatched data
      // Use forceRefresh to bypass debounce - time range changes should always trigger immediate fetch
      fetchStats({ showLoading: true, forceRefresh: true, trigger: `timeRangeChange:${timeRange}` });
    }
  }, [timeRange, mockMode, hasAccess, fetchStats]);

  // Event filter changes - refetch when event filter is changed
  // Uses prevEventIdsRef (initialized with current value) to prevent double-fetch on mount
  // NOTE: We intentionally DON'T check isInitialLoad.current here because:
  // 1. prevEventIdsRef prevents double-fetch on mount (initialized with current value)
  // 2. If user changes filter during initial load, we want to abort and fetch with new filter
  useEffect(() => {
    const currentEventIdsKey = JSON.stringify(selectedEventIds);
    if (!mockMode && hasAccess && prevEventIdsRef.current !== currentEventIdsKey) {
      console.log(`%c[STATS EFFECT] Event filter changed`, 'color: #0891b2; font-weight: bold', {
        prevEventIds: prevEventIdsRef.current,
        newEventIds: currentEventIdsKey
      });
      prevEventIdsRef.current = currentEventIdsKey;
      // Clear stats immediately to prevent showing stale data from different event filter
      setClientStats([]);
      setServiceStats([]);
      // Note: dashboardStats is NOT cleared here - it preserves old values until new data arrives
      // This allows AnimatedValue to smoothly animate from old→new instead of 0→new
      // The Dashboard's validation logic handles showing appropriate data during loading
      fetchStats({ showLoading: true, forceRefresh: true, trigger: 'eventFilterChange' });
    }
  }, [selectedEventIds, mockMode, hasAccess, fetchStats]);

  // Debounced custom date changes
  useEffect(() => {
    if (timeRange === 'custom' && !mockMode && hasAccess) {
      if (customStartDate && customEndDate) {
        const datesChanged =
          lastCustomDates.start?.getTime() !== customStartDate.getTime() ||
          lastCustomDates.end?.getTime() !== customEndDate.getTime();

        if (datesChanged) {
          console.log(`%c[STATS EFFECT] Custom dates changed`, 'color: #0891b2; font-weight: bold', {
            prevStart: lastCustomDates.start?.toLocaleString(),
            prevEnd: lastCustomDates.end?.toLocaleString(),
            newStart: customStartDate.toLocaleString(),
            newEnd: customEndDate.toLocaleString()
          });
          setLoading(true);
          const debounceTimer = setTimeout(() => {
            setLastCustomDates({ start: customStartDate, end: customEndDate });
            fetchStats({ showLoading: true, trigger: 'customDateChange' });
          }, 50);

          return () => clearTimeout(debounceTimer);
        }
      }
    } else if (timeRange !== 'custom') {
      setLastCustomDates({ start: null, end: null });
    }
  }, [customStartDate, customEndDate, timeRange, mockMode, hasAccess, fetchStats]);

  const updateStats = useCallback((updater: {
    cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
    clientStats?: (prev: ClientStat[]) => ClientStat[];
    serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
    dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
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
    });
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
