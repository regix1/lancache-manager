import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  useRef,
  type ReactNode
} from 'react';
import ApiService from '@services/api.service';
import MockDataService from '@/test/mockData.service';
import { useTimeFilter } from './TimeFilterContext';
import { usePollingRate } from './PollingRateContext';
import { useSignalR } from './SignalRContext';

interface CacheInfo {
  totalCacheSize: number;
  usedCacheSize: number;
  freeCacheSize: number;
  usagePercent: number;
  totalFiles: number;
  serviceSizes: Record<string, number>;
}

interface Download {
  id: number;
  service: string;
  clientIp: string;
  startTimeUtc: string;
  endTimeUtc: string | null;
  startTimeLocal: string;
  endTimeLocal: string | null;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  isActive: boolean;
  gameName?: string;
  gameAppId?: number;
  gameImageUrl?: string;
  depotId?: number;
  lastUrl?: string;
}

interface ClientStat {
  clientIp: string;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  totalDownloads: number;
  lastActivityUtc: string;
  lastActivityLocal: string;
}

interface ServiceStat {
  service: string;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  totalDownloads: number;
  lastActivityUtc: string;
  lastActivityLocal: string;
}

interface ProcessingStatus {
  type?: string;
  message: string;
  progress?: number;
  estimatedTime?: string;
  downloadCount?: number;
}

interface BackgroundRemoval {
  gameAppId: number;
  gameName: string;
  startedAt: Date;
  status: 'removing' | 'completed' | 'failed';
  filesDeleted?: number;
  bytesFreed?: number;
  error?: string;
}

interface BackgroundLogProcessing {
  id: string;
  message: string;
  detailMessage?: string;
  progress: number;
  estimatedTime?: string;
  status: 'processing' | 'complete' | 'failed';
  startedAt: Date;
  error?: string;
}

interface BackgroundCacheClearing {
  id: string;
  filesDeleted: number;
  progress: number;
  status: 'clearing' | 'complete' | 'failed';
  startedAt: Date;
  error?: string;
}

interface BackgroundServiceRemoval {
  service: string;
  status: 'removing' | 'complete' | 'failed';
  startedAt: Date;
  error?: string;
}

interface BackgroundDatabaseReset {
  id: string;
  message: string;
  progress: number;
  status: 'resetting' | 'complete' | 'failed';
  startedAt: Date;
  error?: string;
}

interface BackgroundDepotMapping {
  id: string;
  totalMappings: number;
  processedMappings: number;
  mappingsApplied?: number;
  percentComplete: number;
  status: string;
  message: string;
  startedAt: Date;
  isProcessing: boolean;
}

interface DashboardStats {
  totalBandwidthSaved: number;
  totalAddedToCache: number;
  totalServed: number;
  cacheHitRatio: number;
  activeDownloads: number;
  uniqueClients: number;
  topService: string;
  period: {
    duration: string;
    since?: Date | null;
    bandwidthSaved: number;
    addedToCache: number;
    totalServed: number;
    hitRatio: number;
    downloads: number;
  };
  serviceBreakdown?: Array<{
    service: string;
    bytes: number;
    percentage: number;
  }>;
  lastUpdated?: Date;
}

interface DataContextType {
  mockMode: boolean;
  setMockMode: (mode: boolean) => void;
  mockDownloadCount: number | 'unlimited';
  updateMockDataCount: (count: number | 'unlimited') => void;
  apiDownloadCount: number | 'unlimited';
  updateApiDownloadCount: (count: number | 'unlimited') => void;
  cacheInfo: CacheInfo | null;
  activeDownloads: Download[];
  latestDownloads: Download[];
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;
  loading: boolean;
  error: string | null;
  fetchData: () => Promise<void>;
  clearAllData: () => void;
  isProcessingLogs: boolean;
  setIsProcessingLogs: (processing: boolean) => void;
  processingStatus: ProcessingStatus | null;
  setProcessingStatus: (status: ProcessingStatus | null) => void;
  connectionStatus: string;
  getCurrentRefreshInterval: () => number;
  backgroundRemovals: BackgroundRemoval[];
  addBackgroundRemoval: (removal: BackgroundRemoval) => void;
  updateBackgroundRemoval: (gameAppId: number, updates: Partial<BackgroundRemoval>) => void;
  clearBackgroundRemoval: (gameAppId: number) => void;
  backgroundLogProcessing: BackgroundLogProcessing | null;
  setBackgroundLogProcessing: (processing: BackgroundLogProcessing | null) => void;
  updateBackgroundLogProcessing: (updates: Partial<BackgroundLogProcessing>) => void;
  backgroundCacheClearing: BackgroundCacheClearing | null;
  setBackgroundCacheClearing: (clearing: BackgroundCacheClearing | null) => void;
  updateBackgroundCacheClearing: (updates: Partial<BackgroundCacheClearing>) => void;
  backgroundServiceRemovals: BackgroundServiceRemoval[];
  addBackgroundServiceRemoval: (removal: BackgroundServiceRemoval) => void;
  updateBackgroundServiceRemoval: (service: string, updates: Partial<BackgroundServiceRemoval>) => void;
  clearBackgroundServiceRemoval: (service: string) => void;
  backgroundDatabaseReset: BackgroundDatabaseReset | null;
  setBackgroundDatabaseReset: (reset: BackgroundDatabaseReset | null) => void;
  updateBackgroundDatabaseReset: (updates: Partial<BackgroundDatabaseReset>) => void;
  backgroundDepotMapping: BackgroundDepotMapping | null;
  setBackgroundDepotMapping: (mapping: BackgroundDepotMapping | null) => void;
  updateBackgroundDepotMapping: (updates: Partial<BackgroundDepotMapping>) => void;
}

const DataContext = createContext<DataContextType | undefined>(undefined);

export const useData = () => {
  const context = useContext(DataContext);
  if (!context) throw new Error('useData must be used within DataProvider');
  return context;
};

interface DataProviderProps {
  children: ReactNode;
}

export const DataProvider: React.FC<DataProviderProps> = ({ children }) => {
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate } = useTimeFilter();
  const { getPollingInterval, pollingRate } = usePollingRate();

  // Keep refs updated whenever they change
  useEffect(() => {
    currentTimeRangeRef.current = timeRange;
    getTimeRangeParamsRef.current = getTimeRangeParams;
    getPollingIntervalRef.current = getPollingInterval;
  }, [timeRange, getTimeRangeParams, getPollingInterval]);
  const [mockMode, setMockMode] = useState(false);

  // Create a ref to track mock mode for use in callbacks/intervals that might have stale closures
  const mockModeRef = useRef(mockMode);
  useEffect(() => {
    mockModeRef.current = mockMode;
  }, [mockMode]);

  const [lastCustomDates, setLastCustomDates] = useState<{start: Date | null, end: Date | null}>({
    start: null,
    end: null
  });
  const [mockDownloadCount, setMockDownloadCount] = useState<number | 'unlimited'>('unlimited');
  const [apiDownloadCount, setApiDownloadCount] = useState<number | 'unlimited'>('unlimited');
  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(null);
  const [activeDownloads, setActiveDownloads] = useState<Download[]>([]);
  const [latestDownloads, setLatestDownloads] = useState<Download[]>([]);
  const [clientStats, setClientStats] = useState<ClientStat[]>([]);
  const [serviceStats, setServiceStats] = useState<ServiceStat[]>([]);
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [isProcessingLogs, setIsProcessingLogs] = useState(false);
  const [processingStatus, setProcessingStatus] = useState<ProcessingStatus | null>(null);
  const [connectionStatus, setConnectionStatus] = useState('checking');
  const [backgroundRemovals, setBackgroundRemovals] = useState<BackgroundRemoval[]>([]);
  const [backgroundLogProcessing, setBackgroundLogProcessing] = useState<BackgroundLogProcessing | null>(null);
  const [backgroundCacheClearing, setBackgroundCacheClearing] = useState<BackgroundCacheClearing | null>(null);
  const [backgroundServiceRemovals, setBackgroundServiceRemovals] = useState<BackgroundServiceRemoval[]>([]);
  const [backgroundDatabaseReset, setBackgroundDatabaseReset] = useState<BackgroundDatabaseReset | null>(null);
  const [backgroundDepotMapping, setBackgroundDepotMapping] = useState<BackgroundDepotMapping | null>(null);

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

  // Use centralized SignalR connection
  const signalR = useSignalR();

  const getApiUrl = (): string => {
    if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
      return import.meta.env.VITE_API_URL;
    }
    return '';
  };

  const checkConnectionStatus = async () => {
    // Don't check connection in mock mode - always return connected - use ref to avoid stale closure
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

  // Fast refresh data: cards, active downloads, recent downloads (5 seconds)
  const fetchFastData = async () => {
    // Immediately exit if in mock mode - use ref to avoid stale closure
    if (mockModeRef.current) {
      console.warn('[DataContext] fetchFastData blocked - mock mode enabled');
      return;
    }

    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const now = Date.now();

    // Respect user's polling rate but reduce debounce overhead
    // Use smaller of half-interval or 1 second for better responsiveness
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

      const timeout = isProcessingLogs ? 30000 : 10000;
      const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

      const periodMap: Record<string, string> = {
        '1h': '1h', '6h': '6h', '12h': '12h', '24h': '24h',
        '7d': '7d', '30d': '30d', 'live': 'all', 'custom': 'custom'
      };
      const period = periodMap[currentTimeRangeRef.current] || '24h';

      const [cache, active, latest, dashboard] = await Promise.allSettled([
        ApiService.getCacheInfo(abortControllerRef.current.signal),
        ApiService.getActiveDownloads(abortControllerRef.current.signal),
        ApiService.getLatestDownloads(abortControllerRef.current.signal, 'unlimited', startTime, endTime),
        ApiService.getDashboardStats(period, abortControllerRef.current.signal)
      ]);

      if (cache.status === 'fulfilled' && cache.value !== undefined) {
        setCacheInfo(cache.value);
      }
      if (active.status === 'fulfilled' && active.value !== undefined) {
        setActiveDownloads(active.value);
      }
      if (latest.status === 'fulfilled' && latest.value !== undefined) {
        setLatestDownloads(latest.value);
        hasData.current = true;
      }
      if (dashboard.status === 'fulfilled' && dashboard.value !== undefined) {
        setDashboardStats(dashboard.value);
      }

      clearTimeout(timeoutId);
      setError(null);
    } catch (err: any) {
      if (!hasData.current && err.name !== 'AbortError') {
        setError('Failed to fetch fast data');
      }
    }
  };

  // Medium refresh data: client stats (15 seconds)
  const fetchMediumData = async () => {
    // Immediately exit if in mock mode - use ref to avoid stale closure
    if (mockModeRef.current) {
      console.warn('[DataContext] fetchMediumData blocked - mock mode enabled');
      return;
    }

    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const now = Date.now();

    // Minimal debounce for medium data - 500ms
    const debounceTime = 500;
    if (!isInitialLoad.current && (now - lastMediumFetchTime.current) < debounceTime) {
      return;
    }

    lastMediumFetchTime.current = now;

    try {
      // Create a new signal if the current one is aborted or doesn't exist
      const signal = (abortControllerRef.current && !abortControllerRef.current.signal.aborted)
        ? abortControllerRef.current.signal
        : new AbortController().signal;

      const clients = await ApiService.getClientStats(signal, startTime, endTime);
      if (clients) {
        setClientStats(clients);
      }
    } catch (err: any) {
      if (err.name !== 'AbortError') {
        console.error('Failed to fetch client stats:', err);
      }
    }
  };

  // Slow refresh data: service stats for chart (30 seconds)
  const fetchSlowData = async () => {
    // Immediately exit if in mock mode - use ref to avoid stale closure
    if (mockModeRef.current) {
      console.warn('[DataContext] fetchSlowData blocked - mock mode enabled');
      return;
    }

    const { startTime, endTime } = getTimeRangeParamsRef.current();
    const now = Date.now();

    // Minimal debounce for slow data - 1 second
    const debounceTime = 1000;
    if (!isInitialLoad.current && (now - lastSlowFetchTime.current) < debounceTime) {
      return;
    }

    lastSlowFetchTime.current = now;

    try {
      // Create a new signal if the current one is aborted or doesn't exist
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

  // Combined fetch for initial load or manual refresh
  const fetchData = async () => {
    // Don't fetch real data if in mock mode - use ref to avoid stale closure
    if (mockModeRef.current) {
      console.warn('[DataContext] fetchData blocked - mock mode enabled');
      return;
    }

    const { startTime, endTime } = getTimeRangeParamsRef.current();

    if (fetchInProgress.current && !isInitialLoad.current) {
      return;
    }

    fetchInProgress.current = true;

    // Cancel any previous request
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    abortControllerRef.current = new AbortController();

    try {
      if (isInitialLoad.current) {
        setLoading(true);
      }

      if (mockMode) {
        // Use unlimited for mock data
        const actualCount =
          mockDownloadCount === 'unlimited' ? 500 : Number(mockDownloadCount);
        const mockData = MockDataService.generateMockData(actualCount);
        setCacheInfo(mockData.cacheInfo);
        setActiveDownloads(mockData.activeDownloads);
        setLatestDownloads(mockData.latestDownloads);
        setClientStats(mockData.clientStats);
        setServiceStats(mockData.serviceStats);
        setDashboardStats(mockData.dashboardStats);
        setError(null);
        setConnectionStatus('connected');
        hasData.current = true;
      } else {
        const isConnected = await checkConnectionStatus();

        if (isConnected) {
          try {
            const timeout = isProcessingLogs ? 30000 : 10000;
            const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

            const periodMap: Record<string, string> = {
              '1h': '1h', '6h': '6h', '12h': '12h', '24h': '24h',
              '7d': '7d', '30d': '30d', 'live': 'all', 'custom': 'custom'
            };
            const period = periodMap[currentTimeRangeRef.current] || '24h';

            // Fetch all data on initial load or manual refresh
            const [cache, active, latest, clients, services, dashboard] = await Promise.allSettled([
              ApiService.getCacheInfo(abortControllerRef.current.signal),
              ApiService.getActiveDownloads(abortControllerRef.current.signal),
              ApiService.getLatestDownloads(abortControllerRef.current.signal, 'unlimited', startTime, endTime),
              ApiService.getClientStats(abortControllerRef.current.signal, startTime, endTime),
              ApiService.getServiceStats(abortControllerRef.current.signal, null, startTime, endTime),
              ApiService.getDashboardStats(period, abortControllerRef.current.signal)
            ]);

            if (cache.status === 'fulfilled' && cache.value !== undefined) {
              setCacheInfo(cache.value);
            }
            if (active.status === 'fulfilled' && active.value !== undefined) {
              setActiveDownloads(active.value);
            }
            if (latest.status === 'fulfilled' && latest.value !== undefined) {
              setLatestDownloads(latest.value);
              hasData.current = true;
            }
            if (clients.status === 'fulfilled' && clients.value !== undefined) {
              setClientStats(clients.value);
            }
            if (services.status === 'fulfilled' && services.value !== undefined) {
              setServiceStats(services.value);
            }
            if (dashboard.status === 'fulfilled' && dashboard.value !== undefined) {
              setDashboardStats(dashboard.value);
            }

            clearTimeout(timeoutId);
            setError(null);
          } catch (err: any) {
            if (!hasData.current) {
              if (err.name === 'AbortError') {
                setError('Request timeout - the server may be busy');
              } else {
                setError('Failed to fetch data from API');
              }
            }
          }
        } else {
          if (!hasData.current) {
            setError('Cannot connect to API server');
          }
        }
      }
    } catch (err) {
      console.error('Error in fetchData:', err);
      if (!hasData.current && !mockMode) {
        setError('An unexpected error occurred');
      }
    } finally {
      setLoading(false); // Always set loading to false when done
      if (isInitialLoad.current) {
        isInitialLoad.current = false;
      }
      fetchInProgress.current = false;
    }
  };

  const updateMockDataCount = (count: number | 'unlimited') => {
    if (mockMode) {
      setMockDownloadCount(count);
    }
  };

  const updateApiDownloadCount = (count: number | 'unlimited') => {
    setApiDownloadCount(count);
  };

  const getCurrentRefreshInterval = () => {
    if (isProcessingLogs) return 3000; // 3 seconds when processing
    return getPollingIntervalRef.current(); // Use ref to avoid stale closure
  };

  const getMediumRefreshInterval = () => {
    return 15000; // 15 seconds for client stats
  };

  const getSlowRefreshInterval = () => {
    return 30000; // 30 seconds for service chart
  };

  // Subscribe to SignalR events for real-time updates
  useEffect(() => {
    // Don't subscribe in mock mode
    if (mockMode) {
      return;
    }

    // Handler for DownloadsRefresh event
    const handleDownloadsRefresh = () => {
      // Respect the user's polling rate preference
      const now = Date.now();
      const timeSinceLastRefresh = now - lastSignalRRefreshTime.current;

      // Use the current polling rate from ref to avoid stale closure
      const pollingInterval = getPollingIntervalRef.current();

      // Respect user's polling interval preference for SignalR events
      // This ensures SignalR refreshes honor the user's selected polling rate
      if (timeSinceLastRefresh < pollingInterval) {
        return;
      }

      lastSignalRRefreshTime.current = now;

      // Fetch fresh data when downloads are updated
      // Fetch fast data (cache, active downloads, latest downloads, dashboard stats)
      fetchFastData();
      // Also fetch service stats so the chart updates in real-time
      fetchSlowData();
      // And client stats for the top clients table
      fetchMediumData();
    };

    // Subscribe to the event
    signalR.on('DownloadsRefresh', handleDownloadsRefresh);

    console.log('[DataContext] Subscribed to SignalR DownloadsRefresh events');

    // Cleanup: unsubscribe when component unmounts or dependencies change
    return () => {
      signalR.off('DownloadsRefresh', handleDownloadsRefresh);
      console.log('[DataContext] Unsubscribed from SignalR DownloadsRefresh events');
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mockMode]); // signalR.on/off are stable, don't need signalR as dependency

  // Initial load and separate refresh intervals for different data types
  useEffect(() => {
    if (!mockMode) {
      // Mark this effect as active
      isEffectActive.current = true;

      // Clear any existing intervals first
      if (fastIntervalRef.current) {
        clearInterval(fastIntervalRef.current);
        fastIntervalRef.current = null;
      }
      if (mediumIntervalRef.current) {
        clearInterval(mediumIntervalRef.current);
        mediumIntervalRef.current = null;
      }
      if (slowIntervalRef.current) {
        clearInterval(slowIntervalRef.current);
        slowIntervalRef.current = null;
      }

      // Initial load - fetch all data once, then start intervals
      fetchData().then(() => {
        // Only set up intervals if this effect is still active (not cleaned up)
        if (!isEffectActive.current) return;

        // Clear any existing intervals before creating new ones (handles race conditions)
        if (fastIntervalRef.current) clearInterval(fastIntervalRef.current);
        if (mediumIntervalRef.current) clearInterval(mediumIntervalRef.current);
        if (slowIntervalRef.current) clearInterval(slowIntervalRef.current);

        // Set up separate intervals for different data refresh rates AFTER initial load
        const fastInterval = getCurrentRefreshInterval();
        const mediumInterval = getMediumRefreshInterval();
        const slowInterval = getSlowRefreshInterval();

        fastIntervalRef.current = setInterval(fetchFastData, fastInterval);
        mediumIntervalRef.current = setInterval(fetchMediumData, mediumInterval);
        slowIntervalRef.current = setInterval(fetchSlowData, slowInterval);
      });
    }

    return () => {
      // Clear intervals when switching modes or changing polling rate
      isEffectActive.current = false;

      if (fastIntervalRef.current) {
        clearInterval(fastIntervalRef.current);
        fastIntervalRef.current = null;
      }
      if (mediumIntervalRef.current) {
        clearInterval(mediumIntervalRef.current);
        mediumIntervalRef.current = null;
      }
      if (slowIntervalRef.current) {
        clearInterval(slowIntervalRef.current);
        slowIntervalRef.current = null;
      }
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }
    };
  }, [mockMode, pollingRate]); // Re-create intervals when polling rate changes

  // Handle time range changes - refetch data when time range changes
  useEffect(() => {
    if (!mockMode && !isInitialLoad.current) {
      // Set loading state when time range changes
      setLoading(true);

      // Immediate fetch for time range changes - no debounce needed
      fetchData();
    }
  }, [timeRange, mockMode]);

  // Debounced custom date changes - only refetch when both dates are set and different from last
  useEffect(() => {
    if (timeRange === 'custom' && !mockMode) {
      // Only proceed if both dates are set
      if (customStartDate && customEndDate) {
        // Check if dates actually changed
        const datesChanged =
          lastCustomDates.start?.getTime() !== customStartDate.getTime() ||
          lastCustomDates.end?.getTime() !== customEndDate.getTime();

        if (datesChanged) {
          setLoading(true);
          // Minimal debounce to prevent duplicate calls
          const debounceTimer = setTimeout(() => {
            setLastCustomDates({
              start: customStartDate,
              end: customEndDate
            });
            fetchData();
          }, 50); // Reduced to 50ms for faster response

          return () => clearTimeout(debounceTimer);
        }
      }
    } else if (timeRange !== 'custom') {
      // Clear stored custom dates when switching away from custom
      setLastCustomDates({ start: null, end: null });
    }
  }, [customStartDate, customEndDate, timeRange, mockMode]);

  // Mock mode changes
  useEffect(() => {
    if (mockMode) {
      // Clear any existing intervals and abort any pending requests
      if (fastIntervalRef.current) {
        clearInterval(fastIntervalRef.current);
        fastIntervalRef.current = null;
      }
      if (mediumIntervalRef.current) {
        clearInterval(mediumIntervalRef.current);
        mediumIntervalRef.current = null;
      }
      if (slowIntervalRef.current) {
        clearInterval(slowIntervalRef.current);
        slowIntervalRef.current = null;
      }
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
        abortControllerRef.current = null;
      }

      // Clear real data
      setCacheInfo(null);
      setActiveDownloads([]);
      setLatestDownloads([]);
      setClientStats([]);
      setServiceStats([]);

      // Generate mock data
      const actualCount =
        mockDownloadCount === 'unlimited' ? 500 : Number(mockDownloadCount);
      const mockData = MockDataService.generateMockData(actualCount);
      setCacheInfo(mockData.cacheInfo);
      setActiveDownloads(mockData.activeDownloads);
      setLatestDownloads(mockData.latestDownloads);
      setClientStats(mockData.clientStats);
      setServiceStats(mockData.serviceStats);
      setDashboardStats(mockData.dashboardStats);
      setError(null);
      setConnectionStatus('connected');
      hasData.current = true;

      // Set up mock update interval
      const updateInterval = 30000;
      fastIntervalRef.current = setInterval(() => {
        const newDownload = MockDataService.generateRealtimeUpdate();
        setLatestDownloads((prev) => {
          return [newDownload, ...prev];
        });

        setActiveDownloads((prev) => {
          const updated = [newDownload, ...prev.filter((d) => d.id !== newDownload.id)];
          return updated.slice(0, 5);
        });
      }, updateInterval);

      return () => {
        if (fastIntervalRef.current) {
          clearInterval(fastIntervalRef.current);
          fastIntervalRef.current = null;
        }
      };
    } else {
      // Clear mock data and fetch real data
      setCacheInfo(null);
      setActiveDownloads([]);
      setLatestDownloads([]);
      setClientStats([]);
      setServiceStats([]);
      setDashboardStats(null);
      setError(null);
      hasData.current = false;
      isInitialLoad.current = true;

      fetchData();
    }
  }, [mockMode, mockDownloadCount]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (fastIntervalRef.current) {
        clearInterval(fastIntervalRef.current);
      }
      if (mediumIntervalRef.current) {
        clearInterval(mediumIntervalRef.current);
      }
      if (slowIntervalRef.current) {
        clearInterval(slowIntervalRef.current);
      }
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }
    };
  }, []);

  const clearAllData = () => {
    setCacheInfo(null);
    setActiveDownloads([]);
    setLatestDownloads([]);
    setClientStats([]);
    setServiceStats([]);
    setDashboardStats(null);
    hasData.current = false;
  };

  const addBackgroundRemoval = (removal: BackgroundRemoval) => {
    setBackgroundRemovals((prev) => [...prev, removal]);
  };

  const updateBackgroundRemoval = (gameAppId: number, updates: Partial<BackgroundRemoval>) => {
    setBackgroundRemovals((prev) =>
      prev.map((r) => (r.gameAppId === gameAppId ? { ...r, ...updates } : r))
    );
  };

  const clearBackgroundRemoval = (gameAppId: number) => {
    setBackgroundRemovals((prev) => prev.filter((r) => r.gameAppId !== gameAppId));
  };

  const updateBackgroundLogProcessing = (updates: Partial<BackgroundLogProcessing>) => {
    setBackgroundLogProcessing((prev) => (prev ? { ...prev, ...updates } : null));
  };

  const updateBackgroundCacheClearing = (updates: Partial<BackgroundCacheClearing>) => {
    setBackgroundCacheClearing((prev) => (prev ? { ...prev, ...updates } : null));
  };

  const addBackgroundServiceRemoval = (removal: BackgroundServiceRemoval) => {
    setBackgroundServiceRemovals((prev) => [...prev, removal]);
  };

  const updateBackgroundServiceRemoval = (service: string, updates: Partial<BackgroundServiceRemoval>) => {
    setBackgroundServiceRemovals((prev) =>
      prev.map((r) => (r.service === service ? { ...r, ...updates } : r))
    );
  };

  const clearBackgroundServiceRemoval = (service: string) => {
    setBackgroundServiceRemovals((prev) => prev.filter((r) => r.service !== service));
  };

  const updateBackgroundDatabaseReset = (updates: Partial<BackgroundDatabaseReset>) => {
    setBackgroundDatabaseReset((prev) => (prev ? { ...prev, ...updates } : null));
  };

  const updateBackgroundDepotMapping = (updates: Partial<BackgroundDepotMapping>) => {
    setBackgroundDepotMapping((prev) => (prev ? { ...prev, ...updates } : null));
  };

  const value: DataContextType = {
    mockMode,
    setMockMode,
    mockDownloadCount,
    updateMockDataCount,
    apiDownloadCount,
    updateApiDownloadCount,
    cacheInfo,
    activeDownloads,
    latestDownloads,
    clientStats,
    serviceStats,
    dashboardStats,
    loading,
    error,
    fetchData,
    clearAllData,
    isProcessingLogs,
    setIsProcessingLogs,
    processingStatus,
    setProcessingStatus,
    connectionStatus,
    getCurrentRefreshInterval,
    backgroundRemovals,
    addBackgroundRemoval,
    updateBackgroundRemoval,
    clearBackgroundRemoval,
    backgroundLogProcessing,
    setBackgroundLogProcessing,
    updateBackgroundLogProcessing,
    backgroundCacheClearing,
    setBackgroundCacheClearing,
    updateBackgroundCacheClearing,
    backgroundServiceRemovals,
    addBackgroundServiceRemoval,
    updateBackgroundServiceRemoval,
    clearBackgroundServiceRemoval,
    backgroundDatabaseReset,
    setBackgroundDatabaseReset,
    updateBackgroundDatabaseReset,
    backgroundDepotMapping,
    setBackgroundDepotMapping,
    updateBackgroundDepotMapping
  };

  return <DataContext.Provider value={value}>{children}</DataContext.Provider>;
};
