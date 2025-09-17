import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  useRef,
  type ReactNode
} from 'react';
import ApiService from '@services/api.service';
import MockDataService from '@services/mockData.service';
import { REFRESH_INTERVAL } from '@utils/constants';
import { useTimeFilter } from './TimeFilterContext';

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
  startTime: string;
  endTime: string | null;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  isActive: boolean;
  gameName?: string;
  gameAppId?: number;
}

interface ClientStat {
  clientIp: string;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  totalDownloads: number;
  lastSeen: string | null;
}

interface ServiceStat {
  service: string;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  totalDownloads: number;
  lastActivity: string;
}

interface ProcessingStatus {
  type?: string;
  message: string;
  progress?: number;
  estimatedTime?: string;
  downloadCount?: number;
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
  const { getTimeRangeParams, timeRange } = useTimeFilter();
  const [mockMode, setMockMode] = useState(false);
  const [mockDownloadCount, setMockDownloadCount] = useState<number | 'unlimited'>(20);
  const [apiDownloadCount, setApiDownloadCount] = useState<number | 'unlimited'>(20);
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

  const isInitialLoad = useRef(true);
  const hasData = useRef(false);
  const fetchInProgress = useRef(false);
  const abortControllerRef = useRef<AbortController | null>(null);
  const intervalRef = useRef<NodeJS.Timeout | null>(null);

  const getApiUrl = (): string => {
    if (typeof import.meta !== 'undefined' && import.meta.env?.VITE_API_URL) {
      return import.meta.env.VITE_API_URL;
    }
    return '';
  };

  const checkConnectionStatus = async () => {
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

  const fetchData = async () => {
    const { startTime, endTime } = getTimeRangeParams();
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
        // Cap mock data at 100 for performance
        const actualCount =
          mockDownloadCount === 'unlimited' ? 100 : Math.min(Number(mockDownloadCount), 100);
        const mockData = MockDataService.generateMockData(actualCount);
        setCacheInfo(mockData.cacheInfo);
        setActiveDownloads(mockData.activeDownloads);
        setLatestDownloads(mockData.latestDownloads);
        setClientStats(mockData.clientStats);
        setServiceStats(mockData.serviceStats);
        setError(null);
        setConnectionStatus('connected');
        hasData.current = true;
      } else {
        const isConnected = await checkConnectionStatus();

        if (isConnected) {
          try {
            const timeout = isProcessingLogs ? 30000 : 10000;
            const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

            if (isInitialLoad.current) {
              // Phase 1: Critical data for initial display
              const [cache, active] = await Promise.all([
                ApiService.getCacheInfo(abortControllerRef.current.signal),
                ApiService.getActiveDownloads(abortControllerRef.current.signal)
              ]);

              if (cache) setCacheInfo(cache);
              if (active) setActiveDownloads(active);

              // Phase 2: Limited downloads for initial display (cap at 20)
              const latest = await ApiService.getLatestDownloads(
                abortControllerRef.current.signal,
                20,
                startTime,
                endTime
              );
              if (latest) {
                setLatestDownloads(latest);
                hasData.current = true;
              }

              // Phase 3: Defer stats loading after initial render
              setTimeout(async () => {
                if (abortControllerRef.current?.signal.aborted) return;

                try {
                  // Get the appropriate period string for the dashboard stats
                  const periodMap: Record<string, string> = {
                    '1h': '1h',
                    '6h': '6h',
                    '12h': '12h',
                    '24h': '24h',
                    '7d': '7d',
                    '30d': '30d',
                    'all': 'all',
                    'custom': 'custom'
                  };
                  const period = periodMap[timeRange] || '24h';

                  const [clients, services, dashboard] = await Promise.all([
                    ApiService.getClientStats(abortControllerRef.current!.signal, startTime, endTime),
                    ApiService.getServiceStats(abortControllerRef.current!.signal, null, startTime, endTime),
                    ApiService.getDashboardStats(period, abortControllerRef.current!.signal)
                  ]);
                  if (clients) setClientStats(clients);
                  if (services) setServiceStats(services);
                  if (dashboard) setDashboardStats(dashboard);
                } catch (err) {
                }
              }, 100);
            } else {
              // Regular updates - use user-specified count but cap at 100 for unlimited
              const cappedCount = apiDownloadCount === 'unlimited' ? 100 : apiDownloadCount;

              // Get the appropriate period string for the dashboard stats
              const periodMap: Record<string, string> = {
                '1h': '1h',
                '6h': '6h',
                '12h': '12h',
                '24h': '24h',
                '7d': '7d',
                '30d': '30d',
                'all': 'all',
                'custom': 'custom'
              };
              const period = periodMap[timeRange] || '24h';

              const [cache, active, latest, clients, services, dashboard] = await Promise.allSettled([
                ApiService.getCacheInfo(abortControllerRef.current.signal),
                ApiService.getActiveDownloads(abortControllerRef.current.signal),
                ApiService.getLatestDownloads(abortControllerRef.current.signal, cappedCount, startTime, endTime),
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

                if (isProcessingLogs && latest.value.length > 0) {
                  setProcessingStatus((prev) => ({
                    ...prev!,
                    message: `Processing logs... Found ${latest.value.length} downloads`,
                    downloadCount: latest.value.length
                  }));
                }
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
      if (isInitialLoad.current) {
        setLoading(false);
        isInitialLoad.current = false;
      }
      fetchInProgress.current = false;
    }
  };

  const updateMockDataCount = (count: number | 'unlimited') => {
    if (mockMode) {
      // Cap at 100 for performance
      const cappedCount = count === 'unlimited' ? 100 : Math.min(count, 100);
      setMockDownloadCount(cappedCount);
    }
  };

  const updateApiDownloadCount = (count: number | 'unlimited') => {
    // Cap unlimited at 100 for performance
    const cappedCount = count === 'unlimited' ? 100 : count;
    setApiDownloadCount(cappedCount);
  };

  const getCurrentRefreshInterval = () => {
    if (isProcessingLogs) return 15000; // 15 seconds when processing
    if (apiDownloadCount === 'unlimited' || apiDownloadCount > 100) return 30000; // 30 seconds for large datasets
    return REFRESH_INTERVAL; // Default 5 seconds
  };

  // Initial load and refresh interval
  useEffect(() => {
    if (!mockMode) {
      fetchData();

      const refreshInterval = getCurrentRefreshInterval();
      intervalRef.current = setInterval(fetchData, refreshInterval);

      return () => {
        if (intervalRef.current) {
          clearInterval(intervalRef.current);
          intervalRef.current = null;
        }
        if (abortControllerRef.current) {
          abortControllerRef.current.abort();
        }
      };
    }
  }, [isProcessingLogs, mockMode, apiDownloadCount, timeRange]);

  // Mock mode changes
  useEffect(() => {
    if (mockMode) {
      // Clear any existing interval
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }

      // Clear real data
      setCacheInfo(null);
      setActiveDownloads([]);
      setLatestDownloads([]);
      setClientStats([]);
      setServiceStats([]);

      // Generate mock data (capped at 100)
      const actualCount =
        mockDownloadCount === 'unlimited' ? 100 : Math.min(Number(mockDownloadCount), 100);
      const mockData = MockDataService.generateMockData(actualCount);
      setCacheInfo(mockData.cacheInfo);
      setActiveDownloads(mockData.activeDownloads);
      setLatestDownloads(mockData.latestDownloads);
      setClientStats(mockData.clientStats);
      setServiceStats(mockData.serviceStats);
      setError(null);
      setConnectionStatus('connected');
      hasData.current = true;

      // Set up mock update interval
      const updateInterval = 30000;
      intervalRef.current = setInterval(() => {
        const newDownload = MockDataService.generateRealtimeUpdate();
        setLatestDownloads((prev) => {
          const maxCount = 100;
          return [newDownload, ...prev.slice(0, maxCount - 1)];
        });

        setActiveDownloads((prev) => {
          const updated = [newDownload, ...prev.filter((d) => d.id !== newDownload.id)];
          return updated.slice(0, 5);
        });
      }, updateInterval);

      return () => {
        if (intervalRef.current) {
          clearInterval(intervalRef.current);
          intervalRef.current = null;
        }
      };
    } else {
      // Clear mock data and fetch real data
      setCacheInfo(null);
      setActiveDownloads([]);
      setLatestDownloads([]);
      setClientStats([]);
      setServiceStats([]);
      setError(null);
      hasData.current = false;
      isInitialLoad.current = true;

      fetchData();
    }
  }, [mockMode, mockDownloadCount]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
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
    hasData.current = false;
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
    getCurrentRefreshInterval
  };

  return <DataContext.Provider value={value}>{children}</DataContext.Provider>;
};
