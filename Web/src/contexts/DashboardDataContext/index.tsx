import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { unstable_batchedUpdates } from 'react-dom';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import { storage } from '@utils/storage';
import { STORAGE_KEYS } from '@utils/constants';
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
  GameCacheInfo
} from '../../types';
import {
  DashboardDataContext,
  type DashboardDataProviderProps,
  type CachedDetectionResponse,
  type DashboardCacheEnvelope
} from './types';

const CACHE_VERSION = '1';

function readCache<T>(key: string, defaultValue: T): T {
  try {
    const envelope = storage.getJSON<DashboardCacheEnvelope<T>>(key);
    if (envelope && envelope.version === CACHE_VERSION) {
      return envelope.data;
    }
  } catch {
    /* ignore corrupted cache */
  }
  return defaultValue;
}

function writeCache<T>(key: string, data: T): void {
  try {
    storage.setJSON(key, {
      data,
      cachedAt: Date.now(),
      version: CACHE_VERSION
    } as DashboardCacheEnvelope<T>);
  } catch {
    /* ignore storage errors */
  }
}

export const DashboardDataProvider: React.FC<DashboardDataProviderProps> = ({
  children,
  mockMode = false
}) => {
  const { getTimeRangeParams, timeRange, customStartDate, customEndDate, selectedEventIds } =
    useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  const signalR = useSignalR();
  const { hasSession, authMode, isLoading: authLoading } = useAuth();
  const hasAccess = hasSession;
  const isAdmin = authMode === 'authenticated';

  // State
  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(() =>
    readCache(STORAGE_KEYS.DASHBOARD_CACHE_INFO, null)
  );
  const [clientStats, setClientStats] = useState<ClientStat[]>(() =>
    readCache(STORAGE_KEYS.DASHBOARD_CLIENT_STATS, [])
  );
  const [serviceStats, setServiceStats] = useState<ServiceStat[]>(() =>
    readCache(STORAGE_KEYS.DASHBOARD_SERVICE_STATS, [])
  );
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(() =>
    readCache(STORAGE_KEYS.DASHBOARD_STATS, null)
  );
  const [latestDownloads, setLatestDownloads] = useState<Download[]>(() =>
    readCache(STORAGE_KEYS.DASHBOARD_LATEST_DOWNLOADS, [])
  );
  const [gameDetectionData, setGameDetectionData] = useState<CachedDetectionResponse | null>(null);
  const [gameDetectionLookup, setGameDetectionLookup] = useState<Map<number, GameCacheInfo> | null>(
    null
  );
  const [gameDetectionByName, setGameDetectionByName] = useState<Map<string, GameCacheInfo> | null>(
    null
  );
  const [gameDetectionByService, setGameDetectionByService] = useState<Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null>(null);
  const [loading, setLoading] = useState(
    () => readCache(STORAGE_KEYS.DASHBOARD_CACHE_INFO, null) === null
  );
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
  const isAdminRef = useRef(isAdmin);

  // Update refs synchronously on every render
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getRefreshIntervalRef.current = getRefreshInterval;
  mockModeRef.current = mockMode;
  selectedEventIdsRef.current = selectedEventIds;
  authLoadingRef.current = authLoading;
  hasAccessRef.current = hasAccess;
  isAdminRef.current = isAdmin;

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

        // Fetch all data in parallel using Promise.allSettled
        // getCacheInfo is always fetched (guest users need Total Cache / Used Space)
        // getCachedGameDetection is admin-only — skip for guest users to avoid 403
        const [cache, clients, services, dashboard, downloads, detection] =
          await Promise.allSettled([
            ApiService.getCacheInfo(signal),
            ApiService.getClientStats(signal, startTime, endTime, eventIds, undefined, cacheBust),
            ApiService.getServiceStats(signal, startTime, endTime, eventIds, cacheBust),
            ApiService.getDashboardStats(signal, startTime, endTime, eventIds, cacheBust),
            ApiService.getLatestDownloads(
              signal,
              'unlimited',
              startTime,
              endTime,
              eventIds,
              cacheBust
            ),
            isAdminRef.current
              ? ApiService.getCachedGameDetection()
              : Promise.resolve(null as CachedDetectionResponse | null)
          ]);

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
        unstable_batchedUpdates(() => {
          // Cache info is not time-range dependent, always apply
          if (cache.status === 'fulfilled' && cache.value !== undefined) {
            const cacheResult = cache.value;
            setCacheInfo(cacheResult);
            writeCache(STORAGE_KEYS.DASHBOARD_CACHE_INFO, cacheResult);
          }

          // Game detection data is not time-range dependent, always apply
          if (
            detection.status === 'fulfilled' &&
            detection.value !== null &&
            detection.value !== undefined
          ) {
            const detectionResult = detection.value as CachedDetectionResponse;
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
          if (filtersStillValid) {
            if (clients.status === 'fulfilled' && clients.value !== undefined) {
              const clientsResult = clients.value;
              setClientStats(clientsResult);
              writeCache(STORAGE_KEYS.DASHBOARD_CLIENT_STATS, clientsResult);
            }
            if (services.status === 'fulfilled' && services.value !== undefined) {
              const servicesResult = services.value;
              setServiceStats(servicesResult);
              writeCache(STORAGE_KEYS.DASHBOARD_SERVICE_STATS, servicesResult);
            }
            if (dashboard.status === 'fulfilled' && dashboard.value !== undefined) {
              const dashResult = dashboard.value;
              setDashboardStats(dashResult);
              writeCache(STORAGE_KEYS.DASHBOARD_STATS, dashResult);
              hasData.current = true;
            }
            if (downloads.status === 'fulfilled' && downloads.value !== undefined) {
              const downloadsResult = downloads.value;
              setLatestDownloads(downloadsResult);
              writeCache(STORAGE_KEYS.DASHBOARD_LATEST_DOWNLOADS, downloadsResult.slice(0, 50));
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
        // Only update fetchInProgress if we're still the current request
        if (currentRequestIdRef.current === thisRequestId) {
          if (isInitial) {
            isInitialLoad.current = false;
          }
          fetchInProgress.current = false;
        }
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
    SIGNALR_REFRESH_EVENTS.forEach((event) => {
      eventHandlers[event] = () => handleRefreshEvent(event);
      signalR.on(event, eventHandlers[event]);
    });
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      // Use the same handler references for cleanup
      SIGNALR_REFRESH_EVENTS.forEach((event) => {
        signalR.off(event, eventHandlers[event]);
      });
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
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
      unstable_batchedUpdates(() => {
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
      const hasCachedData = readCache(STORAGE_KEYS.DASHBOARD_CACHE_INFO, null) !== null;
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
        showLoading: !hasData.current,
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
        showLoading: !hasData.current,
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
            showLoading: !hasData.current,
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
      loading,
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
      loading,
      error,
      connectionStatus,
      refreshData,
      updateData
    ]
  );

  return <DashboardDataContext.Provider value={value}>{children}</DashboardDataContext.Provider>;
};
