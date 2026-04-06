import React, { useState, useEffect, useRef, useCallback, useMemo, startTransition } from 'react';
import { getCachedValue, setCachedValue, IDB_KEYS } from '@utils/idbCache';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import MockDataService from '../../test/mockData.service';
import { useTimeFilter } from '../useTimeFilter';
import { useRefreshRate } from '../useRefreshRate';
import { useSignalR } from '../SignalRContext/useSignalR';
import { useAuth } from '../useAuth';
import { SIGNALR_REFRESH_EVENTS } from '../SignalRContext/types';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  GameCacheInfo,
  SparklineDataResponse,
  HourlyActivityResponse,
  CacheSnapshotResponse,
  CacheGrowthResponse
} from '../../types';
import {
  DashboardDataContext,
  type DashboardDataProviderProps,
  type CachedDetectionResponse,
  type DashboardBatchResponse
} from './types';

export const DashboardDataProvider: React.FC<DashboardDataProviderProps> = ({
  children,
  mockMode = false
}) => {
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate, selectedEventIds } =
    useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  const signalR = useSignalR();
  const { hasSession, isLoading: authLoading } = useAuth();
  const hasAccess = hasSession;

  // State — initializers read from pre-loaded IDB cache (synchronous, no skeleton flash)
  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(
    () => getCachedValue<CacheInfo>(IDB_KEYS.CACHE_INFO) ?? null
  );
  const [clientStats, setClientStats] = useState<ClientStat[]>(
    () => getCachedValue<ClientStat[]>(IDB_KEYS.CLIENT_STATS) ?? []
  );
  const [serviceStats, setServiceStats] = useState<ServiceStat[]>(
    () => getCachedValue<ServiceStat[]>(IDB_KEYS.SERVICE_STATS) ?? []
  );
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(
    () => getCachedValue<DashboardStats>(IDB_KEYS.DASHBOARD_STATS) ?? null
  );
  const [latestDownloads, setLatestDownloads] = useState<Download[]>(
    () => getCachedValue<Download[]>(IDB_KEYS.LATEST_DOWNLOADS) ?? []
  );
  const [gameDetectionData, setGameDetectionData] = useState<CachedDetectionResponse | null>(
    () => getCachedValue<CachedDetectionResponse>(IDB_KEYS.GAME_DETECTION) ?? null
  );
  const [gameDetectionLookup, setGameDetectionLookup] = useState<Map<number, GameCacheInfo> | null>(
    () => {
      const cached = getCachedValue<CachedDetectionResponse>(IDB_KEYS.GAME_DETECTION);
      if (!cached?.games || cached.games.length === 0) return null;
      const byAppId = new Map<number, GameCacheInfo>();
      for (const game of cached.games) {
        if (game.game_app_id) {
          byAppId.set(game.game_app_id, game);
        }
      }
      return byAppId;
    }
  );
  const [gameDetectionByName, setGameDetectionByName] = useState<Map<string, GameCacheInfo> | null>(
    () => {
      const cached = getCachedValue<CachedDetectionResponse>(IDB_KEYS.GAME_DETECTION);
      if (!cached?.games || cached.games.length === 0) return null;
      const byName = new Map<string, GameCacheInfo>();
      for (const game of cached.games) {
        if (game.game_name) {
          byName.set(game.game_name.toLowerCase(), game);
        }
      }
      return byName;
    }
  );
  const [gameDetectionByService, setGameDetectionByService] = useState<Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null>(() => {
    const cached = getCachedValue<CachedDetectionResponse>(IDB_KEYS.GAME_DETECTION);
    if (!cached?.services) return null;
    const bySvc = new Map<
      string,
      { service_name: string; cache_files_found: number; total_size_bytes: number }
    >();
    for (const svc of cached.services) {
      if (svc.service_name) {
        bySvc.set(svc.service_name.toLowerCase(), svc);
      }
    }
    return bySvc;
  });
  // Sparklines & widget data — hydrate from IDB cache
  const [sparklines, setSparklines] = useState<SparklineDataResponse | null>(
    () => getCachedValue<SparklineDataResponse>(IDB_KEYS.SPARKLINES) ?? null
  );
  const [hourlyActivity, setHourlyActivity] = useState<HourlyActivityResponse | null>(
    () => getCachedValue<HourlyActivityResponse>(IDB_KEYS.HOURLY_ACTIVITY) ?? null
  );
  const [cacheSnapshot, setCacheSnapshot] = useState<CacheSnapshotResponse | null>(
    () => getCachedValue<CacheSnapshotResponse>(IDB_KEYS.CACHE_SNAPSHOT) ?? null
  );
  const [cacheGrowth, setCacheGrowth] = useState<CacheGrowthResponse | null>(
    () => getCachedValue<CacheGrowthResponse>(IDB_KEYS.CACHE_GROWTH) ?? null
  );

  // loading is false if we have cached data (pre-loaded before render)
  const [loading, setLoading] = useState(() => getCachedValue(IDB_KEYS.CACHE_INFO) === undefined);
  const [isRefreshing, setIsRefreshing] = useState(false);
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
  const refreshDebounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
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
    } catch (_err) {
      setConnectionStatus('disconnected');
      return false;
    }
  };

  // Single unified fetch function that fetches all data in parallel
  const fetchAllData = useCallback(
    async (
      options: {
        showLoading?: boolean;
        isInitial?: boolean;
        forceRefresh?: boolean;
        trigger?: string;
      } = {}
    ) => {
      if (mockModeRef.current) return;
      if (authLoadingRef.current || !hasAccessRef.current) {
        // If auth resolved but no access, ensure loading is cleared
        if (!authLoadingRef.current && !hasAccessRef.current) {
          setLoading(false);
        }
        return;
      }

      const {
        showLoading = false,
        isInitial = false,
        forceRefresh = false,
        trigger: _trigger
      } = options;

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
        // Show skeleton only for user-initiated fetches (initial load, time range change).
        // Background updates (SignalR live data, auto-refresh) update data silently.
        if (showLoading) {
          setLoading(true);
        }

        const isConnected = await checkConnectionStatus();
        if (!isConnected) {
          if (!hasData.current) {
            setError('Cannot connect to API server');
          }
          setLoading(false);
          return;
        }

        const timeout = 10000;
        const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

        // Single batch endpoint replaces 6 individual API calls
        const eventId = eventIds && eventIds.length > 0 ? eventIds[0] : undefined;
        const batchResponse: DashboardBatchResponse = await ApiService.getDashboardBatch(
          signal,
          startTime,
          endTime,
          eventId,
          cacheBust
        );

        clearTimeout(timeoutId);

        // CRITICAL: Check if we're still the current request before modifying ANY state
        if (currentRequestIdRef.current !== thisRequestId) {
          return; // A newer request has started, don't touch state
        }

        // Only apply results if filters haven't changed during fetch (prevents stale data)
        const timeRangeStillValid = currentTimeRangeRef.current === currentTimeRange;
        const eventIdsStillValid =
          JSON.stringify(selectedEventIdsRef.current) === JSON.stringify(currentEventIds);
        const filtersStillValid = timeRangeStillValid && eventIdsStillValid;

        // Batch all state updates to prevent multiple re-renders
        startTransition(() => {
          // Cache info is not time-range dependent, always apply (skip if server returned null)
          if (batchResponse.cache !== null && batchResponse.cache !== undefined) {
            setCacheInfo(batchResponse.cache);
          }

          // Game detection data is not time-range dependent, always apply
          if (batchResponse.detection !== null && batchResponse.detection !== undefined) {
            const detectionResult = batchResponse.detection;
            setGameDetectionData(detectionResult);
            // Build lookup maps: primary by game_app_id, fallback by game_name
            if (detectionResult.games && detectionResult.games.length > 0) {
              const byAppId = new Map<number, GameCacheInfo>();
              const byName = new Map<string, GameCacheInfo>();
              for (const game of detectionResult.games) {
                if (game.game_app_id) {
                  byAppId.set(game.game_app_id, game);
                }
                if (game.game_name) {
                  byName.set(game.game_name.toLowerCase(), game);
                }
              }
              setGameDetectionLookup(byAppId);
              setGameDetectionByName(byName);

              // Build service-level lookup
              if (detectionResult.services) {
                const bySvc = new Map<
                  string,
                  { service_name: string; cache_files_found: number; total_size_bytes: number }
                >();
                for (const svc of detectionResult.services) {
                  if (svc.service_name) {
                    bySvc.set(svc.service_name.toLowerCase(), svc);
                  }
                }
                setGameDetectionByService(bySvc);
              }
            }
          }

          // All other data depends on time range AND event filter
          // Only update if filters haven't changed; null means server-side sub-query failed — keep stale data
          if (filtersStillValid) {
            if (batchResponse.clients !== null && batchResponse.clients !== undefined) {
              setClientStats(batchResponse.clients);
            }
            if (batchResponse.services !== null && batchResponse.services !== undefined) {
              setServiceStats(batchResponse.services);
            }
            if (batchResponse.dashboard !== null && batchResponse.dashboard !== undefined) {
              setDashboardStats(batchResponse.dashboard);
              hasData.current = true;
            }
            if (batchResponse.downloads !== null && batchResponse.downloads !== undefined) {
              setLatestDownloads(batchResponse.downloads);
            }
            // Sparklines & widget data — time-range dependent
            if (batchResponse.sparklines !== null && batchResponse.sparklines !== undefined) {
              setSparklines(batchResponse.sparklines);
            }
            if (
              batchResponse.hourlyActivity !== null &&
              batchResponse.hourlyActivity !== undefined
            ) {
              setHourlyActivity(batchResponse.hourlyActivity);
            }
            if (batchResponse.cacheGrowth !== null && batchResponse.cacheGrowth !== undefined) {
              setCacheGrowth(batchResponse.cacheGrowth);
            }
            // cacheSnapshot is null in live mode — only update when backend returns data
            if (batchResponse.cacheSnapshot !== null && batchResponse.cacheSnapshot !== undefined) {
              setCacheSnapshot(batchResponse.cacheSnapshot);
            }
            setError(null);
          }
          // Always clear loading when fetch completes — showLoading only controls
          // whether loading is SET to true, not whether it's cleared. This prevents
          // a race where one call sets loading=true but a superseding call with
          // showLoading=false never clears it (e.g. auth transition triggers both
          // the initial load effect and the time range change effect simultaneously).
          setLoading(false);
        });

        // Write to in-memory cache and IndexedDB (fire-and-forget)
        if (batchResponse.cache) {
          setCachedValue(IDB_KEYS.CACHE_INFO, batchResponse.cache);
        }
        if (batchResponse.detection) {
          setCachedValue(IDB_KEYS.GAME_DETECTION, batchResponse.detection);
        }
        if (filtersStillValid) {
          if (batchResponse.clients) setCachedValue(IDB_KEYS.CLIENT_STATS, batchResponse.clients);
          if (batchResponse.services)
            setCachedValue(IDB_KEYS.SERVICE_STATS, batchResponse.services);
          if (batchResponse.dashboard)
            setCachedValue(IDB_KEYS.DASHBOARD_STATS, batchResponse.dashboard);
          if (batchResponse.downloads)
            setCachedValue(IDB_KEYS.LATEST_DOWNLOADS, batchResponse.downloads);
          if (batchResponse.sparklines)
            setCachedValue(IDB_KEYS.SPARKLINES, batchResponse.sparklines);
          if (batchResponse.hourlyActivity)
            setCachedValue(IDB_KEYS.HOURLY_ACTIVITY, batchResponse.hourlyActivity);
          if (batchResponse.cacheGrowth)
            setCachedValue(IDB_KEYS.CACHE_GROWTH, batchResponse.cacheGrowth);
          if (batchResponse.cacheSnapshot)
            setCachedValue(IDB_KEYS.CACHE_SNAPSHOT, batchResponse.cacheSnapshot);
        }
      } catch (err: unknown) {
        // Check if we're still the current request before setting error state
        if (currentRequestIdRef.current !== thisRequestId) {
          return; // A newer request has started, don't touch state
        }
        if (!hasData.current && !isAbortError(err)) {
          setError('Failed to fetch dashboard data from API');
        }
        setLoading(false);
      } finally {
        // Always clear fetch flags — even for superseded requests.
        // Only the requestId guard on STATE UPDATES (above) prevents stale data.
        // Flags must always reset or subsequent fetches get permanently blocked.
        const wasSuperseded = currentRequestIdRef.current !== thisRequestId;
        if (isInitial && !wasSuperseded) {
          isInitialLoad.current = false;
        }
        fetchInProgress.current = false;
        setIsRefreshing(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    []
  );

  // Public refresh function for manual refreshes
  const refreshData = useCallback(
    async (forceRefresh = false) => {
      await fetchAllData({ showLoading: true, forceRefresh });
    },
    [fetchAllData]
  );

  // Subscribe to SignalR events for real-time updates - SINGLE subscription
  useEffect(() => {
    if (mockMode) return;

    // Trailing debounce: any SignalR refresh event resets the 1-second timer.
    // The fetch fires once, 1 second after the last event in a burst.
    // For historical ranges (not 'live'), skip SignalR refreshes to prevent flickering.
    const handleRefreshEvent = (eventName?: string) => {
      if (currentTimeRangeRef.current !== 'live') return;
      const delay = getRefreshIntervalRef.current() || 500;
      if (refreshDebounceTimerRef.current) clearTimeout(refreshDebounceTimerRef.current);
      refreshDebounceTimerRef.current = setTimeout(
        () => fetchAllData({ trigger: `signalr:${eventName || 'unknown'}` }),
        delay
      );
    };

    // Handler for database reset completion - always refresh immediately
    const handleDatabaseResetProgress = (event: { status?: string }) => {
      const status = (event.status || '').toLowerCase();
      if (status === 'completed') {
        setTimeout(() => fetchAllData({ trigger: 'signalr:DatabaseResetCompleted' }), 500);
      }
    };

    // Handler for game detection completion - always refresh game detection data
    // regardless of the current time range (detection data is not time-range dependent)
    const handleGameDetectionComplete = () => {
      fetchAllData({ forceRefresh: true, trigger: 'signalr:GameDetectionComplete' });
    };

    // Create stable handler references for proper cleanup
    // Exclude GameDetectionComplete from the throttled handler since we have a dedicated one
    const throttledEvents = SIGNALR_REFRESH_EVENTS.filter(
      (event) => event !== 'GameDetectionComplete'
    );
    const eventHandlers: Record<string, () => void> = {};
    throttledEvents.forEach((event) => {
      eventHandlers[event] = () => handleRefreshEvent(event);
      signalR.on(event, eventHandlers[event]);
    });
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    signalR.on('GameDetectionComplete', handleGameDetectionComplete);

    return () => {
      // Use the same handler references for cleanup
      throttledEvents.forEach((event) => {
        signalR.off(event, eventHandlers[event]);
      });
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      signalR.off('GameDetectionComplete', handleGameDetectionComplete);
      // Clear any pending debounce timer on unmount
      if (refreshDebounceTimerRef.current) {
        clearTimeout(refreshDebounceTimerRef.current);
        refreshDebounceTimerRef.current = null;
      }
    };
  }, [mockMode, signalR, fetchAllData]);

  // Load mock data when mock mode is enabled
  useEffect(() => {
    if (mockMode) {
      const mockData = MockDataService.generateMockData('unlimited');
      const mockDetection = MockDataService.generateMockGameDetection();

      // Build detection lookup maps: primary by game_app_id, fallback by game_name
      const lookup = new Map<number, GameCacheInfo>();
      const nameLookup = new Map<string, GameCacheInfo>();
      if (mockDetection.games) {
        for (const game of mockDetection.games) {
          if (game.game_app_id) {
            lookup.set(game.game_app_id, game);
          }
          if (game.game_name) {
            nameLookup.set(game.game_name.toLowerCase(), game);
          }
        }
      }

      // Batch all state updates to prevent multiple re-renders
      startTransition(() => {
        setLoading(true);
        setConnectionStatus('connected');
        setCacheInfo(mockData.cacheInfo);
        setClientStats(mockData.clientStats);
        setServiceStats(mockData.serviceStats);
        setDashboardStats(mockData.dashboardStats);
        setLatestDownloads(mockData.latestDownloads);
        setGameDetectionData(mockDetection);
        setGameDetectionLookup(lookup);
        setGameDetectionByName(nameLookup);
        setError(null);
        setLoading(false);
      });

      hasData.current = true;
      isInitialLoad.current = false;
    }
  }, [mockMode]);

  // Reset stale refs when access is lost (logout) so that re-login triggers
  // a clean initial load instead of racing with the time range change effect.
  const prevHasAccessRef = useRef(hasAccess);
  useEffect(() => {
    if (prevHasAccessRef.current && !hasAccess) {
      // Access lost — reset to initial state so the next login starts clean
      isInitialLoad.current = true;
      hasData.current = false;
    }
    prevHasAccessRef.current = hasAccess;
  }, [hasAccess]);

  // Initial load
  useEffect(() => {
    if (!mockMode && !authLoading && hasAccess) {
      const hasCachedData = getCachedValue(IDB_KEYS.CACHE_INFO) !== undefined;
      fetchAllData({ showLoading: !hasCachedData, isInitial: true, trigger: 'initial' });
    } else if (!mockMode && !authLoading && !hasAccess) {
      // Auth completed but user has no access — stop loading to prevent infinite skeleton
      setLoading(false);
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
      fetchAllData({
        showLoading: true,
        forceRefresh: true,
        trigger: `timeRangeChange:${timeRange}`
      });
    }
  }, [timeRange, mockMode, hasAccess, fetchAllData]);

  // Event filter changes - refetch when event filter is changed
  useEffect(() => {
    const currentEventIdsKey = JSON.stringify(selectedEventIds);
    if (!mockMode && hasAccess && prevEventIdsRef.current !== currentEventIdsKey) {
      prevEventIdsRef.current = currentEventIdsKey;
      // Keep previous data visible during fetch - don't clear immediately
      // Only show loading if we don't have existing data to prevent UI flashing
      fetchAllData({
        showLoading: true,
        forceRefresh: true,
        trigger: 'eventFilterChange'
      });
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
          fetchAllData({
            showLoading: true,
            forceRefresh: true,
            trigger: 'customDateChange'
          });
        }
      }
    } else if (timeRange !== 'custom') {
      setLastCustomDates({ start: null, end: null });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [customStartDate, customEndDate, timeRange, mockMode, hasAccess, fetchAllData]);

  const updateData = useCallback(
    (updater: {
      cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
      clientStats?: (prev: ClientStat[]) => ClientStat[];
      serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
      dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
      latestDownloads?: (prev: Download[]) => Download[];
    }) => {
      // Batch all state updates to prevent multiple re-renders
      startTransition(() => {
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
    },
    []
  );

  // Memoize context value to prevent unnecessary re-renders of consumers
  const value = useMemo(
    () => ({
      cacheInfo,
      clientStats,
      serviceStats,
      dashboardStats,
      latestDownloads,
      gameDetectionData,
      gameDetectionLookup,
      gameDetectionByName,
      gameDetectionByService,
      sparklines,
      hourlyActivity,
      cacheSnapshot,
      cacheGrowth,
      loading,
      isRefreshing,
      error,
      connectionStatus,
      refreshData,
      updateData
    }),
    [
      cacheInfo,
      clientStats,
      serviceStats,
      dashboardStats,
      latestDownloads,
      gameDetectionData,
      gameDetectionLookup,
      gameDetectionByName,
      gameDetectionByService,
      sparklines,
      hourlyActivity,
      cacheSnapshot,
      cacheGrowth,
      loading,
      isRefreshing,
      error,
      connectionStatus,
      refreshData,
      updateData
    ]
  );

  return <DashboardDataContext.Provider value={value}>{children}</DashboardDataContext.Provider>;
};
