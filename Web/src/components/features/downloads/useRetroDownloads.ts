import { useEffect, useRef, useState } from 'react';

import ApiService, {
  type RetroDownloadDto,
  type RetroDownloadResponse,
  type RetroDownloadQueryParams
} from '@services/api.service';

interface RetroDownloadsHookOptions {
  /** Active gating — if false, the hook does not fetch. */
  enabled: boolean;
  /** 1-based page. */
  page: number;
  /** Rows per page (server clamps to 1–200). */
  pageSize: number;
  /** Sort token (matches backend switch). */
  sort: string;
  /** Service filter — 'all' or service name. */
  service: string;
  /** Client filter — 'all' or client IP. */
  client: string;
  /** Free-text search. */
  search: string;
  /** Whether to hide 127.0.0.1 / ::1 rows. */
  hideLocalhost: boolean;
  /** Whether to include zero-byte rows. */
  showZeroBytes: boolean;
  /** Whether to hide rows whose game name is unknown / equals the service. */
  hideUnknown: boolean;
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
    showZeroBytes,
    hideUnknown
  } = options;

  const [data, setData] = useState<RetroDownloadResponse>(EMPTY_RESPONSE);
  const [isLoading, setIsLoading] = useState<boolean>(false);
  const [isFetching, setIsFetching] = useState<boolean>(false);
  const [error, setError] = useState<Error | null>(null);

  // Preserve previous data across fetches (placeholderData: keepPreviousData semantics).
  const hasInitialDataRef = useRef<boolean>(false);

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
      showZeroBytes,
      hideUnknown
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
    showZeroBytes,
    hideUnknown
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
