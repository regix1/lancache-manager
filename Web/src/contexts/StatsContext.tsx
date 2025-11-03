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
import type { CacheInfo, ClientStat, ServiceStat, DashboardStats } from '../types';

interface StatsContextType {
  cacheInfo: CacheInfo | null;
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;
  loading: boolean;
  error: string | null;
  connectionStatus: string;
  refreshStats: () => Promise<void>;
}

const StatsContext = createContext<StatsContextType | undefined>(undefined);

export const useStats = () => {
  const context = useContext(StatsContext);
  if (!context) {
    throw new Error('useStats must be used within StatsProvider');
  }
  return context;
};

interface StatsProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

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

  const [lastCustomDates, setLastCustomDates] = useState<{start: Date | null, end: Date | null}>({
    start: null,
    end: null
  });

  const isInitialLoad = useRef(true);
  const hasData = useRef(false);
  const fetchInProgress = useRef(false);
  const abortControllerRef = useRef<AbortController | null>(null);
  const fastIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const mediumIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const slowIntervalRef = useRef<NodeJS.Timeout | null>(null);
  const lastFastFetchTime = useRef<number>(0);
  const lastMediumFetchTime = useRef<number>(0);
  const lastSlowFetchTime = useRef<number>(0);
  const lastSignalRRefreshTime = useRef<number>(0);
  const isEffectActive = useRef<boolean>(true);
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getPollingIntervalRef = useRef(getPollingInterval);
  const mockModeRef = useRef(mockMode);

  // Keep refs updated
  useEffect(() => {
    currentTimeRangeRef.current = timeRange;
    getTimeRangeParamsRef.current = getTimeRangeParams;
    getPollingIntervalRef.current = getPollingInterval;
    mockModeRef.current = mockMode;
  }, [timeRange, getTimeRangeParams, getPollingInterval, mockMode]);

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

  // Fast refresh: cache info, dashboard stats
  const fetchFastData = async () => {
    if (mockModeRef.current) return;

    const now = Date.now();
    const debounceTime = Math.min(1000, Math.max(250, getPollingIntervalRef.current() / 4));

    if (!isInitialLoad.current && (now - lastFastFetchTime.current) < debounceTime) {
      return;
    }

    lastFastFetchTime.current = now;

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
        '1h': '1h', '6h': '6h', '12h': '12h', '24h': '24h',
        '7d': '7d', '30d': '30d', 'live': 'all', 'custom': 'custom'
      };
      const period = periodMap[currentTimeRangeRef.current] || '24h';

      const [cache, dashboard] = await Promise.allSettled([
        ApiService.getCacheInfo(abortControllerRef.current.signal),
        ApiService.getDashboardStats(period, abortControllerRef.current.signal)
      ]);

      if (cache.status === 'fulfilled' && cache.value !== undefined) {
        setCacheInfo(cache.value);
      }
      if (dashboard.status === 'fulfilled' && dashboard.value !== undefined) {
        setDashboardStats(dashboard.value);
      }

      clearTimeout(timeoutId);
      setError(null);
    } catch (err: any) {
      if (!hasData.current && err.name !== 'AbortError') {
        setError('Failed to fetch stats');
      }
    }
  };

  // Medium refresh: client stats
  const fetchMediumData = async () => {
    if (mockModeRef.current) return;

    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const now = Date.now();
    const debounceTime = 500;

    if (!isInitialLoad.current && (now - lastMediumFetchTime.current) < debounceTime) {
      return;
    }

    lastMediumFetchTime.current = now;

    try {
      const signal = (abortControllerRef.current && !abortControllerRef.current.signal.aborted)
        ? abortControllerRef.current.signal
        : new AbortController().signal;

      const clients = await ApiService.getClientStats(signal, startTime, endTime);
      if (clients && clients.length >= 0) {
        setClientStats(clients);
      }
    } catch (err: any) {
      if (err.name !== 'AbortError') {
        console.error('Failed to fetch client stats:', err);
      }
    }
  };

  // Slow refresh: service stats
  const fetchSlowData = async () => {
    if (mockModeRef.current) return;

    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const now = Date.now();
    const debounceTime = 1000;

    if (!isInitialLoad.current && (now - lastSlowFetchTime.current) < debounceTime) {
      return;
    }

    lastSlowFetchTime.current = now;

    try {
      const signal = (abortControllerRef.current && !abortControllerRef.current.signal.aborted)
        ? abortControllerRef.current.signal
        : new AbortController().signal;

      const services = await ApiService.getServiceStats(signal, null, startTime, endTime);
      if (services) {
        setServiceStats(services);
      }
    } catch (err: any) {
      if (err.name !== 'AbortError') {
        console.error('Failed to fetch service stats:', err);
      }
    }
  };

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
        '1h': '1h', '6h': '6h', '12h': '12h', '24h': '24h',
        '7d': '7d', '30d': '30d', 'live': 'all', 'custom': 'custom'
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
    } catch (err: any) {
      if (!hasData.current) {
        if (err.name === 'AbortError') {
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
      fetchFastData();
      fetchSlowData();
      fetchMediumData();
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

  // Initial load and polling intervals
  useEffect(() => {
    if (!mockMode) {
      isEffectActive.current = true;

      // Clear existing intervals
      if (fastIntervalRef.current) clearInterval(fastIntervalRef.current);
      if (mediumIntervalRef.current) clearInterval(mediumIntervalRef.current);
      if (slowIntervalRef.current) clearInterval(slowIntervalRef.current);

      // Initial load
      refreshStats().then(() => {
        if (!isEffectActive.current) return;

        // Clear existing intervals (race condition handling)
        if (fastIntervalRef.current) clearInterval(fastIntervalRef.current);
        if (mediumIntervalRef.current) clearInterval(mediumIntervalRef.current);
        if (slowIntervalRef.current) clearInterval(slowIntervalRef.current);

        // Set up intervals
        const fastInterval = getPollingInterval();
        const mediumInterval = 15000; // 15 seconds
        const slowInterval = 30000; // 30 seconds

        fastIntervalRef.current = setInterval(fetchFastData, fastInterval);
        mediumIntervalRef.current = setInterval(fetchMediumData, mediumInterval);
        slowIntervalRef.current = setInterval(fetchSlowData, slowInterval);
      });
    }

    return () => {
      isEffectActive.current = false;
      if (fastIntervalRef.current) clearInterval(fastIntervalRef.current);
      if (mediumIntervalRef.current) clearInterval(mediumIntervalRef.current);
      if (slowIntervalRef.current) clearInterval(slowIntervalRef.current);
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, [mockMode, refreshStats, getPollingInterval]);

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

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (fastIntervalRef.current) clearInterval(fastIntervalRef.current);
      if (mediumIntervalRef.current) clearInterval(mediumIntervalRef.current);
      if (slowIntervalRef.current) clearInterval(slowIntervalRef.current);
      if (abortControllerRef.current) abortControllerRef.current.abort();
    };
  }, []);

  const value = {
    cacheInfo,
    clientStats,
    serviceStats,
    dashboardStats,
    loading,
    error,
    connectionStatus,
    refreshStats
  };

  return (
    <StatsContext.Provider value={value}>
      {children}
    </StatsContext.Provider>
  );
};
