import { useCallback, useEffect, useRef, useState } from 'react';

import ApiService, {
  type RetroDownloadDto,
  type RetroDownloadResponse,
  type RetroDownloadQueryParams
} from '@services/api.service';
import { ApiError } from '@services/apiError';
import { SIGNALR_REFRESH_EVENTS, type SignalREventName } from '@contexts/SignalRContext/types';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useMockMode } from '@contexts/useMockMode';
import { useTimeFilter } from '@contexts/useTimeFilter';
import { useRefreshRate } from '@contexts/useRefreshRate';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useRefreshThrottle } from '@hooks/useRefreshThrottle';
import MockDataService from '@/test/mockData.service';
import type { HitMissFilter } from './RetroView.types';

interface RetroDownloadsHookOptions {
  /** Active gating - if false, the hook does not fetch. */
  enabled: boolean;
  /** 1-based page. */
  page: number;
  /** Rows per page (server clamps to 1–200), or ALL_ITEMS_PAGE_SIZE for every row. */
  pageSize: number;
  /** Sort token (matches backend switch). */
  sort: string;
  /** Service filter - 'all' or service name. */
  service: string;
  /** Client filter - 'all', or one or more client addresses separated by commas. */
  client: string;
  /** Free-text search. */
  search: string;
  /** Whether to hide 127.0.0.1 / ::1 rows. */
  hideLocalhost: boolean;
  /** Whether to hide zero-byte rows. */
  hideMetadata: boolean;
  /** Whether to hide rows under 1 MB. */
  hideSmallFiles?: boolean;
  /** Whether to hide evicted rows on top of the server's stored evicted-data mode. */
  hideEvicted?: boolean;
  /** Whether to hide rows whose game name is unknown / equals the service. */
  hideUnknown: boolean;
  /** Whether a download that is still running is listed alongside the finished ones. */
  includeActive?: boolean;
  /** Hit/miss bucket filter - 'all', 'hit', or 'miss'. */
  hitMiss: HitMissFilter;
  /** When true, server merges depot rows by game before paginating. */
  groupByGame?: boolean;
  /** When true, server merges all rows for the same service into one row, overriding groupByGame. */
  groupByService?: boolean;
  /** Read with groupByGame. Keys each bucket on the game identity alone, so one title seen under
   *  two services is a single row - the way the grouped Downloads views key theirs. */
  mergeAcrossServices?: boolean;
  /** Read with mergeAcrossServices. Collapses unmapped Steam rows into one Unknown/Other bucket. */
  groupUnknownGames?: boolean;
  /** When true, groups holding more than one download sort ahead of single-download groups. */
  groupByFrequency?: boolean;
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
  /** Downloads behind the groups on every page, for the pager's sub-label. */
  totalDownloads: number;
  totalPages: number;
  currentPage: number;
  pageSize: number;
  isLoading: boolean;
  isFetching: boolean;
  /** True once a request has answered. The totals above are placeholders until then. */
  hasResponse: boolean;
  error: Error | null;
}

const EMPTY_RESPONSE: RetroDownloadResponse = {
  items: [],
  totalItems: 0,
  totalDownloads: 0,
  totalPages: 0,
  currentPage: 1,
  pageSize: 0
};

/**
 * The largest page the endpoint serves; anything above it is clamped down to this. The walk below
 * asks for exactly this much at a time, so no single request makes the server build more rows than
 * one page holds.
 */
const SERVER_PAGE_LIMIT = 200;

/**
 * The page size that stands for "All". It is answered by walking bounded pages and accumulating
 * them here, never by asking the endpoint for the whole table in one request: that request is what
 * drove the service out of memory and took the running prefill sessions down with it.
 */
export const ALL_ITEMS_PAGE_SIZE = 0;

/**
 * Asks for one bounded page at a time and accumulates until the set is exhausted.
 *
 * The rows are collected in this call's own list and handed back in one piece, so a walk that is
 * abandoned partway - the reader changes a filter, the sort, the page size, or leaves the page -
 * has nothing to merge into the results of the selection that replaced it. A request that fails
 * partway rejects with that failure, so the pages gathered so far are never presented as the whole
 * set.
 */
const fetchEveryPage = async (
  params: RetroDownloadQueryParams,
  fetchPage: (pageParams: RetroDownloadQueryParams) => Promise<RetroDownloadResponse>
): Promise<RetroDownloadResponse> => {
  const items: RetroDownloadDto[] = [];
  // The server re-groups and re-sorts the whole set for each of these requests, so a download that
  // commits partway through pushes the rest along and the group that ended one page starts the
  // next. Two rows with one id are one React key, and expanding either would expand both.
  const seen = new Set<string>();
  for (let page = 1; ; page += 1) {
    const response = await fetchPage({ ...params, page, pageSize: SERVER_PAGE_LIMIT });
    for (const item of response.items) {
      if (seen.has(item.id)) continue;
      seen.add(item.id);
      items.push(item);
    }
    if (page >= response.totalPages) {
      return {
        items,
        totalItems: response.totalItems,
        totalDownloads: response.totalDownloads,
        // One page holds every row, so the pager collapses and the two totals above count the
        // whole set rather than a slice of it.
        totalPages: 1,
        currentPage: 1,
        pageSize: items.length
      };
    }
  }
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
    hideSmallFiles,
    hideEvicted,
    hideUnknown,
    includeActive,
    hitMiss,
    groupByGame,
    groupByService,
    mergeAcrossServices,
    groupUnknownGames,
    groupByFrequency,
    startTime,
    endTime,
    eventId
  } = options;

  const { on, off, isConnected } = useSignalR();
  const { mockMode } = useMockMode();
  const { timeRange } = useTimeFilter();
  const { getRefreshInterval } = useRefreshRate();
  // The same cadence the card, normal and compact views and the Dashboard answer on, read from the
  // one refresh-rate setting, so a finished download reaches every view at the same moment.
  const scheduleReload = useRefreshThrottle(getRefreshInterval);
  // Null until a request has answered. An answer that holds no rows is indistinguishable from the
  // placeholder by value, so the difference has to be carried by the absence itself: a reader that
  // acts on the count needs to know it is looking at an answer and not at a fetch that has not run.
  const [data, setData] = useState<RetroDownloadResponse | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(false);
  const [isFetching, setIsFetching] = useState<boolean>(false);
  const [error, setError] = useState<Error | null>(null);
  // Bumped to ask the fetch below for the same page again, the way DownloadAssociationsContext
  // re-asks for its rows. The previous response stays on screen while it runs.
  const [refreshVersion, setRefreshVersion] = useState<number>(0);

  // Preserve previous data across fetches (placeholderData: keepPreviousData semantics).
  const hasInitialDataRef = useRef<boolean>(false);
  // Lets the fetch effect tell a background SignalR-driven refetch (only refreshVersion changed)
  // apart from a user-initiated one (a page/filter/sort dependency changed): the fade below is
  // wanted for the latter and must stay off for the former.
  const prevRefreshVersionRef = useRef<number>(refreshVersion);

  // Stable, so the subscription below re-runs only when the connection's own callbacks change.
  const reload = useCallback((): void => {
    setRefreshVersion((version) => version + 1);
  }, []);

  // Events raised while the socket was down are never delivered, so a genuine reconnect refetches
  // rather than leaving whatever was on screen when the connection dropped.
  useReconnectRefetch(isConnected, reload);

  useEffect(() => {
    // The provider stops the socket and drops its held messages while mock mode is on, so no
    // server event reaches this hook then. Keeping mockMode in the dependency list is what
    // re-subscribes the moment the toggle goes back off.
    if (mockMode) return;

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
  }, [enabled, mockMode, on, off, reload, scheduleReload, timeRange]);

  useEffect(() => {
    // A run where only refreshVersion changed since the last run is a background SignalR refresh;
    // any other changed dependency (page, filter, sort, ...) is user-initiated. Classified above the
    // gate below because the reconnect refetch is not gated on enabled: a bump that lands while the
    // view sits hidden has to be consumed here, or the next run with the view showing reads its own
    // version mismatch as a background refresh and skips the fade it should have.
    const isBackgroundRefresh = refreshVersion !== prevRefreshVersionRef.current;
    prevRefreshVersionRef.current = refreshVersion;

    if (!enabled) {
      // A request that was in flight when this was switched off is aborted by the cleanup below,
      // and the settle returns early on an aborted request, so nothing else can turn these off. Left
      // set, the busy indicator the page draws from isFetching stays on for the rest of the visit.
      setIsFetching(false);
      setIsLoading(false);
      return;
    }

    const isAllItems = pageSize === ALL_ITEMS_PAGE_SIZE;

    const params: RetroDownloadQueryParams = {
      page,
      pageSize,
      sort,
      service,
      client,
      search,
      hideLocalhost,
      showZeroBytes: !hideMetadata,
      hideSmallFiles,
      hideEvicted,
      hideUnknown,
      includeActive,
      hitMiss,
      groupByGame,
      groupByService,
      mergeAcrossServices,
      groupUnknownGames,
      groupByFrequency,
      startTime,
      endTime,
      eventId
    };

    // Mock mode answers the same query out of the generator instead of the server, so the
    // Downloads and Retro views show rows and issue no request while the toggle is on. "All" is
    // handled below instead: the generator pages its rows the way the endpoint does, so it takes
    // the same walk.
    if (mockMode && !isAllItems) {
      setData(MockDataService.generateMockRetroData(params));
      hasInitialDataRef.current = true;
      setError(null);
      setIsFetching(false);
      setIsLoading(false);
      return;
    }

    const controller = new AbortController();

    // Written on every run rather than only on user-initiated ones. The settle below returns early
    // on an aborted request, so a user fetch that a version bump cancels partway leaves the flag
    // true; a background run that merely skipped setting it would then fade the table for its whole
    // duration, which is the fade this hook exists to keep off.
    setIsFetching(!isBackgroundRefresh);
    if (!hasInitialDataRef.current) {
      setIsLoading(true);
    }

    // "All" is a walk of bounded pages rather than one request for the whole table. Mock mode
    // walks the generator's pages the same way, so the toggle shows every generated row too.
    const pending = isAllItems
      ? fetchEveryPage(params, (pageParams) =>
          mockMode
            ? Promise.resolve(MockDataService.generateMockRetroData(pageParams))
            : ApiService.getRetroDownloads(pageParams, controller.signal)
        )
      : ApiService.getRetroDownloads(params, controller.signal);

    pending
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
    mockMode,
    page,
    pageSize,
    sort,
    service,
    client,
    search,
    hideLocalhost,
    hideMetadata,
    hideSmallFiles,
    hideEvicted,
    hideUnknown,
    includeActive,
    hitMiss,
    groupByGame,
    groupByService,
    mergeAcrossServices,
    groupUnknownGames,
    groupByFrequency,
    startTime,
    endTime,
    eventId,
    refreshVersion
  ]);

  // Readers that only draw rows and totals see the same empty page they always did before the first
  // request answers; `hasResponse` is what tells the ones that act on the count apart from them.
  const response = data ?? EMPTY_RESPONSE;

  return {
    items: response.items,
    totalItems: response.totalItems,
    totalDownloads: response.totalDownloads,
    totalPages: response.totalPages,
    currentPage: response.currentPage,
    pageSize: response.pageSize,
    isLoading,
    isFetching,
    hasResponse: data !== null,
    error
  };
}
