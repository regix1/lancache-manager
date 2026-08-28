import { useCallback, useEffect, useRef, useState } from 'react';

import ApiService, {
  type RetroDownloadDto,
  type RetroDownloadResponse,
  type RetroDownloadQueryParams
} from '@services/api.service';
import { ApiError } from '@services/apiError';
import { SIGNALR_REFRESH_EVENTS, type SignalREventName } from '@contexts/SignalRContext/types';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useRefreshRate } from '@contexts/useRefreshRate';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useRefreshThrottle } from '@hooks/useRefreshThrottle';
import type { HitMissFilter } from './RetroView.types';

interface RetroDownloadsHookOptions {
  /** Active gating - if false, the hook does not fetch. */
  enabled: boolean;
  /** 1-based page. */
  page: number;
  /** Rows per page (server clamps to 1–200). */
  pageSize: number;
  /** Sort token (matches backend switch). */
  sort: string;
  /** Service filter - 'all' or service name. */
  service: string;
  /** Client filter - 'all' or client IP. */
  client: string;
  /** Free-text search. */
  search: string;
  /** Whether to hide 127.0.0.1 / ::1 rows. */
  hideLocalhost: boolean;
  /** Whether to hide zero-byte rows. */
  hideMetadata: boolean;
  /** Whether to hide rows whose game name is unknown / equals the service. */
  hideUnknown: boolean;
  /** Hit/miss bucket filter - 'all', 'hit', or 'miss'. */
  hitMiss: HitMissFilter;
  /** When true, server merges depot rows by game before paginating. */
  groupByGame?: boolean;
  /** When true, server merges all rows for the same service into one row, overriding groupByGame. */
  groupByService?: boolean;
  /** Unix start time (seconds) from header time filter. */
  startTime?: number;
  /** Unix end time (seconds) from header time filter. */
  endTime?: number;
  /** Optional event filter - only downloads tagged to this event. */
  eventId?: number;
}

interface RetroDownloadsHookResult {
  items: RetroDownloadDto[];
  totalItems: number;
  totalPages: number;
  currentPage: number;
  pageSize: number;
  isLoading: boolean;
  isFetching: boolean;
  error: Error | null;
}

const EMPTY_RESPONSE: RetroDownloadResponse = {
  items: [],
  totalItems: 0,
  totalPages: 0,
  currentPage: 1,
  pageSize: 0
};

/**
 * Fired while a download or a log run is still in progress. The card, normal and compact views and
 * the Dashboard answer these only on the Live range (`DashboardDataContext` gates them in
 * `handleRefreshEvent`), and Retro shares the header's range with all of them, so Retro has to make
 * the same distinction or a bounded range leaves it listing rows the other views do not show.
 */
const RETRO_LIVE_ONLY_EVENTS: readonly SignalREventName[] = [
  'DownloadsRefresh',
  'LogProcessingComplete'
];

/**
 * The rest: completions that fire once, after an action, and that the other views answer on every
 * range. Checked against the event union here because `on`/`off` take a plain string and would
 * accept a name that no longer exists.
 */
const RETRO_REFRESH_EVENTS: readonly SignalREventName[] = SIGNALR_REFRESH_EVENTS.filter(
  (eventName) => !RETRO_LIVE_ONLY_EVENTS.includes(eventName)
);

/**
 * Fetch the server-paginated `/api/downloads/retro` endpoint.
 *
 * Strongly-typed self-contained data hook. Previous-response data stays
 * visible while a new page is being fetched (no empty flash) and the hook
 * aborts in-flight requests when keys change.
 */
export function useRetroDownloads(options: RetroDownloadsHookOptions): RetroDownloadsHookResult {
  const {
    enabled,
    page,
    pageSize,
    sort,
    service,
    client,
    search,
    hideLocalhost,
    hideMetadata,
    hideUnknown,
    hitMiss,
    groupByGame,
    groupByService,
    startTime,
    endTime,
    eventId
  } = options;

  const { on, off, isConnected } = useSignalR();
  const { timeRange } = useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  // The same cadence the card, normal and compact views and the Dashboard answer on, read from the
  // one refresh-rate setting, so a finished download reaches every view at the same moment.
  const scheduleReload = useRefreshThrottle(getRefreshInterval);
  const [data, setData] = useState<RetroDownloadResponse>(EMPTY_RESPONSE);
  const [isLoading, setIsLoading] = useState<boolean>(false);
  const [isFetching, setIsFetching] = useState<boolean>(false);
  const [error, setError] = useState<Error | null>(null);
  // Bumped to ask the fetch below for the same page again, the way DownloadAssociationsContext
  // re-asks for its rows. The previous response stays on screen while it runs.
  const [refreshVersion, setRefreshVersion] = useState<number>(0);

  // Preserve previous data across fetches (placeholderData: keepPreviousData semantics).
  const hasInitialDataRef = useRef<boolean>(false);

  // Stable, so the subscription below re-runs only when the connection's own callbacks change.
  const reload = useCallback((): void => {
    setRefreshVersion((version) => version + 1);
  }, []);

  // Events raised while the socket was down are never delivered, so a genuine reconnect refetches
  // rather than leaving whatever was on screen when the connection dropped.
  useReconnectRefetch(isConnected, reload);

  useEffect(() => {
    const handleRefresh = () => {
      // The retro view stays mounted behind display:none once it has been opened, so a hook that is
      // switched off still receives every event. Bumping the version there re-renders the whole
      // list for a fetch that will not run; switching back on refetches by itself.
      if (!enabled) return;
      scheduleReload(reload);
    };

    const handleLiveRefresh = () => {
      if (!enabled || timeRange !== 'live') return;
      scheduleReload(reload);
    };

    RETRO_REFRESH_EVENTS.forEach((eventName) => on(eventName, handleRefresh));
    RETRO_LIVE_ONLY_EVENTS.forEach((eventName) => on(eventName, handleLiveRefresh));

    return () => {
      RETRO_REFRESH_EVENTS.forEach((eventName) => off(eventName, handleRefresh));
      RETRO_LIVE_ONLY_EVENTS.forEach((eventName) => off(eventName, handleLiveRefresh));
    };
  }, [enabled, on, off, reload, scheduleReload, timeRange]);

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const controller = new AbortController();
    const params: RetroDownloadQueryParams = {
      page,
      pageSize,
      sort,
      service,
      client,
      search,
      hideLocalhost,
      showZeroBytes: !hideMetadata,
      hideUnknown,
      hitMiss,
      groupByGame,
      groupByService,
      startTime,
      endTime,
      eventId
    };

    setIsFetching(true);
    if (!hasInitialDataRef.current) {
      setIsLoading(true);
    }

    ApiService.getRetroDownloads(params, controller.signal)
      .then((response) => {
        setData(response);
        hasInitialDataRef.current = true;
        setError(null);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        if (err instanceof Error && err.name === 'AbortError') {
          return;
        }
        if (err instanceof ApiError && err.status === 404) {
          // The tagged event was deleted between selection and this request. The shared
          // selection is pruned elsewhere, so an empty result is the correct state here,
          // not an error banner.
          setData(EMPTY_RESPONSE);
          hasInitialDataRef.current = true;
          setError(null);
          return;
        }
        const normalized = err instanceof Error ? err : new Error(String(err));
        setError(normalized);
      })
      .finally(() => {
        if (controller.signal.aborted) {
          return;
        }
        setIsFetching(false);
        setIsLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [
    enabled,
    page,
    pageSize,
    sort,
    service,
    client,
    search,
    hideLocalhost,
    hideMetadata,
    hideUnknown,
    hitMiss,
    groupByGame,
    groupByService,
    startTime,
    endTime,
    eventId,
    refreshVersion
  ]);

  return {
    items: data.items,
    totalItems: data.totalItems,
    totalPages: data.totalPages,
    currentPage: data.currentPage,
    pageSize: data.pageSize,
    isLoading,
    isFetching,
    error
  };
}
