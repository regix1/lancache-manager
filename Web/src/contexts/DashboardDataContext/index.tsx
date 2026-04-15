import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { getCachedValue, setCachedValue, IDB_KEYS } from '@utils/idbCache';
import ApiService from '@services/api.service';
import { mark as markTiming } from '@utils/timingTracker';
import { computeTimeRangeParams } from '@contexts/TimeFilterContext.utils';
import type { TimeRange } from '@contexts/TimeFilterContext.types';
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

  // State. Only CACHE_INFO and GAME_DETECTION hydrate from IDB (cold-start win);
  // all other fields start empty and populate on first batch fetch (fast enough now).
  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(
    () => getCachedValue<CacheInfo>(IDB_KEYS.CACHE_INFO) ?? null
  );
  const [clientStats, setClientStats] = useState<ClientStat[]>([]);
  const [serviceStats, setServiceStats] = useState<ServiceStat[]>([]);
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(null);
  const [latestDownloads, setLatestDownloads] = useState<Download[]>([]);
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
  // Transient widget data — no IDB hydration; starts empty, populates on first fetch.
  const [sparklines, setSparklines] = useState<SparklineDataResponse | null>(null);
  const [hourlyActivity, setHourlyActivity] = useState<HourlyActivityResponse | null>(null);
  const [cacheSnapshot, setCacheSnapshot] = useState<CacheSnapshotResponse | null>(null);
  const [cacheGrowth, setCacheGrowth] = useState<CacheGrowthResponse | null>(null);

  // loading is false if we have cached CACHE_INFO (pre-loaded before render)
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
      const currentEventIds = [...selectedEventIdsRef.current];
      const { startTime, endTime } = getTimeRangeParamsRef.current();
      const eventIds = currentEventIds.length > 0 ? currentEventIds : undefined;
      // NOTE: we no longer pass a `cacheBust` token on forceRefresh. The 30s
      // TTL single-flight cache (apiCache.getOrFetch) already dedupes; passing
      // cacheBust would bypass warm prefetched entries and double-fetch.
      // The minute-bucket quantization in getTimeRangeParams ensures prefetch
      // and click land on identical keys within the minute.

      abortControllerRef.current = new AbortController();
      const signal = abortControllerRef.current.signal;

      try {
        // Show skeleton only for user-initiated fetches (initial load, time range change).
        // Background updates (SignalR live data, auto-refresh) update data silently.
        if (showLoading) {
          setLoading(true);
        }

        const timeout = 10000;
        const timeoutId = setTimeout(() => abortControllerRef.current?.abort(), timeout);

        // Single batch endpoint replaces 6 individual API calls
        const eventId = eventIds && eventIds.length > 0 ? eventIds[0] : undefined;
        markTiming('fetch-start');
        const batchResponse: DashboardBatchResponse = await ApiService.getDashboardBatch(
          signal,
          startTime,
          endTime,
          eventId
        );
        markTiming('fetch-done');

        clearTimeout(timeoutId);

        // CRITICAL: Check if we're still the current request before modifying ANY state
        if (currentRequestIdRef.current !== thisRequestId) {
          return; // A newer request has started, don't touch state
        }

        // requestId check above already ensures we're the latest request.
        // No additional filter validation needed — if requestId matches, this data is current.

        // Apply state updates directly — React 18+ auto-batches setState in
        // async handlers/microtasks, so no explicit transition wrapper is needed.
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

        // Time-range dependent data — apply unconditionally. A null from a failed
        // sub-query should NOT freeze stale Live values; let it overwrite so the
        // bug surfaces instead of silently preserving old data.
        setClientStats(batchResponse.clients ?? []);
        setServiceStats(batchResponse.services ?? []);
        setDashboardStats(batchResponse.dashboard);
        if (batchResponse.dashboard) {
          hasData.current = true;
        }
        setLatestDownloads(batchResponse.downloads ?? []);
        setSparklines(batchResponse.sparklines);
        setHourlyActivity(batchResponse.hourlyActivity);
        setCacheGrowth(batchResponse.cacheGrowth);
        // cacheSnapshot is null in live mode — only update when backend returns data
        if (batchResponse.cacheSnapshot !== null && batchResponse.cacheSnapshot !== undefined) {
          setCacheSnapshot(batchResponse.cacheSnapshot);
        }

        setConnectionStatus('connected');
        setError(null);
        setLoading(false);

        // Persist only what benefits cold-start. Cached stats shown instantly on mount:
        //   CACHE_INFO — small, shows cache size immediately
        //   GAME_DETECTION — required for game icons to render without flash
        // Everything else (clients, services, dashboard stats, downloads, sparklines,
        // hourlyActivity, cacheGrowth, cacheSnapshot) is NOT persisted — the 30s
        // apiCache handles warm reuse, and each structuredClone during setCachedValue
        // was adding meaningful latency (LATEST_DOWNLOADS is up to 500 rows).
        if (batchResponse.cache) {
          setCachedValue(IDB_KEYS.CACHE_INFO, batchResponse.cache);
        }
        if (batchResponse.detection) {
          setCachedValue(IDB_KEYS.GAME_DETECTION, batchResponse.detection);
        }
        markTiming('state-applied');
      } catch (err: unknown) {
        // Check if we're still the current request before setting error state
        if (currentRequestIdRef.current !== thisRequestId) {
          return; // A newer request has started, don't touch state
        }
        if (!isAbortError(err)) {
          setConnectionStatus('disconnected');
          if (!hasData.current) {
            setError('Failed to fetch dashboard data from API');
          }
        }
        setLoading(false);
      } finally {
        // Always clear fetch flags — even for superseded requests.
        // Only the requestId guard on STATE UPDATES (above) prevents stale data.
        // Flags must always reset or subsequent fetches get permanently blocked.
        const wasSuperseded = currentRequestIdRef.current !== thisRequestId;
        // Clear initial-load flag unconditionally for any initial request. If this
        // request was superseded, the superseding request has taken over — we're
        // no longer in "initial loading" state either way. Leaving this flag stuck
        // at true would break the time-range change effect (which gates fetches on
        // `!isInitialLoad.current`), forcing the user to manually refresh.
        if (isInitial) {
          isInitialLoad.current = false;
        }
        fetchInProgress.current = false;
        setIsRefreshing(false);
        // Safety net: if we're the latest request but the try/catch returned early
        // without clearing loading (e.g., the requestId check at line 276 returned
        // AFTER a rapid re-entry where currentRequestIdRef already bumped past us),
        // ensure loading doesn't get stuck. Idempotent with the urgent setLoading(false)
        // above — no flicker.
        if (!wasSuperseded) {
          setLoading(false);
        }
      }
    },
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

      // React 18+ auto-batches setState calls in event handlers; no transition needed.
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
      // Keep previous values visible and let them update in place when new data arrives;
      // the skeleton would just hide the old values without replacing them until the fetch completes.
      fetchAllData({
        showLoading: false,
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
      // Keep previous data visible during fetch - update in place when new data arrives.
      fetchAllData({
        showLoading: false,
        forceRefresh: true,
        trigger: 'eventFilterChange'
      });
    }
  }, [selectedEventIds, mockMode, hasAccess, fetchAllData]);

  // Refetch authoritative state when the SignalR hub reconnects. Any push
  // events that fired while the socket was down won't be replayed, so we pull
  // a fresh batch to reconcile.
  useEffect(() => {
    if (mockMode || !hasAccess) return;

    const handleSignalRReconnected = () => {
      fetchAllData({
        showLoading: false,
        forceRefresh: true,
        trigger: 'signalr-reconnected'
      });
    };

    window.addEventListener('signalr-reconnected', handleSignalRReconnected);
    return () => {
      window.removeEventListener('signalr-reconnected', handleSignalRReconnected);
    };
  }, [fetchAllData, mockMode, hasAccess]);

  // Custom date changes - immediate fetch, no debounce
  useEffect(() => {
    if (timeRange === 'custom' && !mockMode && hasAccess) {
      if (customStartDate && customEndDate) {
        const datesChanged =
          lastCustomDates.start?.getTime() !== customStartDate.getTime() ||
          lastCustomDates.end?.getTime() !== customEndDate.getTime();

        if (datesChanged) {
          setLastCustomDates({ start: customStartDate, end: customEndDate });
          // Keep previous values visible during fetch - update in place when new data arrives.
          fetchAllData({
            showLoading: false,
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

  // Background sequential prefetcher for non-current time ranges.
  // Warms apiCache so a subsequent time-range click resolves instantly from
  // the 30s single-flight cache. Strict sequential execution (NO parallel
  // fan-out) to protect the 6GB server.
  useEffect(() => {
    if (mockMode) return;
    if (!hasAccess) return;
    if (connectionStatus !== 'connected') {
      // eslint-disable-next-line no-console
      console.log(`[bg-prefetch] skipped — connectionStatus=${connectionStatus}`);
      return;
    }
    if (isInitialLoad.current) {
      // eslint-disable-next-line no-console
      console.log('[bg-prefetch] skipped — initial load not complete');
      return;
    }

    interface NavigatorConnection {
      readonly saveData?: boolean;
      readonly effectiveType?: string;
    }
    const connection = (navigator as Navigator & { connection?: NavigatorConnection }).connection;
    if (connection?.saveData === true) {
      // eslint-disable-next-line no-console
      console.log('[bg-prefetch] skipped — Save-Data enabled');
      return;
    }
    if (connection?.effectiveType !== undefined && connection.effectiveType !== '4g') {
      // eslint-disable-next-line no-console
      console.log(`[bg-prefetch] skipped — slow connection (${connection.effectiveType})`);
      return;
    }

    const RANGES_TO_WARM: readonly TimeRange[] = ['24h', '1h', '6h', '12h', '7d', '30d'];
    const queue: TimeRange[] = RANGES_TO_WARM.filter((r) => r !== timeRange);
    // eslint-disable-next-line no-console
    console.log(`[bg-prefetch] queue start — current=${timeRange}, warming=[${queue.join(', ')}]`);

    let cancelled = false;
    const currentEventId = selectedEventIds[0];

    const scheduleIdle = (fn: () => void): void => {
      const scheduler = (
        globalThis as unknown as {
          scheduler?: {
            postTask?: (cb: () => void, opts?: { priority: string }) => Promise<void>;
          };
        }
      ).scheduler;
      if (scheduler?.postTask) {
        void scheduler.postTask(fn, { priority: 'background' });
        return;
      }
      const rIC = (
        window as Window &
          typeof globalThis & {
            requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number;
          }
      ).requestIdleCallback;
      if (typeof rIC === 'function') {
        rIC(fn, { timeout: 2000 });
        return;
      }
      setTimeout(fn, 1000);
    };

    const waitMs = (ms: number): Promise<void> =>
      new Promise<void>((resolve) => {
        const id = setTimeout(resolve, ms);
        // Best-effort cancel; cancelled flag is checked post-resolve anyway.
        if (cancelled) clearTimeout(id);
      });

    const waitUntilVisible = async (): Promise<void> => {
      if (document.visibilityState === 'visible') return;
      await new Promise<void>((resolve) => {
        const handler = () => {
          if (document.visibilityState === 'visible') {
            document.removeEventListener('visibilitychange', handler);
            resolve();
          }
        };
        document.addEventListener('visibilitychange', handler);
      });
    };

    const run = async (): Promise<void> => {
      for (const range of queue) {
        if (cancelled) return;
        if (document.visibilityState !== 'visible') {
          // eslint-disable-next-line no-console
          console.log(`[bg-prefetch] paused — tab hidden (pending ${range})`);
        }
        await waitUntilVisible();
        if (cancelled) return;

        // Pause while the user's real fetch is in flight.
        if (fetchInProgress.current) {
          // eslint-disable-next-line no-console
          console.log(`[bg-prefetch] waiting — user fetch in flight (pending ${range})`);
        }
        let guard = 0;
        while (fetchInProgress.current && guard < 20) {
          await waitMs(1000);
          if (cancelled) return;
          guard += 1;
        }

        const quantizedNow = Math.floor(Date.now() / 60_000) * 60_000;
        const { startTime, endTime } = computeTimeRangeParams(range, quantizedNow);
        if (startTime === undefined || endTime === undefined) continue;

        // eslint-disable-next-line no-console
        console.log(`[bg-prefetch] → warming ${range}`);
        await new Promise<void>((resolve) => {
          scheduleIdle(() => {
            if (cancelled) {
              resolve();
              return;
            }
            // Fire-and-forget. getDashboardBatch routes through apiCache.getOrFetch
            // for single-flight + TTL dedupe; no outer wrap needed.
            void ApiService.getDashboardBatch(
              new AbortController().signal,
              startTime,
              endTime,
              currentEventId
            ).catch(() => {
              // prefetch errors non-fatal
            });
            resolve();
          });
        });

        await waitMs(1000);
      }
      if (!cancelled) {
        // eslint-disable-next-line no-console
        console.log('[bg-prefetch] queue complete — all ranges warmed');
      }
    };

    void run();

    return () => {
      cancelled = true;
      // eslint-disable-next-line no-console
      console.log('[bg-prefetch] cancelled (effect re-run or unmount)');
    };
  }, [connectionStatus, timeRange, mockMode, hasAccess, selectedEventIds]);

  const updateData = useCallback(
    (updater: {
      cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
      clientStats?: (prev: ClientStat[]) => ClientStat[];
      serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
      dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
      latestDownloads?: (prev: Download[]) => Download[];
    }) => {
      // React 18+ auto-batches setState calls in event handlers; no transition needed.
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
