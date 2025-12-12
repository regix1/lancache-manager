import React, { createContext, useContext, useState, useEffect, useRef, useCallback } from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import MockDataService from '../../test/mockData.service';
import { useTimeFilter } from '../TimeFilterContext';
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
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const mockModeRef = useRef(mockMode);

  // Update refs on each render (no useEffect needed)
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
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

  // Fetch all stats (called by SignalR events)
  const fetchAllStats = useCallback(async () => {
    if (mockModeRef.current) return;

    const now = Date.now();
    // Debounce rapid SignalR events (min 250ms between fetches)
    if (now - lastFetchTime.current < 250) {
      return;
    }
    lastFetchTime.current = now;

    const { startTime, endTime } = getTimeRangeParamsRef.current();

    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    abortControllerRef.current = new AbortController();

    try {
      const isConnected = await checkConnectionStatus();
      if (!isConnected) return;

      const timeout = 10000;
      const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

      const periodMap: Record<string, string> = {
        '1h': '1h',
        '6h': '6h',
        '12h': '12h',
        '24h': '24h',
        '7d': '7d',
        '30d': '30d',
        live: 'all',
        custom: 'custom'
      };
      const period = periodMap[currentTimeRangeRef.current] || '24h';

      const [cache, clients, services, dashboard] = await Promise.allSettled([
        ApiService.getCacheInfo(abortControllerRef.current.signal),
        ApiService.getClientStats(abortControllerRef.current.signal, startTime, endTime),
        ApiService.getServiceStats(abortControllerRef.current.signal, null, startTime, endTime),
        ApiService.getDashboardStats(period, abortControllerRef.current.signal)
      ]);

      if (cache.status === 'fulfilled' && cache.value !== undefined) {
        setCacheInfo(cache.value);
      }
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

      clearTimeout(timeoutId);
      setError(null);
    } catch (err: unknown) {
      if (!hasData.current && !isAbortError(err)) {
        setError('Failed to fetch stats');
      }
    }
  }, []);

  // Combined refresh for initial load or manual refresh
  const refreshStats = useCallback(async () => {
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

      const isConnected = await checkConnectionStatus();
      if (!isConnected) {
        if (!hasData.current) {
          setError('Cannot connect to API server');
        }
        return;
      }

      const timeout = 10000;
      const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

      const periodMap: Record<string, string> = {
        '1h': '1h',
        '6h': '6h',
        '12h': '12h',
        '24h': '24h',
        '7d': '7d',
        '30d': '30d',
        live: 'all',
        custom: 'custom'
      };
      const period = periodMap[currentTimeRangeRef.current] || '24h';

      const [cache, clients, services, dashboard] = await Promise.allSettled([
        ApiService.getCacheInfo(abortControllerRef.current.signal),
        ApiService.getClientStats(abortControllerRef.current.signal, startTime, endTime),
        ApiService.getServiceStats(abortControllerRef.current.signal, null, startTime, endTime),
        ApiService.getDashboardStats(period, abortControllerRef.current.signal)
      ]);

      if (cache.status === 'fulfilled' && cache.value !== undefined) {
        setCacheInfo(cache.value);
      }
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

      clearTimeout(timeoutId);
      setError(null);
    } catch (err: unknown) {
      if (!hasData.current) {
        if (isAbortError(err)) {
          setError('Request timeout - the server may be busy');
        } else {
          setError('Failed to fetch stats from API');
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
  useEffect(() => {
    if (mockMode) return;

    // Handler for database reset completion
    const handleDatabaseResetProgress = (payload: { status?: string }) => {
      const status = (payload.status || '').toLowerCase();
      if (status === 'completed' || status === 'complete' || status === 'done') {
        setTimeout(() => fetchAllStats(), 500);
      }
    };

    // Events that trigger data refresh
    const refreshEvents = [
      'DownloadsRefresh',
      'FastProcessingComplete',
      'DepotMappingComplete',
      'LogRemovalComplete',
      'CorruptionRemovalComplete',
      'ServiceRemovalComplete',
      'GameDetectionComplete',
      'GameRemovalComplete',
      'CacheClearComplete'
    ];

    refreshEvents.forEach(event => signalR.on(event, fetchAllStats));
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      refreshEvents.forEach(event => signalR.off(event, fetchAllStats));
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
    };
  }, [mockMode, signalR, fetchAllStats]);

  // Load mock data when mock mode is enabled
  useEffect(() => {
    if (mockMode) {
      setLoading(true);
      setConnectionStatus('connected');

      // Generate mock data immediately
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

  // Initial load only - SignalR handles real-time updates
  useEffect(() => {
    if (!mockMode) {
      refreshStats();
    }

    return () => {
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, refreshStats]);

  // Handle time range changes
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      setLoading(true);
      refreshStats();
    }
  }, [timeRange, mockMode, refreshStats]);

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
            refreshStats();
          }, 50);

          return () => clearTimeout(debounceTimer);
        }
      }
    } else if (timeRange !== 'custom') {
      setLastCustomDates({ start: null, end: null });
    }
  }, [customStartDate, customEndDate, timeRange, mockMode, refreshStats]);

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
