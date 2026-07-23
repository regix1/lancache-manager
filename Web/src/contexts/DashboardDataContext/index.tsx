import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import ApiService from '@services/api.service';
import { isAbortError } from '@utils/error';
import { EMPTY_CACHED_DETECTION, buildDetectionLookupMaps } from '@utils/gameDetection';
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
  GameDetectionSummary,
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
import {
  applyDashboardBatchResponse,
  buildRangeKey,
  type DashboardSlices
} from './applyBatchResponse';
import { APP_EVENTS } from '@utils/constants';

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

  // State. All 9 dashboard fields start empty and populate from the first batch fetch.
  const [cacheInfo, setCacheInfo] = useState<CacheInfo | null>(null);
  const [clientStats, setClientStats] = useState<ClientStat[]>([]);
  const [serviceStats, setServiceStats] = useState<ServiceStat[]>([]);
  const [dashboardStats, setDashboardStats] = useState<DashboardStats | null>(null);
  const [latestDownloads, setLatestDownloads] = useState<Download[]>([]);
  const [gameDetectionData, setGameDetectionData] = useState<CachedDetectionResponse | null>(null);
  const [gameDetectionLookup, setGameDetectionLookup] = useState<Map<
    number,
    GameDetectionSummary
  > | null>(null);
  const [gameDetectionByName, setGameDetectionByName] = useState<Map<
    string,
    GameDetectionSummary
  > | null>(null);
  const [gameDetectionByService, setGameDetectionByService] = useState<Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null>(null);
  const [sparklines, setSparklines] = useState<SparklineDataResponse | null>(null);
  const [hourlyActivity, setHourlyActivity] = useState<HourlyActivityResponse | null>(null);
  const [cacheSnapshot, setCacheSnapshot] = useState<CacheSnapshotResponse | null>(null);
  const [cacheGrowth, setCacheGrowth] = useState<CacheGrowthResponse | null>(null);

  const [loading, setLoading] = useState(true);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [connectionStatus, setConnectionStatus] = useState('checking');
  // True while the latest batch had failed sections (kept or cleared slices);
  // cleared again by the next fully successful apply.
  const [dataStale, setDataStale] = useState(false);

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
  const lastRefreshFetchRef = useRef<number>(0);
  // Separate timer for dedicated always-refresh events (eviction scan/removal):
  // they bypass the live-only gate but still coalesce bursts into one fetch.
  const forcedRefreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const applyDetectionFromBatch = useCallback((detection: CachedDetectionResponse) => {
    setGameDetectionData(detection);
    const { byAppId, byName, byService } = buildDetectionLookupMaps(detection);
    setGameDetectionLookup(byAppId);
    setGameDetectionByName(byName);
    setGameDetectionByService(byService);
  }, []);

  const clearDetectionState = useCallback(() => {
    applyDetectionFromBatch(EMPTY_CACHED_DETECTION);
  }, [applyDetectionFromBatch]);
  const prevEventIdsRef = useRef<string>(JSON.stringify(selectedEventIds));
  const currentRequestIdRef = useRef(0);
  // Range key of the currently displayed batch slices; a failed section only
  // keeps previous data when a fetch targets this same range.
  const appliedRangeKeyRef = useRef<string | null>(null);

  // IMPORTANT: These refs are updated on every render BEFORE effects run
  // This ensures that any function reading from these refs gets the current value
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const getRefreshIntervalRef = useRef(getRefreshInterval);
  const mockModeRef = useRef(mockMode);
  const selectedEventIdsRef = useRef<number[]>(selectedEventIds);
  const authLoadingRef = useRef(authLoading);
  const hasAccessRef = useRef(hasAccess);
  const slicesRef = useRef<DashboardSlices>({
    cacheInfo,
    clientStats,
    serviceStats,
    dashboardStats,
    latestDownloads,
    sparklines,
    hourlyActivity,
    cacheSnapshot,
    cacheGrowth
  });

  // Update refs synchronously on every render
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  getRefreshIntervalRef.current = getRefreshInterval;
  mockModeRef.current = mockMode;
  selectedEventIdsRef.current = selectedEventIds;
  authLoadingRef.current = authLoading;
  hasAccessRef.current = hasAccess;
  slicesRef.current = {
    cacheInfo,
    clientStats,
    serviceStats,
    dashboardStats,
    latestDownloads,
    sparklines,
    hourlyActivity,
    cacheSnapshot,
    cacheGrowth
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

      // Abort any in-flight request BEFORE checking concurrent flag.
      // EXCEPTION: when the in-flight request is the initial REST hydrate and the
      // new caller is NOT a forced refresh (e.g., a SignalR-triggered refetch that
      // lands within the first 250-1000ms of mount), do NOT abort the initial.
      // Otherwise the initial batch is canceled, the UI waits for the 2nd request,
      // and the first paint shows empty placeholders. Supersession via
      // currentRequestIdRef still prevents out-of-order state writes.
      const isAbortingInitialDueToSignalR = isInitialLoad.current && !forceRefresh;
      if (!isAbortingInitialDueToSignalR && abortControllerRef.current) {
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
      // Backend IMemoryCache dedupes identical in-flight requests (15s live / 60s historical TTL).

      const requestController = new AbortController();
      abortControllerRef.current = requestController;
      const signal = requestController.signal;
      let timeoutId: ReturnType<typeof setTimeout> | null = null;

      try {
        // Show skeleton only for user-initiated fetches (initial load, time range change).
        // Background updates (SignalR live data, auto-refresh) update data silently.
        if (showLoading) {
          setLoading(true);
        }

        const timeout = 10000;
        timeoutId = setTimeout(() => requestController.abort(), timeout);

        // Single batch endpoint replaces 6 individual API calls
        const eventId = eventIds && eventIds.length > 0 ? eventIds[0] : undefined;
        const batchResponse: DashboardBatchResponse = await ApiService.getDashboardBatch(
          signal,
          startTime,
          endTime,
          eventId
        );

        // CRITICAL: Check if we're still the current request before modifying ANY state
        if (currentRequestIdRef.current !== thisRequestId) {
          return; // A newer request has started, don't touch state
        }

        // requestId check above already ensures we're the latest request.
        // No additional filter validation needed - if requestId matches, this data is current.

        // Game detection data is not time-range dependent - apply only when the
        // sub-query succeeded (the apply helper records the failure otherwise).
        if (batchResponse.detection !== null && batchResponse.detection !== undefined) {
          applyDetectionFromBatch(batchResponse.detection);
        }

        // Resolve each slice under the wire contract (null = failed sub-query,
        // empty = successful empty): keep previous data on failure within the
        // same range, clear on a range change, apply successful results. [13]
        const rangeKey = buildRangeKey(startTime, endTime, eventId);
        const { next, hadPartialFailure, failedSectionKeys } = applyDashboardBatchResponse(
          slicesRef.current,
          batchResponse,
          { rangeKey, previousRangeKey: appliedRangeKeyRef.current }
        );
        appliedRangeKeyRef.current = rangeKey;

        // Apply state updates directly - React 18+ auto-batches setState in
        // async handlers/microtasks, so no explicit transition wrapper is needed.
        // Kept sections pass their previous reference back, so React bails out
        // of those updates.
        setCacheInfo(next.cacheInfo);
        setClientStats(next.clientStats);
        setServiceStats(next.serviceStats);
        setDashboardStats(next.dashboardStats);
        if (batchResponse.dashboard) {
          hasData.current = true;
        }
        setLatestDownloads(next.latestDownloads);
        setSparklines(next.sparklines);
        setHourlyActivity(next.hourlyActivity);
        setCacheSnapshot(next.cacheSnapshot);
        setCacheGrowth(next.cacheGrowth);

        setConnectionStatus('connected');
        // A partial apply clears any prior hard error; the stale flag is now the
        // degradation signal, so stale data never appears silently healthy. [35]
        setError(null);
        if (hadPartialFailure) {
          console.warn('Dashboard batch returned failed sections:', failedSectionKeys);
          setDataStale(true);
        } else {
          setDataStale(false);
        }
        setLoading(false);
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
        if (timeoutId !== null) {
          clearTimeout(timeoutId);
        }
        if (abortControllerRef.current === requestController) {
          abortControllerRef.current = null;
        }
        // Always clear fetch flags - even for superseded requests.
        // Only the requestId guard on STATE UPDATES (above) prevents stale data.
        // Flags must always reset or subsequent fetches get permanently blocked.
        const wasSuperseded = currentRequestIdRef.current !== thisRequestId;
        // Clear initial-load flag unconditionally for any initial request. If this
        // request was superseded, the superseding request has taken over - we're
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
        // above - no flicker.
        if (!wasSuperseded) {
          setLoading(false);
        }
      }
    },
    [applyDetectionFromBatch]
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

    // The refresh-rate setting IS the live update interval: LIVE (0) -> 500ms (instant), otherwise
    // the chosen interval (e.g. 10s). Leading+trailing THROTTLE — fire immediately if the interval
    // has elapsed since the last fetch, else schedule one trailing fetch for the remainder. Unlike a
    // plain trailing debounce this fires ON SCHEDULE during a continuous download (the old debounce
    // reset on every ~1s tick and starved at 10s, looking frozen) AND still catches the final update.
    // For historical ranges (not 'live'), skip SignalR refreshes to prevent flickering.
    const handleRefreshEvent = (eventName?: string) => {
      if (currentTimeRangeRef.current !== 'live') return;
      const interval = getRefreshIntervalRef.current() || 500;
      const runFetch = () => {
        lastRefreshFetchRef.current = Date.now();
        // Force the fetch: a server refresh event means committed rows exist, so this
        // request must supersede any in-flight batch that may have started before the
        // commit (the requestId guard then discards the superseded response). A non-forced
        // call here could be swallowed by the 250ms debounce or the in-progress guard and
        // leave a pre-commit response as the final state.
        fetchAllData({ forceRefresh: true, trigger: `signalr:${eventName || 'unknown'}` });
      };
      const elapsed = Date.now() - lastRefreshFetchRef.current;
      if (refreshDebounceTimerRef.current) clearTimeout(refreshDebounceTimerRef.current);
      if (elapsed >= interval) {
        runFetch();
      } else {
        refreshDebounceTimerRef.current = setTimeout(runFetch, interval - elapsed);
      }
    };

    // Handler for database reset — clear stale dashboard slices as tables are wiped
    const handleDatabaseResetProgress = (event: { status?: string; stageKey?: string }) => {
      const status = (event.status || '').toLowerCase();
      const stageKey = event.stageKey;

      if (status === 'starting' || status === 'deleting') {
        if (stageKey === 'signalr.dbReset.clearedGameDetections') {
          clearDetectionState();
        }

        if (
          stageKey === 'signalr.dbReset.clearedDownloads' ||
          stageKey === 'signalr.dbReset.clearedServiceStats' ||
          stageKey === 'signalr.dbReset.clearedClientStats'
        ) {
          // The dataset is being wiped from under the current range key; the next apply must treat every section as fresh, not "keep previous" filler from the pre-reset session. [32]
          appliedRangeKeyRef.current = null;
          setDataStale(false);
          setServiceStats([]);
          setClientStats([]);
          setLatestDownloads([]);
        }
      }

      if (status === 'completed') {
        void fetchAllData({ trigger: 'signalr:DatabaseResetCompleted' });
      }
    };

    const handleCacheClearingComplete = () => {
      clearDetectionState();
      handleRefreshEvent('CacheClearingComplete');
    };

    // Handler for game detection completion - always refresh game detection data
    // regardless of the current time range (detection data is not time-range dependent)
    const handleGameDetectionComplete = () => {
      fetchAllData({ forceRefresh: true, trigger: 'signalr:GameDetectionComplete' });
    };

    // Eviction scan/removal completions change detection + evicted data, which
    // (like game detection) is not time-range dependent — they must refresh even
    // outside the 'live' range, so they bypass handleRefreshEvent's live-only
    // gate. They still coalesce through their own debounce timer: per-entity
    // removals fired in quick succession and scheduled automatic scans must not
    // each trigger an undebounced full batch fetch (heavy on small hosts).
    const handleForcedRefreshEvent = (eventName: string) => {
      if (forcedRefreshTimerRef.current) clearTimeout(forcedRefreshTimerRef.current);
      forcedRefreshTimerRef.current = setTimeout(
        () => fetchAllData({ forceRefresh: true, trigger: `signalr:${eventName}` }),
        1000
      );
    };

    // Events with dedicated handlers — the keys of this map drive both the
    // registration below and their exclusion from the debounced live-only list,
    // so adding an entry here is the single edit site.
    const dedicatedHandlers: Record<string, () => void> = {
      GameDetectionComplete: handleGameDetectionComplete,
      CacheClearingComplete: handleCacheClearingComplete,
      EvictionScanComplete: () => handleForcedRefreshEvent('EvictionScanComplete'),
      EvictionRemovalComplete: () => handleForcedRefreshEvent('EvictionRemovalComplete')
    };
    const throttledEvents = SIGNALR_REFRESH_EVENTS.filter((event) => !(event in dedicatedHandlers));
    const eventHandlers: Record<string, () => void> = {};
    throttledEvents.forEach((event) => {
      eventHandlers[event] = () => handleRefreshEvent(event);
      signalR.on(event, eventHandlers[event]);
    });
    Object.entries(dedicatedHandlers).forEach(([event, handler]) => {
      signalR.on(event, handler);
    });
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      // Use the same handler references for cleanup
      throttledEvents.forEach((event) => {
        signalR.off(event, eventHandlers[event]);
      });
      Object.entries(dedicatedHandlers).forEach(([event, handler]) => {
        signalR.off(event, handler);
      });
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      if (forcedRefreshTimerRef.current) {
        clearTimeout(forcedRefreshTimerRef.current);
        forcedRefreshTimerRef.current = null;
      }
      // Clear any pending debounce timer on unmount
      if (refreshDebounceTimerRef.current) {
        clearTimeout(refreshDebounceTimerRef.current);
        refreshDebounceTimerRef.current = null;
      }
    };
  }, [mockMode, signalR, fetchAllData, clearDetectionState]);

  // Load mock data when mock mode is enabled
  useEffect(() => {
    if (mockMode) {
      const mockData = MockDataService.generateMockData('unlimited');
      const mockDetection = MockDataService.generateMockGameDetection();
      const { byAppId, byName, byService } = buildDetectionLookupMaps(mockDetection);

      // React 18+ auto-batches setState calls in event handlers; no transition needed.
      setLoading(true);
      setConnectionStatus('connected');
      setCacheInfo(mockData.cacheInfo);
      setClientStats(mockData.clientStats);
      setServiceStats(mockData.serviceStats);
      setDashboardStats(mockData.dashboardStats);
      setLatestDownloads(mockData.latestDownloads);
      setHourlyActivity(MockDataService.generateMockHourlyActivity());
      setGameDetectionData(mockDetection);
      setGameDetectionLookup(byAppId);
      setGameDetectionByName(byName);
      setGameDetectionByService(byService);
      setError(null);
      setLoading(false);

      hasData.current = true;
      isInitialLoad.current = false;
    }
  }, [mockMode]);

  // Mock and real sessions must not let a partial response after the switch reuse the other session's slices for a matching range key. [32]
  const prevMockModeRef = useRef(mockMode);
  useEffect(() => {
    if (prevMockModeRef.current !== mockMode) {
      appliedRangeKeyRef.current = null;
      setDataStale(false);
    }
    prevMockModeRef.current = mockMode;
  }, [mockMode]);

  // Reset stale refs when access is lost (logout) so that re-login triggers
  // a clean initial load instead of racing with the time range change effect.
  const prevHasAccessRef = useRef(hasAccess);
  useEffect(() => {
    if (prevHasAccessRef.current && !hasAccess) {
      // Access lost - reset to initial state so the next login starts clean
      isInitialLoad.current = true;
      hasData.current = false;
      // A stale range key or stale flag from the ended session must not survive into the next login. [32]
      appliedRangeKeyRef.current = null;
      setDataStale(false);
    }
    prevHasAccessRef.current = hasAccess;
  }, [hasAccess]);

  // Initial load
  useEffect(() => {
    if (!mockMode && !authLoading && hasAccess) {
      fetchAllData({ showLoading: true, isInitial: true, trigger: 'initial' });
    } else if (!mockMode && !authLoading && !hasAccess) {
      // Auth completed but user has no access - stop loading to prevent infinite skeleton
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

    window.addEventListener(APP_EVENTS.SIGNALR_RECONNECTED, handleSignalRReconnected);
    return () => {
      window.removeEventListener(APP_EVENTS.SIGNALR_RECONNECTED, handleSignalRReconnected);
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

  // Background prefetch was REMOVED - caching 6 batch responses simultaneously
  // OOM-crashed the browser tab. The backend IMemoryCache (60s for non-live,
  // 15s for live) handles range-switch caching at the server level.

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
      dataStale,
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
      dataStale,
      refreshData,
      updateData
    ]
  );

  return <DashboardDataContext.Provider value={value}>{children}</DashboardDataContext.Provider>;
};
