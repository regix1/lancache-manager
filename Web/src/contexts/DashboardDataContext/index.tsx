import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import { isAbortError } from '@utils/error';
import { EMPTY_CACHED_DETECTION, buildDetectionLookupMaps } from '@utils/gameDetection';
import MockDataService from '../../test/mockData.service';
import { useTimeFilter } from '../useTimeFilter';
import { useRefreshRate } from '../useRefreshRate';
import { useRefreshThrottle } from '@hooks/useRefreshThrottle';
import { useSignalR } from '../SignalRContext/useSignalR';
import { useAuth } from '../useAuth';
import {
  SIGNALR_REFRESH_EVENTS,
  isSkippedRun,
  type CacheClearCompleteEvent,
  type EvictionRemovalCompleteEvent,
  type EvictionScanCompleteEvent,
  type GameDetectionCompleteEvent,
  type EventHandler
} from '../SignalRContext/types';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  Event,
  GameDetectionSummary,
  SparklineDataResponse,
  HourlyActivityResponse,
  CacheSnapshotResponse
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
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getEffectiveTimezone } from '@utils/timezone';
import { useTimezone } from '@contexts/useTimezone';

// Null is how the wire spells a failed sub-query, so a thrown fetch applies as a total failure.
const FAILED_BATCH: DashboardBatchResponse = {
  cache: null,
  clients: null,
  services: null,
  dashboard: null,
  downloads: null,
  detection: null,
  sparklines: null,
  hourlyActivity: null,
  cacheSnapshot: null
};

export const DashboardDataProvider: React.FC<DashboardDataProviderProps> = ({
  children,
  mockMode = false
}) => {
  const {
    getTimeRangeParams,
    timeRange,
    customStartDate,
    customEndDate,
    selectedEventIds,
    setSelectedEventIds,
    setTimeRange
  } = useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  const signalR = useSignalR();
  const { hasSession, isLoading: authLoading } = useAuth();
  const hasAccess = hasSession;

  // State. Dashboard fields start empty and populate from the first batch fetch.
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

  const [loading, setLoading] = useState(true);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [connectionStatus, setConnectionStatus] = useState('checking');
  // True while the latest batch had failed sections (kept or cleared slices);
  // cleared again by the next fully successful apply.
  const [dataStale, setDataStale] = useState(false);
  // Widgets read their own key to tell a failed sub-query from a successful empty result.
  const [failedSectionKeys, setFailedSectionKeys] = useState<(keyof DashboardBatchResponse)[]>([]);

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
  const scheduleLiveRefresh = useRefreshThrottle(getRefreshInterval);
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

  // Single writer for the batch-owned slices, so the failed path cannot drift from the successful
  // one. Kept sections pass their previous reference back, so React bails out of those.
  const applySlices = useCallback((slices: DashboardSlices) => {
    setCacheInfo(slices.cacheInfo);
    setClientStats(slices.clientStats);
    setServiceStats(slices.serviceStats);
    setDashboardStats(slices.dashboardStats);
    setLatestDownloads(slices.latestDownloads);
    setSparklines(slices.sparklines);
    setHourlyActivity(slices.hourlyActivity);
    setCacheSnapshot(slices.cacheSnapshot);
  }, []);
  const prevEventIdsRef = useRef<string>(JSON.stringify(selectedEventIds));
  const currentRequestIdRef = useRef(0);
  // Range key of the currently displayed batch slices; a failed section only
  // keeps previous data when a fetch targets this same range.
  const appliedRangeKeyRef = useRef<string | null>(null);

  // IMPORTANT: These refs are updated on every render BEFORE effects run
  // This ensures that any function reading from these refs gets the current value
  const currentTimeRangeRef = useRef<string>(timeRange);
  const getTimeRangeParamsRef = useRef(getTimeRangeParams);
  const mockModeRef = useRef(mockMode);
  const selectedEventIdsRef = useRef<number[]>(selectedEventIds);
  const setSelectedEventIdsRef = useRef(setSelectedEventIds);
  const setTimeRangeRef = useRef(setTimeRange);
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
    cacheSnapshot
  });

  // Update refs synchronously on every render
  currentTimeRangeRef.current = timeRange;
  getTimeRangeParamsRef.current = getTimeRangeParams;
  mockModeRef.current = mockMode;
  selectedEventIdsRef.current = selectedEventIds;
  setSelectedEventIdsRef.current = setSelectedEventIds;
  setTimeRangeRef.current = setTimeRange;
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
    cacheSnapshot
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

      // Prevent concurrent fetches (except for initial load or force refresh). Checked before the
      // abort below, since a caller that returns here starts nothing to replace the aborted results.
      if (fetchInProgress.current && !isInitial && !forceRefresh) {
        return;
      }

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

      fetchInProgress.current = true;

      // Generate unique request ID - only this request can modify state
      const thisRequestId = ++currentRequestIdRef.current;

      // Read current values from refs - these are always up-to-date
      // IMPORTANT: Capture these at fetch start to detect stale data when fetch completes
      const currentEventIds = [...selectedEventIdsRef.current];
      const { startTime, endTime } = getTimeRangeParamsRef.current();
      const eventIds = currentEventIds.length > 0 ? currentEventIds : undefined;
      const eventId = eventIds && eventIds.length > 0 ? eventIds[0] : undefined;
      const rangeKey = buildRangeKey(startTime, endTime, eventId);
      // A range change writes no slice here: clearing the snapshot at fetch start repainted the
      // used-space card for the length of the request and then put it back. The range check in the
      // apply below is what keeps one window's snapshot off another window's label.
      // Backend IMemoryCache dedupes identical in-flight requests (15s live / 60s historical TTL).

      const requestController = new AbortController();
      abortControllerRef.current = requestController;
      const signal = requestController.signal;
      let timeoutId: ReturnType<typeof setTimeout> | null = null;
      let timedOut = false;

      try {
        // Show skeleton only for user-initiated fetches (initial load, time range change).
        // Background updates (SignalR live data, auto-refresh) update data silently.
        if (showLoading) {
          setLoading(true);
        }

        const timeout = 10000;
        timeoutId = setTimeout(() => {
          timedOut = true;
          requestController.abort();
        }, timeout);

        // Single batch endpoint replaces 6 individual API calls
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
        // same range, clear on a range change, apply successful results.
        const { next, hadPartialFailure, failedSectionKeys } = applyDashboardBatchResponse(
          slicesRef.current,
          batchResponse,
          { rangeKey, previousRangeKey: appliedRangeKeyRef.current }
        );
        appliedRangeKeyRef.current = rangeKey;

        // Apply state updates directly - React 18+ auto-batches setState in
        // async handlers/microtasks, so no explicit transition wrapper is needed.
        applySlices(next);
        if (batchResponse.dashboard) {
          hasData.current = true;
        }

        setConnectionStatus('connected');
        // A partial apply clears any prior hard error; the stale flag is now the
        // degradation signal, so stale data never appears silently healthy.
        setError(null);
        // Published even when none failed, so a recovered section drops the old failure.
        setFailedSectionKeys(failedSectionKeys);
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
        // A 404 with an event selected is ambiguous: it is what the server returns when that
        // event was deleted mid-flight, but it is also what a misrouted proxy or an API predating
        // this endpoint returns for every request. The error body carries nothing that tells the
        // two apart - NotFoundException collapses to plain "{resource} not found" text, and the
        // house error-handling standard forbids branching on message text - so confirm the id is
        // actually gone before touching the selection, instead of assuming from the status alone.
        if (err instanceof ApiError && err.status === 404 && currentEventIds.length > 0) {
          const missingEventId = currentEventIds[0];
          let missingEventConfirmed = false;
          try {
            const allEvents = await ApiService.getEvents(signal);
            if (currentRequestIdRef.current !== thisRequestId) {
              return; // A newer request has started, don't touch state
            }
            missingEventConfirmed = !allEvents.some((event: Event) => event.id === missingEventId);
          } catch {
            if (currentRequestIdRef.current !== thisRequestId) {
              return; // A newer request has started, don't touch state
            }
            // Could not confirm either way, an abort included. The id check above already handled
            // supersession, so an abort here is this request's own 10s timeout or the initial-load
            // cleanup, neither of which claims a new id. Leaving missingEventConfirmed false routes
            // the original 404 into the normal error path rather than dropping it silently.
          }
          if (missingEventConfirmed) {
            // Confirmed gone: drop only the id the request carried. Events still selected
            // alongside it exist, so their filter and the chosen time range stay as they are;
            // only an emptied selection returns the dashboard to the live view, which is the
            // same rule the prune effect applies so both recoveries end in the same state.
            // The [selectedEventIds] effect refetches from here.
            const remainingEventIds = selectedEventIdsRef.current.filter(
              (id: number) => id !== missingEventId
            );
            if (remainingEventIds.length !== selectedEventIdsRef.current.length) {
              setSelectedEventIdsRef.current(remainingEventIds);
            }
            if (remainingEventIds.length === 0 && currentTimeRangeRef.current !== 'live') {
              setTimeRangeRef.current('live');
            }
            return;
          }
        }
        // Its own 10s timeout aborts too, and an API that never answers is not a cancellation.
        if (!isAbortError(err) || timedOut) {
          setConnectionStatus('disconnected');
          if (!hasData.current) {
            // Read for truthiness only and never rendered as text, so this is a flag rather
            // than a message. Consumers switch on `error || failed` and show their own copy.
            setError('Failed to fetch dashboard data from API'); // i18n-exempt
          }
          // Figures for the range already displayed are kept, so a blip does not blank a working
          // dashboard; figures for a range the fetch never reached are cleared on rangeKey.
          const failedApply = applyDashboardBatchResponse(slicesRef.current, FAILED_BATCH, {
            rangeKey,
            previousRangeKey: appliedRangeKeyRef.current
          });
          appliedRangeKeyRef.current = rangeKey;
          applySlices(failedApply.next);
          setFailedSectionKeys(failedApply.failedSectionKeys);
          setDataStale(true);
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
    [applyDetectionFromBatch, applySlices]
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
      // Shared with the Retro list through useRefreshThrottle, so every live surface answers on the
      // one refresh-rate setting and a finished download reaches them all at the same moment.
      scheduleLiveRefresh(() => {
        // Force the fetch: a server refresh event means committed rows exist, so this
        // request must supersede any in-flight batch that may have started before the
        // commit (the requestId guard then discards the superseded response). A non-forced
        // call here could be swallowed by the 250ms debounce or the in-progress guard and
        // leave a pre-commit response as the final state.
        fetchAllData({ forceRefresh: true, trigger: `signalr:${eventName || 'unknown'}` });
      });
    };

    // Handler for database reset — clear stale dashboard slices as tables are wiped
    const handleDatabaseResetProgress = (event: { status?: string; stageKey?: string }) => {
      const status = (event.status || '').toLowerCase();
      const stageKey = event.stageKey;

      if (status === 'starting' || status === 'deleting') {
        if (stageKey === 'signalr.dbReset.clearedGameDetections') {
          clearDetectionState();
        }

        if (stageKey === 'signalr.dbReset.clearedDownloads') {
          // The dataset is being wiped from under the current range key; the next apply must treat every section as fresh, not "keep previous" filler from the pre-reset session.
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

    // Handler for game detection completion - always refresh game detection data
    // regardless of the current time range (detection data is not time-range dependent)
    const handleGameDetectionComplete = (event: GameDetectionCompleteEvent) => {
      if (isSkippedRun(event)) return;
      fetchAllData({ forceRefresh: true, trigger: 'signalr:GameDetectionComplete' });
    };

    // Cache Files (totalFiles / scan timestamp) is also not time-range dependent. A scan
    // started from Schedules must update that card even when the dashboard filter is 24h/7d;
    // the live-only gate on handleRefreshEvent would otherwise leave it stale until reload.
    const handleCacheScanComplete = () => {
      fetchAllData({ forceRefresh: true, trigger: 'signalr:CacheScanComplete' });
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

    const handleCacheClearingComplete = (event: CacheClearCompleteEvent) => {
      if (!event.success || event.cancelled) return;
      clearDetectionState();
      handleForcedRefreshEvent('CacheClearingComplete');
    };

    // Eviction scan/removal: same all-range force-refresh as cache clear so Dashboard,
    // Downloads, and Clients do not keep stale batch data after detection or file removal.
    const handleEvictionScanComplete = (event: EvictionScanCompleteEvent) => {
      if (!event.success || isSkippedRun(event)) return;
      handleForcedRefreshEvent('EvictionScanComplete');
    };

    const handleEvictionRemovalComplete = (event: EvictionRemovalCompleteEvent) => {
      if (!event.success || event.cancelled) return;
      clearDetectionState();
      handleForcedRefreshEvent('EvictionRemovalComplete');
    };

    // Events with dedicated handlers — the keys of this map drive both the
    // registration below and their exclusion from the debounced live-only list,
    // so adding an entry here is the single edit site.
    const dedicatedHandlers: Record<string, EventHandler> = {
      GameDetectionComplete: handleGameDetectionComplete,
      CacheScanComplete: handleCacheScanComplete,
      CacheClearingComplete: handleCacheClearingComplete,
      EvictionScanComplete: handleEvictionScanComplete,
      EvictionRemovalComplete: handleEvictionRemovalComplete,
      // Group membership and row-mode writes restructure which client stats rows exist —
      // one summed row per nickname versus one row per member address, and an address with
      // no mapping always gets its own row — in every time range, not just 'live'. So these
      // bypass handleRefreshEvent's live-only gate. Successive writes are coalesced by
      // handleForcedRefreshEvent's shared debounce.
      ClientGroupCreated: () => handleForcedRefreshEvent('ClientGroupCreated'),
      ClientGroupUpdated: () => handleForcedRefreshEvent('ClientGroupUpdated'),
      ClientGroupDeleted: () => handleForcedRefreshEvent('ClientGroupDeleted'),
      ClientGroupsCleared: () => handleForcedRefreshEvent('ClientGroupsCleared'),
      // A depot mapping or a removal fires once, right after an action the user just took, and
      // changes which rows exist in every time range — a removed game stays on screen on 24h or
      // 7d otherwise. handleRefreshEvent's live-only gate exists to stop a continuous download
      // repainting a historical range, which a single completion cannot do. They still coalesce
      // through handleForcedRefreshEvent's shared debounce.
      DepotMappingComplete: () => handleForcedRefreshEvent('DepotMappingComplete'),
      LogRemovalComplete: () => handleForcedRefreshEvent('LogRemovalComplete'),
      CorruptionRemovalComplete: () => handleForcedRefreshEvent('CorruptionRemovalComplete'),
      ServiceRemovalComplete: () => handleForcedRefreshEvent('ServiceRemovalComplete'),
      GameRemovalComplete: () => handleForcedRefreshEvent('GameRemovalComplete')
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
    };
  }, [mockMode, signalR, fetchAllData, clearDetectionState, scheduleLiveRefresh]);

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
      // Mock mode replaces every slice, so a real section failure must not report over mock data.
      setCacheSnapshot(null);
      setFailedSectionKeys([]);
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

  useEffect(() => {
    if (!mockMode) {
      return;
    }

    const { startTime, endTime } = getTimeRangeParams();
    setSparklines(MockDataService.generateMockSparklines(startTime, endTime));
  }, [customEndDate, customStartDate, getTimeRangeParams, mockMode, timeRange]);

  // Mock and real sessions must not let a partial response after the switch reuse the other session's slices for a matching range key.
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
      // A stale range key or stale flag from the ended session must not survive into the next login.
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

  // Handle time range changes - fetch new data.
  // Gated on the range having actually changed, not on !isInitialLoad.current: that flag clears
  // only in the initial request's finally, so a range change during the first batch was dropped.
  const prevTimeRangeRef = useRef(timeRange);
  useEffect(() => {
    if (!mockMode && hasAccess && prevTimeRangeRef.current !== timeRange) {
      prevTimeRangeRef.current = timeRange;
      // Use forceRefresh to bypass debounce - time range changes should always trigger immediate fetch
      // Keep previous data visible during fetch - update in place when new data arrives.
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

  // The server groups the hourly buckets on the clock the request names and the marker is read
  // from that same clock, so a zone change has to move both. [54]
  //
  // Subscribed to, not read from: without this hook React never re-runs the body and the
  // comparison below waits for an unrelated render. Not destructured either, because the request
  // at api.service.ts:378 reads the preference itself and a second reader here would drift. [62]
  useTimezone();
  const readerZone = getEffectiveTimezone();
  const prevReaderZoneRef = useRef(readerZone);
  useEffect(() => {
    if (!mockMode && hasAccess && prevReaderZoneRef.current !== readerZone) {
      prevReaderZoneRef.current = readerZone;
      // Forced because a batch in flight was grouped on the clock the reader just left, and a plain
      // call would be dropped by the 250ms debounce or the in-progress guard.
      fetchAllData({
        showLoading: false,
        forceRefresh: true,
        trigger: `clockChange:${readerZone}`
      });
    }
  }, [readerZone, mockMode, hasAccess, fetchAllData]);

  // Push events that fired while the socket was down are never replayed, so pull a fresh batch
  // once the hub is live again. Keyed off the connection state, not a reconnect event: a full
  // close followed by a fresh connection reports no reconnect, and that is the path a backgrounded
  // tab takes.
  useReconnectRefetch(signalR.isConnected, () => {
    if (mockMode || !hasAccess) return;
    fetchAllData({
      showLoading: false,
      forceRefresh: true,
      trigger: 'signalr-reconnected'
    });
  });

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
      loading,
      isRefreshing,
      error,
      connectionStatus,
      dataStale,
      failedSectionKeys,
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
      loading,
      isRefreshing,
      error,
      connectionStatus,
      dataStale,
      failedSectionKeys,
      refreshData,
      updateData
    ]
  );

  return <DashboardDataContext.Provider value={value}>{children}</DashboardDataContext.Provider>;
};
