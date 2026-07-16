import { API_BASE } from '../utils/constants';
import { isAbortError } from '../utils/error';
import { ApiError, assertOk, buildApiError } from './apiError';
import type {
  OperationStatus,
  PrefillSessionStatus,
  DaemonSessionStatus,
  DaemonAuthState
} from '../types/operations';
import type {
  CacheInfo,
  CacheSizeInfo,
  CacheSizeScanningInfo,
  CacheSizeUnavailableInfo,
  CacheSizeScanStartInfo,
  Download,
  ClientStat,
  ServiceStat,
  ProcessingStatus,
  ClearCacheResponse,
  MessageResponse,
  Config,
  DashboardStats,
  HourlyActivityResponse,
  CacheGrowthResponse,
  SparklineDataResponse,
  CacheSnapshotResponse,
  CachedCorruptionDetectionResponse,
  CorruptionDetectionMethod,
  CorruptionScanHistoryResponse,
  CorruptedChunkDetail,
  GameCacheInfo,
  ServiceCacheInfo,
  DatasourceLogPosition,
  DatasourceServiceCounts,
  Event,
  CreateEventRequest,
  UpdateEventRequest,
  DownloadSpeedSnapshot,
  SpeedHistorySnapshot,
  ClientGroup,
  CreateClientGroupRequest,
  UpdateClientGroupRequest,
  StatsExclusionsResponse,
  ClientExclusionRule,
  EpicGameMappingDto,
  EpicMappingStats,
  EpicDaemonStatusDto,
  EpicMappingAuthStatus,
  EpicScheduleStatus,
  XboxGameMappingDto,
  XboxMappingStats,
  XboxMappingAuthStatus,
  PicsStatus
} from '../types';
import type { StructuralScanMode } from '../types/corruptionScan';
import type { DashboardBatchResponse } from '../contexts/DashboardDataContext/types';
import type {
  NotificationMode,
  ServiceScheduleInfo
} from '../components/features/management/schedules/types';
import type {
  ScheduledPrefillConfigDto,
  ScheduledPrefillServiceScheduleDto
} from '../components/features/management/schedules/scheduled-prefill/types';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId,
  PersistentPrefillValiditySettings,
  PersistentSessionNotFoundState
} from '../components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from '../hooks/usePrefillSteamAuth';
import type { MetricsSecurityResponse } from '../components/features/management/grafana/GrafanaEndpoints.types';
import type { GuestDurationResponse } from '../components/features/user/AccessSecurityCard.types';

// The backend error body shape (`ApiErrorData`) and the structured 409 `OperationConflictBody`
// now live in ./apiError alongside the typed `ApiError` they feed, so the sibling services can
// share them without importing this module. handleResponse routes every failure through
// buildApiError there.

interface CachedGameDetectionResponse {
  hasCachedResults: boolean;
  games?: GameCacheInfo[];
  services?: ServiceCacheInfo[];
  totalGamesDetected?: number;
  totalServicesDetected?: number;
  lastDetectionTime?: string;
}

// RC3 fix: every persistent-login response variant now
// carries the DaemonSession.Id the login started/resolved on, so the frontend can pin it and
// reject/detect a later cross-session mismatch instead of trusting whichever session the backend
// happens to resolve as "active" on each call. The bare `'authenticated'` string literal cannot
// carry a field - it is kept only for backward compatibility with `isPersistentLoginAuthenticatedResponse`
// and is not expected to be emitted once the backend contract change lands.
type PersistentChallengeResponse =
  | (CredentialChallenge & { sessionId: string })
  | 'authenticated'
  | { authenticated: true; sessionId?: string }
  | { status: 'authenticated' | 'logged-in'; message?: string; sessionId?: string };

/**
 * Structural info attached as `.cause` on the Error thrown by `getPersistentChallenge` for a 404
 * (matches the existing pattern below of attaching a typed body to `.cause` for 409s). Exists
 * because the generic `handleResponse` throws `errorData.error` verbatim with no "HTTP 404" prefix
 * whenever the body has an `error` field - which ResolveRunningPersistentSession's typed NotFound
 * body now always does - so a caller can no longer rely on message-sniffing alone to detect this.
 */
export interface PersistentSessionNotFoundInfo {
  status: 404;
  state: PersistentSessionNotFoundState;
}

/**
 * Structural info attached as `.cause` on the Error thrown by `getPersistentChallenge`/
 * `providePersistentCredential` for a 409 (RC3 fix):
 * `PersistentPrefillController` now rejects a supplied `sessionId` that no longer matches the
 * live session instead of silently substituting the current one. `session_replaced` is the
 * mismatch itself; `credential_rejected` is the RC4 manager-leg signal (the daemon reported
 * `Success: false` on `provide-credential`, e.g. a challenge that was already consumed). Read
 * structurally via `.cause`, never by sniffing the thrown message.
 */
export interface PersistentSessionConflictInfo {
  status: 409;
  error: 'session_replaced' | 'credential_rejected';
  state: string;
}

/**
 * Result of POST persistent/logout. `forgotten` is true when the daemon acknowledged an in-place
 * logout (no container restart); false covers a genuine failure (daemon reported failure, or the
 * round-trip throwing) and the caller must fall back to a stop+restart. NOTE: an un-updated
 * steam/epic daemon image also reports `forgotten: true` while only tearing down the live session
 * without deleting the stored account file - this is in-band indistinguishable and not detected;
 * it self-resolves once the daemon image is rebuilt.
 */
interface PersistentLogoutResponse {
  forgotten: boolean;
  fallback?: string;
}

interface OperationResponse {
  message?: string;
  success?: boolean;
  /**
   * Canonical operation lifecycle status from the backend `OperationStatus` enum
   * (serialized lowercase via `OperationStatusJsonConverter`). Typing this as the
   * union instead of `string` gives TypeScript the power to reject typos like
   * `'started'` - which were historically possible and caused at least one "Unexpected response" bug.
   */
  status?: OperationStatus;
  operationId?: string;
  // Wait-queue model (QueuedOperationResponse): present when the request was parked
  // behind a conflicting op (queued) or deduplicated against an identical one
  // (alreadyRunning). Callers must NOT seed a running card in either case.
  queued?: boolean;
  alreadyRunning?: boolean;
  // Log processing specific
  logSizeMB?: number;
  remainingMB?: number;
  resume?: boolean;
  estimatedTimeMinutes?: number;
  // Depot rebuild specific
  rebuildInProgress?: boolean;
  started?: boolean;
  requiresFullScan?: boolean;
  changeGap?: number;
  estimatedApps?: number;
}

export interface RetroDownloadDto {
  /** Composite key: depotId_clientIp or nodepot_service_clientIp_downloadId */
  id: string;
  /** Earliest download start time in the group (UTC, ISO 8601 string) */
  startTimeUtc: string;
  /** Latest download end time in the group (UTC, ISO 8601 string) */
  endTimeUtc: string;
  /** Steam depot ID, null if non-Steam */
  depotId: number | null;
  /** Resolved game/app name from depot mapping or download record */
  appName: string;
  /** Steam app ID for game image lookup */
  steamAppId: number | null;
  /** Epic Games app ID for game image lookup */
  epicAppId: string | null;
  /** Service name (steam, epic, wsus, etc.) */
  service: string;
  /** Datasource name for multi-datasource support */
  datasource: string;
  /** Client IP address */
  clientIp: string;
  /** Weighted average download speed in bytes per second */
  averageBytesPerSecond: number;
  /** Total cache hit bytes across all downloads in group */
  cacheHitBytes: number;
  /** Total cache miss bytes across all downloads in group */
  cacheMissBytes: number;
  /** Cache hit percentage (0-100) */
  cacheHitPercent: number;
  /** Total bytes (hit + miss) across all downloads in group */
  totalBytes: number;
  /** Number of individual download sessions in this group */
  requestCount: number;
  /** List of original download IDs for event association lookups */
  downloadIds: number[];
  /** All distinct client IPs that contributed to this row (single-element for non-merged, multi for merged) */
  clientIps: string[];
  /** All distinct depot IDs that contributed to this row (single-element for non-merged, multi for merged) */
  depotIds: number[];
}

export interface RetroDownloadResponse {
  items: RetroDownloadDto[];
  totalItems: number;
  totalPages: number;
  currentPage: number;
  pageSize: number;
}

export interface RetroDownloadQueryParams {
  page: number;
  pageSize: number;
  sort?: string;
  service?: string;
  client?: string;
  search?: string;
  hideLocalhost?: boolean;
  showZeroBytes?: boolean;
  hideUnknown?: boolean;
  /** Hit/miss bucket filter - 'all' (or omitted) is no filter, 'hit', or 'miss'. */
  hitMiss?: string;
  groupByGame?: boolean;
  /** When true, merges all rows for the same service into one row, overriding groupByGame. */
  groupByService?: boolean;
  startTime?: number;
  endTime?: number;
  eventId?: number;
}

class ApiService {
  static async handleResponse<T>(response: Response): Promise<T> {
    // Cancellation (499 / client-closed request) is a distinct terminal outcome, NOT a failure:
    // it stays the AbortError path so isAbortError() catches it. Never fold this into ApiError.
    if (response.status === 499) {
      const error = new Error('Request cancelled');
      error.name = 'AbortError';
      throw error;
    }

    // The single throw site for failures. All status classification (401 auth-event dispatch,
    // 403 warn, 409 conflict cause) and the one message precedence live in buildApiError, so every
    // failing response - here and via assertOk - throws the identical typed ApiError.
    if (!response.ok) {
      throw await buildApiError(response);
    }

    // Success. An empty 2xx body is an explicit, documented "no content" result (void / 204-style
    // endpoints) - it is NOT a masked error, since any failure already threw above. Callers that
    // require a shape must not rely on this branch.
    const text = await response.text();
    if (!text || text.trim() === '') {
      return undefined as unknown as T;
    }

    try {
      return JSON.parse(text) as T;
    } catch {
      // Success status but a non-JSON body: surface as a typed parse error, never silently.
      throw new ApiError({
        status: response.status,
        kind: 'parse',
        body: null,
        message: 'Invalid JSON response from server'
      });
    }
  }

  // Helper to add headers to all requests
  private static getHeaders(additionalHeaders: Record<string, string> = {}): HeadersInit {
    return {
      ...additionalHeaders
    };
  }

  // Helper to get fetch options with credentials for session cookies
  static getFetchOptions(options: RequestInit = {}): RequestInit {
    return {
      ...options,
      credentials: 'include', // Important: include HttpOnly session cookies
      headers: {
        ...this.getHeaders(),
        ...(options.headers || {})
      }
    };
  }

  // Dashboard batch endpoint - fetches all 6 dashboard data sources in a single request
  static async getDashboardBatch(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventId?: number,
    cacheBust?: number
  ): Promise<DashboardBatchResponse> {
    let url = `${API_BASE}/dashboard/batch`;
    const params = new URLSearchParams();
    if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
    if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
    if (eventId) params.append('eventId', eventId.toString());
    if (cacheBust) params.append('cacheBust', cacheBust.toString());
    if (params.toString()) url += `?${params}`;

    // No client-side cache. Backend IMemoryCache (60s non-live, 15s live)
    // handles dedup at the server level. Avoids OOM from holding multiple
    // batch responses in browser memory.
    try {
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<DashboardBatchResponse>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        console.error('getDashboardBatch error:', error);
      }
      throw error;
    }
  }

  static async getCacheInfo(signal?: AbortSignal): Promise<CacheInfo> {
    try {
      const res = await fetch(`${API_BASE}/cache`, this.getFetchOptions({ signal }));
      return await this.handleResponse<CacheInfo>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCacheInfo error:', error);
      }
      throw error;
    }
  }

  static async getLatestDownloads(
    signal?: AbortSignal,
    count: number | 'unlimited' = 'unlimited',
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    cacheBust?: number
  ): Promise<Download[]> {
    try {
      const actualCount = count === 'unlimited' ? 2147483647 : count;
      let url = `${API_BASE}/downloads/latest`;
      const params = new URLSearchParams();
      params.append('count', actualCount.toString());
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<Download[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getLatestDownloads error:', error);
      }
      throw error;
    }
  }

  static async getRetroDownloads(
    params: RetroDownloadQueryParams,
    signal?: AbortSignal
  ): Promise<RetroDownloadResponse> {
    try {
      const qs = new URLSearchParams();
      qs.append('page', String(params.page));
      qs.append('pageSize', String(params.pageSize));
      if (params.sort) qs.append('sort', params.sort);
      if (params.service) qs.append('service', params.service);
      if (params.client) qs.append('client', params.client);
      if (params.search) qs.append('search', params.search);
      if (params.hideLocalhost !== undefined)
        qs.append('hideLocalhost', String(params.hideLocalhost));
      if (params.showZeroBytes !== undefined)
        qs.append('showZeroBytes', String(params.showZeroBytes));
      if (params.hideUnknown !== undefined) qs.append('hideUnknown', String(params.hideUnknown));
      if (params.hitMiss) qs.append('hitMiss', params.hitMiss);
      if (params.groupByGame !== undefined) qs.append('groupByGame', String(params.groupByGame));
      if (params.groupByService !== undefined)
        qs.append('groupByService', String(params.groupByService));
      if (params.startTime !== undefined) qs.append('startTime', String(params.startTime));
      if (params.endTime !== undefined) qs.append('endTime', String(params.endTime));
      if (params.eventId !== undefined) qs.append('eventId', String(params.eventId));

      const url = `${API_BASE}/downloads/retro?${qs.toString()}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<RetroDownloadResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getRetroDownloads error:', error);
      }
      throw error;
    }
  }

  static async getClientStats(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    includeExcluded?: boolean,
    cacheBust?: number
  ): Promise<ClientStat[]> {
    try {
      let url = `${API_BASE}/stats/clients`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (includeExcluded) params.append('includeExcluded', 'true');
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<ClientStat[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getClientStats error:', error);
      }
      throw error;
    }
  }

  static async getStatsExclusions(signal?: AbortSignal): Promise<StatsExclusionsResponse> {
    try {
      const res = await fetch(`${API_BASE}/stats/exclusions`, this.getFetchOptions({ signal }));
      return await this.handleResponse<StatsExclusionsResponse>(res);
    } catch (error: unknown) {
      {
        console.error('getStatsExclusions error:', error);
      }
      throw error;
    }
  }

  static async updateStatsExclusions(ips: string[]): Promise<StatsExclusionsResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/stats/exclusions`,
        this.getFetchOptions({
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ips })
        })
      );
      return await this.handleResponse<StatsExclusionsResponse>(res);
    } catch (error: unknown) {
      {
        console.error('updateStatsExclusions error:', error);
      }
      throw error;
    }
  }

  static async updateStatsExclusionRules(
    rules: ClientExclusionRule[]
  ): Promise<StatsExclusionsResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/stats/exclusions`,
        this.getFetchOptions({
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ rules })
        })
      );
      return await this.handleResponse<StatsExclusionsResponse>(res);
    } catch (error: unknown) {
      {
        console.error('updateStatsExclusionRules error:', error);
      }
      throw error;
    }
  }

  static async getEvictionSettings(signal?: AbortSignal): Promise<{
    evictedDataMode: string;
    evictionScanNotifications: boolean;
    pruneOrphanedDownloads: boolean;
  }> {
    try {
      const res = await fetch(`${API_BASE}/stats/eviction`, this.getFetchOptions({ signal }));
      return await this.handleResponse<{
        evictedDataMode: string;
        evictionScanNotifications: boolean;
        pruneOrphanedDownloads: boolean;
      }>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getEvictionSettings error:', error);
      }
      throw error;
    }
  }

  static async updateEvictionSettings(
    evictedDataMode?: string,
    evictionScanNotifications?: boolean,
    pruneOrphanedDownloads?: boolean
  ): Promise<{
    evictedDataMode: string;
    evictionScanNotifications: boolean;
    pruneOrphanedDownloads: boolean;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/stats/eviction`,
        this.getFetchOptions({
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            evictedDataMode,
            evictionScanNotifications,
            pruneOrphanedDownloads
          })
        })
      );
      return await this.handleResponse<{
        evictedDataMode: string;
        evictionScanNotifications: boolean;
        pruneOrphanedDownloads: boolean;
      }>(res);
    } catch (error: unknown) {
      {
        console.error('updateEvictionSettings error:', error);
      }
      throw error;
    }
  }

  static async startEvictionScan(): Promise<{
    operationId: string;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/stats/eviction/reconcile`,
        this.getFetchOptions({ method: 'POST' })
      );
      return await this.handleResponse<{
        operationId: string;
        queued?: boolean;
        alreadyRunning?: boolean;
      }>(res);
    } catch (error: unknown) {
      console.error('startEvictionScan error:', error);
      throw error;
    }
  }

  static async resetEvictions(): Promise<{ reset: number }> {
    try {
      const res = await fetch(
        `${API_BASE}/stats/eviction/reset`,
        this.getFetchOptions({ method: 'POST' })
      );
      return await this.handleResponse<{ reset: number }>(res);
    } catch (error: unknown) {
      console.error('resetEvictions error:', error);
      throw error;
    }
  }

  static async getServiceStats(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    cacheBust?: number
  ): Promise<ServiceStat[]> {
    try {
      let url = `${API_BASE}/stats/services`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<ServiceStat[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getServiceStats error:', error);
      }
      throw error;
    }
  }

  // Dashboard aggregated stats
  static async getDashboardStats(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventIds?: number[],
    cacheBust?: number
  ): Promise<DashboardStats> {
    try {
      let url = `${API_BASE}/stats/dashboard`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventIds && eventIds.length > 0) params.append('eventId', eventIds[0].toString());
      if (cacheBust) params.append('cacheBust', cacheBust.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<DashboardStats>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getDashboardStats error:', error);
      }
      throw error;
    }
  }

  // Hourly activity data for Peak Usage Hours widget
  static async getHourlyActivity(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventId?: number
  ): Promise<HourlyActivityResponse> {
    try {
      let url = `${API_BASE}/stats/hourly-activity`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventId) params.append('eventId', eventId.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<HourlyActivityResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getHourlyActivity error:', error);
      }
      throw error;
    }
  }

  // Cache growth data for Cache Growth widget
  static async getCacheGrowth(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    interval = 'daily',
    actualCacheSize?: number,
    eventId?: number
  ): Promise<CacheGrowthResponse> {
    try {
      let url = `${API_BASE}/stats/cache-growth`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      params.append('interval', interval);
      // Pass actual cache size to detect deletions and calculate net growth
      if (actualCacheSize && actualCacheSize > 0) {
        params.append('actualCacheSize', actualCacheSize.toString());
      }
      if (eventId) params.append('eventId', eventId.toString());
      url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<CacheGrowthResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCacheGrowth error:', error);
      }
      throw error;
    }
  }

  // Sparkline data for dashboard stat cards
  static async getSparklineData(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number,
    eventId?: number
  ): Promise<SparklineDataResponse> {
    try {
      let url = `${API_BASE}/stats/sparklines`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (eventId) params.append('eventId', eventId.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<SparklineDataResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getSparklineData error:', error);
      }
      throw error;
    }
  }

  // Historical cache size snapshots for displaying used space in time ranges
  static async getCacheSnapshot(
    signal?: AbortSignal,
    startTime?: number,
    endTime?: number
  ): Promise<CacheSnapshotResponse> {
    try {
      let url = `${API_BASE}/stats/cache-snapshot`;
      const params = new URLSearchParams();
      if (startTime && !isNaN(startTime)) params.append('startTime', startTime.toString());
      if (endTime && !isNaN(endTime)) params.append('endTime', endTime.toString());
      if (params.toString()) url += `?${params}`;
      const res = await fetch(url, this.getFetchOptions({ signal }));
      return await this.handleResponse<CacheSnapshotResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCacheSnapshot error:', error);
      }
      throw error;
    }
  }

  // Start async cache clearing operation for all datasources (requires auth)
  static async clearAllCache(): Promise<ClearCacheResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/cache`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
          // No timeout - Rust backend handles efficiently
        })
      );
      return await this.handleResponse<ClearCacheResponse>(res);
    } catch (error: unknown) {
      console.error('clearAllCache error:', error);
      throw error;
    }
  }

  // Surfaces failures (throws ApiError) rather than masking them as an empty list - a network/500
  // error is not the same as "no images available". The caller decides how to react.
  static async getAvailableGameImages(): Promise<string[]> {
    const res = await fetch(`${API_BASE}/game-images/available`, this.getFetchOptions({}));
    return await this.handleResponse<string[]>(res);
  }

  // Get the backend's image cache generation (used as cache-bust param on image URLs). Surfaces
  // failures instead of returning a bogus 0 that would masquerade as a real cache version.
  static async getImageCacheVersion(): Promise<number> {
    const res = await fetch(`${API_BASE}/game-images/cache-version`, this.getFetchOptions({}));
    const result = await this.handleResponse<{ version: number }>(res);
    return result.version;
  }

  // Clear the game image cache (disk + in-memory failed-fetch cache)
  static async clearImageCache(): Promise<{
    message: string;
    failedCacheEntriesCleared: number;
    epicImageUrlsRefreshed: number;
    cacheGeneration: number;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/game-images/cache`,
        this.getFetchOptions({
          method: 'DELETE'
        })
      );
      const result = await this.handleResponse<{
        message: string;
        failedCacheEntriesCleared: number;
        epicImageUrlsRefreshed: number;
        cacheGeneration: number;
      }>(res);
      return result;
    } catch (error: unknown) {
      console.error('clearImageCache error:', error);
      throw error;
    }
  }

  // Start async cache clearing operation for a specific datasource (requires auth)
  static async clearDatasourceCache(datasourceName: string): Promise<ClearCacheResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/datasources/${encodeURIComponent(datasourceName)}`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<ClearCacheResponse>(res);
    } catch (error: unknown) {
      console.error('clearDatasourceCache error:', error);
      throw error;
    }
  }

  // Get status of cache clearing operation

  // Reset selected database tables (requires auth)
  static async resetSelectedTables(tableNames: string[]): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/database/tables`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ tables: tableNames })
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('resetSelectedTables error:', error);
      throw error;
    }
  }

  // Reset log position (requires auth) - all datasources
  static async resetLogPosition(position: 'top' | 'bottom' = 'bottom'): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/position`,
        this.getFetchOptions({
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ reset: true, position: position === 'top' ? 0 : null })
          // No timeout - may need to read entire log file to count lines
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('resetLogPosition error:', error);
      throw error;
    }
  }

  // Reset log position for a specific datasource (requires auth)
  static async resetDatasourceLogPosition(
    datasourceName: string,
    position: 'top' | 'bottom' = 'bottom'
  ): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/position/${encodeURIComponent(datasourceName)}`,
        this.getFetchOptions({
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ position: position === 'top' ? 0 : null })
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('resetDatasourceLogPosition error:', error);
      throw error;
    }
  }

  // Get log positions for all datasources
  static async getLogPositions(): Promise<DatasourceLogPosition[]> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/positions`,
        this.getFetchOptions({
          signal: AbortSignal.timeout(10000)
        })
      );
      return await this.handleResponse<DatasourceLogPosition[]>(res);
    } catch (error: unknown) {
      console.error('getLogPositions error:', error);
      throw error;
    }
  }

  // Process all logs (requires auth) - all datasources
  static async processAllLogs(): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/process`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' }
          // No timeout - Rust log processor handles large files efficiently
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('processAllLogs error:', error);
      throw error;
    }
  }

  // Process logs for a specific datasource (requires auth)
  static async processDatasourceLogs(datasourceName: string): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/process/${encodeURIComponent(datasourceName)}`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('processDatasourceLogs error:', error);
      throw error;
    }
  }

  static async getProcessingStatus(): Promise<ProcessingStatus> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/process/status`,
        this.getFetchOptions({
          signal: AbortSignal.timeout(5000)
        })
      );
      return await this.handleResponse<ProcessingStatus>(res);
    } catch (error: unknown) {
      console.error('getProcessingStatus error:', error);
      throw error;
    }
  }

  static async forceKillLogProcessing(): Promise<{ message: string }> {
    const res = await fetch(
      `${API_BASE}/logs/process/kill`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' }
      })
    );
    return await this.handleResponse<{ message: string }>(res);
  }

  // Get log removal status

  // Get counts of log entries per service, grouped by datasource
  static async getServiceLogCountsByDatasource(): Promise<DatasourceServiceCounts[]> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/service-counts/by-datasource`,
        this.getFetchOptions({
          // No timeout - can take time for large log files
        })
      );
      return await this.handleResponse<DatasourceServiceCounts[]>(res);
    } catch (error: unknown) {
      console.error('getServiceLogCountsByDatasource error:', error);
      throw error;
    }
  }

  // Remove specific service entries from a specific datasource's logs (requires auth)
  static async removeServiceFromDatasourceLogs(
    datasourceName: string,
    service: string
  ): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/datasources/${encodeURIComponent(datasourceName)}/services/${encodeURIComponent(service)}`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('removeServiceFromDatasourceLogs error:', error);
      throw error;
    }
  }

  // Delete entire log file for a datasource (requires auth)
  static async deleteLogFile(datasourceName: string): Promise<MessageResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/logs/datasources/${encodeURIComponent(datasourceName)}/file`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<MessageResponse>(res);
    } catch (error: unknown) {
      console.error('deleteLogFile error:', error);
      throw error;
    }
  }

  // Get count of LogEntries in database (not log files)
  static async getDatabaseLogEntriesCount(): Promise<number> {
    try {
      const res = await fetch(`${API_BASE}/database/log-entries-count`, this.getFetchOptions());
      const data = await this.handleResponse<{ count: number }>(res);
      // handleResponse returns undefined on an empty 2xx body; default to 0 rather than deref.
      return data?.count ?? 0;
    } catch (error: unknown) {
      console.error('getDatabaseLogEntriesCount error:', error);
      throw error;
    }
  }

  // Get configuration info
  static async getConfig(): Promise<Config> {
    const res = await fetch(
      `${API_BASE}/system/config`,
      this.getFetchOptions({
        // No timeout - can take time for large log file scanning
      })
    );
    return await this.handleResponse<Config>(res);
  }

  // Get directory write permissions and docker socket availability
  static async getDirectoryPermissions(): Promise<{
    cache: { path: string; exists: boolean; writable: boolean; readOnly: boolean };
    logs: { path: string; exists: boolean; writable: boolean; readOnly: boolean };
    dockerSocket: { available: boolean };
  }> {
    const res = await fetch(`${API_BASE}/system/permissions`, this.getFetchOptions());
    return await this.handleResponse(res);
  }

  // ==================== Steam API Key Management ====================

  // Test a Steam Web API key without saving it
  static async testSteamApiKey(apiKey: string): Promise<{ valid: boolean; message: string }> {
    const response = await fetch(
      `${API_BASE}/steam-api-keys/test`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ apiKey })
      })
    );
    return this.handleResponse<{ valid: boolean; message: string }>(response);
  }

  // Save a Steam Web API key
  static async saveSteamApiKey(apiKey: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/steam-api-keys`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ apiKey })
      })
    );
    await assertOk(response);
  }

  // ==================== Steam Auth Mode ====================

  // Set Steam authentication mode (anonymous or authenticated)
  static async setSteamAuthMode(mode: 'anonymous' | 'authenticated'): Promise<void> {
    const response = await fetch(
      `${API_BASE}/steam-auth/mode`,
      this.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode })
      })
    );
    await assertOk(response);
  }

  // ==================== Setup Status ====================

  // Get setup status (public endpoint, no auth needed)
  static async getSetupStatus(): Promise<{
    isCompleted: boolean;
    hasProcessedLogs: boolean;
    setupCompleted: boolean;
    needsPostgresCredentials: boolean;
    currentSetupStep: string | null;
    dataSourceChoice: string | null;
    completedPlatforms: string | null;
    mode: 'embedded' | 'external';
    postgresHost: string | null;
    postgresPort: number | null;
    postgresDatabase: string | null;
    postgresUser: string | null;
  }> {
    const response = await fetch(
      `${API_BASE}/system/setup`,
      this.getFetchOptions({ cache: 'no-store' })
    );
    return this.handleResponse<{
      isCompleted: boolean;
      hasProcessedLogs: boolean;
      setupCompleted: boolean;
      needsPostgresCredentials: boolean;
      currentSetupStep: string | null;
      dataSourceChoice: string | null;
      completedPlatforms: string | null;
      mode: 'embedded' | 'external';
      postgresHost: string | null;
      postgresPort: number | null;
      postgresDatabase: string | null;
      postgresUser: string | null;
    }>(response);
  }

  // Mark setup as completed
  static async markSetupComplete(): Promise<void> {
    const response = await fetch(
      `${API_BASE}/system/setup`,
      this.getFetchOptions({
        cache: 'no-store',
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ completed: true })
      })
    );
    await assertOk(response);
  }

  // PICS/Depot related endpoints (consolidated from GameInfoController)
  static async getPicsStatus(signal?: AbortSignal): Promise<PicsStatus> {
    try {
      const res = await fetch(`${API_BASE}/depots/status`, this.getFetchOptions({ signal }));
      return await this.handleResponse<PicsStatus>(res);
    } catch (error: unknown) {
      console.error('getPicsStatus error:', error);
      throw error;
    }
  }

  static async triggerSteamKitRebuild(
    incremental = false,
    signal?: AbortSignal
  ): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/depots/rebuild?incremental=${incremental}`,
        this.getFetchOptions({
          method: 'POST',
          signal,
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('triggerSteamKitRebuild error:', error);
      throw error;
    }
  }

  static async cancelSteamKitRebuild(signal?: AbortSignal): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/depots/rebuild`,
        this.getFetchOptions({
          method: 'DELETE',
          signal,
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await assertOk(res);
    } catch (error: unknown) {
      console.error('cancelSteamKitRebuild error:', error);
      throw error;
    }
  }

  static async checkIncrementalViability(
    signal?: AbortSignal
  ): Promise<{ viable: boolean; reason?: string; willTriggerFullScan?: boolean }> {
    try {
      const res = await fetch(
        `${API_BASE}/depots/rebuild/check-incremental`,
        this.getFetchOptions({
          method: 'GET',
          signal
        })
      );
      return await this.handleResponse<{ viable: boolean; reason?: string }>(res);
    } catch (error: unknown) {
      console.error('checkIncrementalViability error:', error);
      throw error;
    }
  }

  static async setDepotScheduledScanMode(
    mode: 'incremental' | 'full' | 'github',
    signal?: AbortSignal
  ): Promise<void> {
    const payload: boolean | 'github' = mode === 'github' ? 'github' : mode === 'incremental';

    try {
      const res = await fetch(
        `${API_BASE}/depots/rebuild/config/mode`,
        this.getFetchOptions({
          method: 'PUT',
          signal,
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        })
      );
      await this.handleResponse<{ incrementalMode?: boolean | string; message?: string }>(res);
    } catch (error: unknown) {
      console.error('setDepotScheduledScanMode error:', error);
      throw error;
    }
  }

  static async downloadPrecreatedDepotData(signal?: AbortSignal): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/depots/import?source=github`,
        this.getFetchOptions({
          method: 'POST',
          signal
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        console.error('downloadPrecreatedDepotData error:', error);
      }
      throw error;
    }
  }

  static async applyDepotMappings(signal?: AbortSignal): Promise<OperationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/depots`,
        this.getFetchOptions({
          method: 'PATCH',
          signal,
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<OperationResponse>(res);
    } catch (error: unknown) {
      console.error('applyDepotMappings error:', error);
      throw error;
    }
  }

  // Set cache clearing delete mode (requires auth)
  static async setCacheDeleteMode(
    deleteMode: string
  ): Promise<{ message: string; deleteMode: string }> {
    const res = await fetch(
      `${API_BASE}/system/cache-delete-mode`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ deleteMode })
      })
    );
    return await this.handleResponse<{ message: string; deleteMode: string }>(res);
  }

  // Check if rsync is available on the system
  static async isRsyncAvailable(): Promise<{ available: boolean }> {
    try {
      const res = await fetch(`${API_BASE}/system/rsync/available`, this.getFetchOptions());
      return await this.handleResponse<{ available: boolean }>(res);
    } catch (error: unknown) {
      console.error('isRsyncAvailable error:', error);
      throw error;
    }
  }

  // Get cache size with deletion time estimates. A force refresh is an asynchronous queued
  // operation and returns CacheSizeScanStartInfo. A non-force read only reads the persisted
  // result and may report an active scan or the expected not-yet-calculated empty state.
  static async getCacheSize(
    datasource?: string,
    force?: boolean,
    signal?: AbortSignal
  ): Promise<
    CacheSizeInfo | CacheSizeScanningInfo | CacheSizeUnavailableInfo | CacheSizeScanStartInfo
  > {
    try {
      const params = new URLSearchParams();
      if (datasource) params.set('datasource', datasource);
      if (force) params.set('force', 'true');
      const queryString = params.toString();
      const url = queryString ? `${API_BASE}/cache/size?${queryString}` : `${API_BASE}/cache/size`;
      // No timeout - cache size calculation can take a very long time for large caches. The
      // optional signal lets a caller abort an in-flight request (e.g. superseded by a newer one).
      const res = await fetch(url, this.getFetchOptions(signal ? { signal } : {}));
      return await this.handleResponse<
        CacheSizeInfo | CacheSizeScanningInfo | CacheSizeUnavailableInfo | CacheSizeScanStartInfo
      >(res);
    } catch (error: unknown) {
      // A cancelled request (aborted via signal) is a distinct terminal outcome, not a failure -
      // don't log it as an error; the caller filters it via isAbortError.
      if (!isAbortError(error)) {
        console.error('getCacheSize error:', error);
      }
      throw error;
    }
  }

  // Get the cached corruption scan for one detection method (returns immediately without
  // running a scan). The method is required so a load can never adopt the other method's scan.
  static async getCachedCorruptionDetection(
    detectionMethod: CorruptionDetectionMethod
  ): Promise<CachedCorruptionDetectionResponse> {
    try {
      const params = new URLSearchParams();
      params.set('detectionMethod', detectionMethod);
      const res = await fetch(
        `${API_BASE}/cache/corruption/cached?${params.toString()}`,
        this.getFetchOptions({
          signal: AbortSignal.timeout(30000) // 30 seconds for large datasets
        })
      );
      return await this.handleResponse<CachedCorruptionDetectionResponse>(res);
    } catch (error: unknown) {
      console.error('getCachedCorruptionDetection error:', error);
      throw error;
    }
  }

  // Start background corruption detection scan
  static async startCorruptionDetection(
    detectionMethod: CorruptionDetectionMethod = 'repeated_miss',
    threshold = 3,
    lookbackDays = 30,
    scanMode?: StructuralScanMode
  ): Promise<{
    operationId: string;
    message: string;
    status: string;
    scanMode?: StructuralScanMode;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const params = new URLSearchParams();
      params.set('detectionMethod', detectionMethod);
      if (detectionMethod === 'repeated_miss') {
        params.set('threshold', String(threshold));
        params.set('lookbackDays', String(lookbackDays));
      } else if (scanMode) {
        params.set('scanMode', scanMode);
      }
      const res = await fetch(
        `${API_BASE}/cache/corruption/detect?${params.toString()}`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<{
        operationId: string;
        message: string;
        status: string;
        scanMode?: StructuralScanMode;
        queued?: boolean;
        alreadyRunning?: boolean;
      }>(res);
    } catch (error: unknown) {
      console.error('startCorruptionDetection error:', error);
      throw error;
    }
  }

  // Get corruption detection status

  // Remove corrupted chunks for a specific service (requires auth)
  static async removeCorruptedChunks(
    service: string,
    scanId: string,
    candidateIds?: string[]
  ): Promise<{
    message: string;
    service: string;
    operationId?: string;
    status?: OperationStatus;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const params = new URLSearchParams();
      params.set('scanId', scanId);
      if (candidateIds && candidateIds.length > 0) {
        params.set('candidateIds', candidateIds.join(','));
      }
      const res = await fetch(
        `${API_BASE}/cache/services/${encodeURIComponent(service)}/corruption?${params.toString()}`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
          // No timeout - Rust corruption remover handles large operations efficiently
        })
      );
      return await this.handleResponse<{ message: string; service: string }>(res);
    } catch (error: unknown) {
      console.error('removeCorruptedChunks error:', error);
      throw error;
    }
  }

  static async removeAllCorruptedChunks(
    scanId: string,
    services?: string[]
  ): Promise<{ message: string; started: boolean }> {
    try {
      const params = new URLSearchParams();
      params.set('scanId', scanId);
      // Optional subset filter: absent = remove corruption for all services (unchanged).
      if (services && services.length > 0) {
        params.set('services', services.join(','));
      }
      const res = await fetch(
        `${API_BASE}/cache/corruption?${params.toString()}`,
        this.getFetchOptions({
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' }
        })
      );
      // 202 Accepted = an operation was started (or queued); 200 OK = a no-op response
      // (no corruption data / no matching services) with no SignalR event to follow.
      const started = res.status === 202;
      const body = await this.handleResponse<{ message?: string }>(res);
      return { message: body?.message ?? '', started };
    } catch (error: unknown) {
      console.error('removeAllCorruptedChunks error:', error);
      throw error;
    }
  }

  // Get detailed corruption information for a specific service
  static async getCorruptionDetails(
    service: string,
    scanId: string
  ): Promise<CorruptedChunkDetail[]> {
    try {
      const params = new URLSearchParams();
      params.set('scanId', scanId);
      const url = `${API_BASE}/cache/services/${encodeURIComponent(service)}/corruption?${params.toString()}`;
      const res = await fetch(
        url,
        this.getFetchOptions({
          // No timeout - wait for backend to complete analysis (could take several minutes for large logs)
        })
      );
      return await this.handleResponse<CorruptedChunkDetail[]>(res);
    } catch (error: unknown) {
      console.error('getCorruptionDetails error:', error);
      throw error;
    }
  }

  // List retained corruption scan snapshots (at most three per detection method,
  // newest first, including zero-result scans)
  static async getCorruptionScanHistory(): Promise<CorruptionScanHistoryResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/corruption/history`,
        this.getFetchOptions({
          signal: AbortSignal.timeout(30000)
        })
      );
      return await this.handleResponse<CorruptionScanHistoryResponse>(res);
    } catch (error: unknown) {
      console.error('getCorruptionScanHistory error:', error);
      throw error;
    }
  }

  // Read-only stored evidence for one service of a saved history snapshot. This dedicated
  // route never requires the snapshot to be current and cannot be used for removal.
  static async getCorruptionHistoryDetails(
    scanId: string,
    service: string
  ): Promise<CorruptedChunkDetail[]> {
    try {
      const url = `${API_BASE}/cache/corruption/history/${encodeURIComponent(scanId)}/services/${encodeURIComponent(service)}`;
      const res = await fetch(
        url,
        this.getFetchOptions({
          signal: AbortSignal.timeout(30000)
        })
      );
      return await this.handleResponse<CorruptedChunkDetail[]>(res);
    } catch (error: unknown) {
      console.error('getCorruptionHistoryDetails error:', error);
      throw error;
    }
  }

  // Delete one saved corruption scan snapshot (database record and stored evidence only,
  // never cache files, access-log entries, or download history)
  static async deleteCorruptionScanHistory(scanId: string): Promise<{ message?: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/corruption/history/${encodeURIComponent(scanId)}`,
        this.getFetchOptions({
          method: 'DELETE',
          signal: AbortSignal.timeout(30000)
        })
      );
      const body = await this.handleResponse<{ message?: string }>(res);
      return body ?? {};
    } catch (error: unknown) {
      console.error('deleteCorruptionScanHistory error:', error);
      throw error;
    }
  }

  // Start game cache detection as background operation
  static async startGameCacheDetection(
    forceRefresh = false
  ): Promise<{ operationId: string; queued?: boolean; alreadyRunning?: boolean }> {
    try {
      const url = `${API_BASE}/games/detect${forceRefresh ? '?forceRefresh=true' : ''}`;
      const res = await fetch(
        url,
        this.getFetchOptions({
          method: 'POST',
          signal: AbortSignal.timeout(10000) // Short timeout since it returns immediately
        })
      );
      return await this.handleResponse<{ operationId: string }>(res);
    } catch (error: unknown) {
      console.error('startGameCacheDetection error:', error);
      throw error;
    }
  }

  // In-flight dedupe: when StorageSection and GameCacheDetector mount on the same tick,
  // they each call getCachedGameDetection(). Without this, the backend gets two parallel
  // requests for identical data. Share the in-flight promise so only one network round-trip
  // happens; both callers resolve to the same payload. Cleared in finally so subsequent
  // calls (after a fresh scan or user-triggered reload) hit the network again.
  private static _cachedGameDetectionInFlight: Promise<CachedGameDetectionResponse> | null = null;

  // Get cached game detection results from database (if available)
  static getCachedGameDetection(): Promise<CachedGameDetectionResponse> {
    if (ApiService._cachedGameDetectionInFlight) {
      return ApiService._cachedGameDetectionInFlight;
    }

    const promise = (async (): Promise<CachedGameDetectionResponse> => {
      try {
        const res = await fetch(`${API_BASE}/games/detect/cached`, ApiService.getFetchOptions({}));
        return await ApiService.handleResponse<CachedGameDetectionResponse>(res);
      } catch (error: unknown) {
        console.error('getCachedGameDetection error:', error);
        throw error;
      } finally {
        ApiService._cachedGameDetectionInFlight = null;
      }
    })();

    ApiService._cachedGameDetectionInFlight = promise;
    return promise;
  }

  // Remove all cache files for a specific game (fire-and-forget, requires auth)
  static async removeGameFromCache(gameAppId: number): Promise<{
    message: string;
    operationId: string;
    appId: string;
    gameName: string;
    status: string;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/games/${gameAppId}`,
        this.getFetchOptions({
          method: 'DELETE'
          // Returns immediately with 202 Accepted - removal happens in background
        })
      );
      return await this.handleResponse<{
        message: string;
        operationId: string;
        appId: string;
        gameName: string;
        status: string;
        queued?: boolean;
        alreadyRunning?: boolean;
      }>(res);
    } catch (error: unknown) {
      console.error('removeGameFromCache error:', error);
      throw error;
    }
  }

  // Remove all cache files for a specific Epic game by name (fire-and-forget, requires auth)
  static async removeEpicGameFromCache(gameName: string): Promise<{
    message: string;
    operationId: string;
    appId: string;
    gameName: string;
    status: string;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/games/epic/${encodeURIComponent(gameName)}`,
        this.getFetchOptions({
          method: 'DELETE'
          // Returns immediately with 202 Accepted - removal happens in background
        })
      );
      return await this.handleResponse<{
        message: string;
        operationId: string;
        appId: string;
        gameName: string;
        status: string;
        queued?: boolean;
        alreadyRunning?: boolean;
      }>(res);
    } catch (error: unknown) {
      console.error('removeEpicGameFromCache error:', error);
      throw error;
    }
  }

  // Remove all cache files for a specific named game (Blizzard/Riot) by service + name (fire-and-forget, requires auth)
  static async removeNamedGameFromCache(
    service: string,
    gameName: string
  ): Promise<{
    message: string;
    operationId: string;
    appId: string;
    gameName: string;
    status: string;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/games/named/${encodeURIComponent(service)}/${encodeURIComponent(gameName)}`,
        this.getFetchOptions({
          method: 'DELETE'
          // Returns immediately with 202 Accepted - removal happens in background
        })
      );
      return await this.handleResponse<{
        message: string;
        operationId: string;
        appId: string;
        gameName: string;
        status: string;
        queued?: boolean;
        alreadyRunning?: boolean;
      }>(res);
    } catch (error: unknown) {
      console.error('removeNamedGameFromCache error:', error);
      throw error;
    }
  }

  // Remove all cache files for a specific service (fire-and-forget, requires auth)
  static async removeServiceFromCache(serviceName: string): Promise<{
    message: string;
    serviceName: string;
    status: string;
    operationId: string;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/services/${encodeURIComponent(serviceName)}`,
        this.getFetchOptions({
          method: 'DELETE'
          // Returns immediately with 202 Accepted - removal happens in background
        })
      );
      return await this.handleResponse<{
        message: string;
        serviceName: string;
        status: string;
        operationId: string;
        queued?: boolean;
        alreadyRunning?: boolean;
      }>(res);
    } catch (error: unknown) {
      console.error('removeServiceFromCache error:', error);
      throw error;
    }
  }

  // Remove ALL evicted downloads, log entries, and detection rows in one batched backend
  // operation (single log rewrite pass + single DB transaction + single disk-summary refresh).
  // Progress/cancel flow through the standard eviction_removal notification (bulk scope).
  static async removeAllEvicted(): Promise<{
    operationId: string;
    queued?: boolean;
    alreadyRunning?: boolean;
  }> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/evicted`,
        this.getFetchOptions({ method: 'DELETE' })
      );
      return await this.handleResponse<{ operationId: string }>(res);
    } catch (error: unknown) {
      console.error('removeAllEvicted error:', error);
      throw error;
    }
  }

  // Remove only the evicted downloads (and their log entries) for a Steam game (fire-and-forget, requires auth)
  static async removeEvictedForGame(
    gameAppId: number
  ): Promise<{ operationId: string; scope: string; key: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/evicted/steam?key=${gameAppId}`,
        this.getFetchOptions({ method: 'DELETE' })
      );
      return await this.handleResponse<{ operationId: string; scope: string; key: string }>(res);
    } catch (error: unknown) {
      console.error('removeEvictedForGame error:', error);
      throw error;
    }
  }

  // Remove only the evicted downloads (and their log entries) for an Epic game (fire-and-forget, requires auth)
  static async removeEvictedForEpicGame(
    epicAppId: string
  ): Promise<{ operationId: string; scope: string; key: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/evicted/epic?key=${encodeURIComponent(epicAppId)}`,
        this.getFetchOptions({ method: 'DELETE' })
      );
      return await this.handleResponse<{ operationId: string; scope: string; key: string }>(res);
    } catch (error: unknown) {
      console.error('removeEvictedForEpicGame error:', error);
      throw error;
    }
  }

  // Remove only the evicted downloads (and their log entries) for a non-game service (fire-and-forget, requires auth)
  static async removeEvictedForService(
    serviceName: string
  ): Promise<{ operationId: string; scope: string; key: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/evicted/service?key=${encodeURIComponent(serviceName)}`,
        this.getFetchOptions({ method: 'DELETE' })
      );
      return await this.handleResponse<{ operationId: string; scope: string; key: string }>(res);
    } catch (error: unknown) {
      console.error('removeEvictedForService error:', error);
      throw error;
    }
  }

  // Remove only the evicted downloads (and their log entries) for a named (Blizzard/Riot) game
  // identified by (service, gameName). Named games have no Steam/Epic AppId, so they use a
  // dedicated two-segment route. Fire-and-forget, requires auth.
  static async removeEvictedForNamedGame(
    service: string,
    gameName: string
  ): Promise<{ operationId: string; scope: string; service: string; gameName: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/cache/evicted/named/${encodeURIComponent(service)}/${encodeURIComponent(gameName)}`,
        this.getFetchOptions({ method: 'DELETE' })
      );
      return await this.handleResponse<{
        operationId: string;
        scope: string;
        service: string;
        gameName: string;
      }>(res);
    } catch (error: unknown) {
      console.error('removeEvictedForNamedGame error:', error);
      throw error;
    }
  }

  // Get active cache operations (for recovery on page load)
  // Note: Used by NotificationsContext for operation recovery

  // Get all active removal operations (games, services, corruption)
  // Used for universal recovery on page refresh

  // Get guest session duration configuration with source labelling (admin-only).
  static async getGuestSessionDuration(signal?: AbortSignal): Promise<GuestDurationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/auth/guest/config/duration`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<GuestDurationResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getGuestSessionDuration error:', error);
      }
      throw error;
    }
  }

  // Set or clear the guest session duration UI override.
  // Pass `null` to clear the override and revert to env/appsettings default.
  static async setGuestSessionDuration(
    durationHours: number | null
  ): Promise<GuestDurationResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/auth/guest/config/duration`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ durationHours })
        })
      );
      return await this.handleResponse<GuestDurationResponse>(res);
    } catch (error: unknown) {
      console.error('setGuestSessionDuration error:', error);
      throw error;
    }
  }

  // ==================== Events API ====================

  // Get all events
  static async getEvents(signal?: AbortSignal): Promise<Event[]> {
    try {
      const res = await fetch(`${API_BASE}/events`, this.getFetchOptions({ signal }));
      return await this.handleResponse<Event[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getEvents error:', error);
      }
      throw error;
    }
  }

  // Get currently active events
  static async getActiveEvents(signal?: AbortSignal): Promise<Event[]> {
    try {
      const res = await fetch(`${API_BASE}/events/active`, this.getFetchOptions({ signal }));
      return await this.handleResponse<Event[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getActiveEvents error:', error);
      }
      throw error;
    }
  }

  // Get a single event by ID

  // Create a new event
  static async createEvent(data: CreateEventRequest): Promise<Event> {
    try {
      const res = await fetch(
        `${API_BASE}/events`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(data)
        })
      );
      return await this.handleResponse<Event>(res);
    } catch (error: unknown) {
      console.error('createEvent error:', error);
      throw error;
    }
  }

  // Update an existing event
  static async updateEvent(id: number, data: UpdateEventRequest): Promise<Event> {
    try {
      const res = await fetch(
        `${API_BASE}/events/${id}`,
        this.getFetchOptions({
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(data)
        })
      );
      return await this.handleResponse<Event>(res);
    } catch (error: unknown) {
      console.error('updateEvent error:', error);
      throw error;
    }
  }

  // Delete an event
  static async deleteEvent(id: number): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/events/${id}`,
        this.getFetchOptions({
          method: 'DELETE'
        })
      );
      await assertOk(res);
    } catch (error: unknown) {
      console.error('deleteEvent error:', error);
      throw error;
    }
  }

  // Get downloads for an event

  // ==================== Downloads with Associations ====================

  // Get a single download with its events

  // Get events for multiple downloads in a single batch request
  static async getBatchDownloadEvents(
    downloadIds: number[],
    signal?: AbortSignal
  ): Promise<
    Record<
      number,
      { events: { id: number; name: string; colorIndex: number; autoTagged: boolean }[] }
    >
  > {
    if (downloadIds.length === 0) {
      return {};
    }
    try {
      // IMPORTANT: use getFetchOptions() to include credentials for HttpOnly session cookies.
      const res = await fetch(
        `${API_BASE}/downloads/batch-download-events`,
        this.getFetchOptions({
          signal,
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ downloadIds })
        })
      );
      return await this.handleResponse(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getBatchDownloadEvents error:', error);
      }
      throw error;
    }
  }

  // =====================================================
  // Real-time Download Speed API
  // =====================================================

  // Get current download speeds snapshot
  static async getCurrentSpeeds(signal?: AbortSignal): Promise<DownloadSpeedSnapshot> {
    try {
      const res = await fetch(`${API_BASE}/speeds/current`, this.getFetchOptions({ signal }));
      return await this.handleResponse<DownloadSpeedSnapshot>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getCurrentSpeeds error:', error);
      }
      throw error;
    }
  }

  // Get historical download speeds for a time period
  static async getSpeedHistory(minutes = 60, signal?: AbortSignal): Promise<SpeedHistorySnapshot> {
    try {
      const res = await fetch(
        `${API_BASE}/speeds/history?minutes=${minutes}`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<SpeedHistorySnapshot>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getSpeedHistory error:', error);
      }
      throw error;
    }
  }

  // ==================== Client Groups API ====================

  // Get all client groups
  static async getClientGroups(signal?: AbortSignal): Promise<ClientGroup[]> {
    try {
      const res = await fetch(`${API_BASE}/client-groups`, this.getFetchOptions({ signal }));
      return await this.handleResponse<ClientGroup[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getClientGroups error:', error);
      }
      throw error;
    }
  }

  // Get a single client group by ID

  // Create a new client group
  static async createClientGroup(data: CreateClientGroupRequest): Promise<ClientGroup> {
    try {
      const res = await fetch(
        `${API_BASE}/client-groups`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(data)
        })
      );
      return await this.handleResponse<ClientGroup>(res);
    } catch (error: unknown) {
      console.error('createClientGroup error:', error);
      throw error;
    }
  }

  // Update an existing client group
  static async updateClientGroup(id: number, data: UpdateClientGroupRequest): Promise<ClientGroup> {
    try {
      const res = await fetch(
        `${API_BASE}/client-groups/${id}`,
        this.getFetchOptions({
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(data)
        })
      );
      return await this.handleResponse<ClientGroup>(res);
    } catch (error: unknown) {
      console.error('updateClientGroup error:', error);
      throw error;
    }
  }

  // Delete a client group
  static async deleteClientGroup(id: number): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/client-groups/${id}`,
        this.getFetchOptions({
          method: 'DELETE'
        })
      );
      await assertOk(res);
    } catch (error: unknown) {
      console.error('deleteClientGroup error:', error);
      throw error;
    }
  }

  // Add a member (IP) to a client group
  static async addClientGroupMember(groupId: number, clientIp: string): Promise<ClientGroup> {
    try {
      const res = await fetch(
        `${API_BASE}/client-groups/${groupId}/members`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ clientIp })
        })
      );
      return await this.handleResponse<ClientGroup>(res);
    } catch (error: unknown) {
      console.error('addClientGroupMember error:', error);
      throw error;
    }
  }

  // Remove a member (IP) from a client group
  static async removeClientGroupMember(groupId: number, clientIp: string): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/client-groups/${groupId}/members/${encodeURIComponent(clientIp)}`,
        this.getFetchOptions({
          method: 'DELETE'
        })
      );
      await assertOk(res);
    } catch (error: unknown) {
      console.error('removeClientGroupMember error:', error);
      throw error;
    }
  }

  // Get IP to group mapping for efficient lookups

  // =====================================================
  // Universal Operation Cancellation APIs
  // =====================================================

  // Cancel any operation by ID
  static async cancelOperation(operationId: string): Promise<{ message: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/operations/${operationId}/cancel`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          signal: AbortSignal.timeout(5000)
        })
      );
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('cancelOperation error:', error);
      throw error;
    }
  }

  static async forceKillOperation(operationId: string): Promise<{ message: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/operations/${operationId}/force-kill`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          signal: AbortSignal.timeout(10000)
        })
      );
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('forceKillOperation error:', error);
      throw error;
    }
  }

  // =====================
  // Prefill Admin APIs
  // =====================

  // Get prefill sessions (paginated)
  static async getPrefillSessions(
    page = 1,
    pageSize = 20,
    status?: PrefillSessionStatus,
    platform?: string,
    signal?: AbortSignal
  ): Promise<PrefillSessionsResponse> {
    try {
      const params = new URLSearchParams({ page: page.toString(), pageSize: pageSize.toString() });
      if (status) params.set('status', status);
      if (platform) params.append('platform', platform);
      const res = await fetch(
        `${API_BASE}/prefill-admin/sessions?${params}`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<PrefillSessionsResponse>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillSessions error:', error);
      throw error;
    }
  }

  // Get active prefill sessions (in-memory)
  static async getActivePrefillSessions(signal?: AbortSignal): Promise<DaemonSessionDto[]> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/sessions/active`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<DaemonSessionDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getActivePrefillSessions error:', error);
      throw error;
    }
  }

  // Get prefill history for a specific session
  static async getPrefillSessionHistory(
    sessionId: string,
    signal?: AbortSignal
  ): Promise<PrefillHistoryEntryDto[]> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/sessions/${sessionId}/history`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<PrefillHistoryEntryDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillSessionHistory error:', error);
      throw error;
    }
  }

  // Terminate a specific prefill session
  static async terminatePrefillSession(
    sessionId: string,
    reason?: string,
    force = false
  ): Promise<{ message: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/sessions/${sessionId}/terminate`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ reason, force })
        })
      );
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('terminatePrefillSession error:', error);
      throw error;
    }
  }

  // Terminate all prefill sessions
  static async terminateAllPrefillSessions(
    reason?: string,
    force = true
  ): Promise<{ message: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/sessions/terminate-all`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ reason, force })
        })
      );
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('terminateAllPrefillSessions error:', error);
      throw error;
    }
  }

  // Get Steam user bans
  static async getSteamBans(
    includeLifted = false,
    signal?: AbortSignal
  ): Promise<BannedSteamUserDto[]> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/bans?includeLifted=${includeLifted}`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<BannedSteamUserDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getSteamBans error:', error);
      throw error;
    }
  }

  // Ban a Steam user by session ID
  static async banSteamUserBySession(
    sessionId: string,
    reason?: string,
    expiresAt?: string
  ): Promise<BannedSteamUserDto> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/bans/by-session/${sessionId}`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ reason, expiresAt })
        })
      );
      return await this.handleResponse<BannedSteamUserDto>(res);
    } catch (error: unknown) {
      console.error('banSteamUserBySession error:', error);
      throw error;
    }
  }

  // Ban a Steam user by username

  // Lift a Steam user ban
  static async liftSteamBan(banId: number): Promise<{ message: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/bans/${banId}/lift`,
        this.getFetchOptions({
          method: 'POST'
        })
      );
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('liftSteamBan error:', error);
      throw error;
    }
  }

  // =====================
  // Prefill Cache APIs
  // =====================

  // Get all cached apps
  static async getPrefillCachedApps(signal?: AbortSignal): Promise<CachedAppDto[]> {
    try {
      const res = await fetch(`${API_BASE}/prefill-admin/cache`, this.getFetchOptions({ signal }));
      return await this.handleResponse<CachedAppDto[]>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillCachedApps error:', error);
      throw error;
    }
  }

  // Check cache status for apps via daemon (verifies manifests/build versions)
  static async getPrefillCacheStatus(
    sessionId: string,
    appIds: string[],
    serviceBasePath = 'steam-daemon',
    signal?: AbortSignal
  ): Promise<PrefillCacheStatusDto> {
    try {
      const res = await fetch(
        `${API_BASE}/${serviceBasePath}/sessions/${sessionId}/cache-status`,
        this.getFetchOptions({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ appIds }),
          signal
        })
      );
      return await this.handleResponse<PrefillCacheStatusDto>(res);
    } catch (error: unknown) {
      if (!isAbortError(error)) console.error('getPrefillCacheStatus error:', error);
      throw error;
    }
  }

  // Check which apps are cached

  // Clear cache for a specific app

  // Clear entire prefill cache
  static async clearAllPrefillCache(): Promise<{ message: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/prefill-admin/cache`,
        this.getFetchOptions({
          method: 'DELETE'
        })
      );
      return await this.handleResponse<{ message: string }>(res);
    } catch (error: unknown) {
      console.error('clearAllPrefillCache error:', error);
      throw error;
    }
  }

  // =====================================================
  // Migration / Import APIs
  // =====================================================

  // Validate a connection string for migration
  static async validateMigrationConnection(
    connectionString: string
  ): Promise<MigrationValidationResult> {
    const res = await fetch(
      `${API_BASE}/migration/validate-connection?connectionString=${encodeURIComponent(connectionString)}`,
      this.getFetchOptions({ method: 'GET' })
    );
    return this.handleResponse<MigrationValidationResult>(res);
  }

  // Import from LancacheManager database
  static async importFromLancacheManager(
    connectionString: string,
    batchSize: number,
    overwriteExisting: boolean
  ): Promise<MigrationImportResult> {
    const res = await fetch(
      `${API_BASE}/migration/import-lancache-manager`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ connectionString, batchSize, overwriteExisting })
      })
    );
    return this.handleResponse<MigrationImportResult>(res);
  }

  // =====================================================
  // Epic Game Mappings API
  // =====================================================

  static async getEpicDaemonStatus(): Promise<EpicDaemonStatusDto> {
    const response = await fetch(`${API_BASE}/epic-daemon/status`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicDaemonStatusDto>(response);
  }

  // Battle.net daemon exposes the same service-agnostic status shape as Epic.
  static async getBattleNetDaemonStatus(): Promise<EpicDaemonStatusDto> {
    const response = await fetch(`${API_BASE}/battlenet-daemon/status`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicDaemonStatusDto>(response);
  }

  static async getRiotDaemonStatus(): Promise<EpicDaemonStatusDto> {
    const response = await fetch(`${API_BASE}/riot-daemon/status`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicDaemonStatusDto>(response);
  }

  // Xbox daemon exposes the same service-agnostic status shape as Epic.
  static async getXboxDaemonStatus(): Promise<EpicDaemonStatusDto> {
    const response = await fetch(`${API_BASE}/xbox-daemon/status`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicDaemonStatusDto>(response);
  }

  // Xbox game mappings share Epic's SHARED-catalog model (AdminOnly read). Resolution is automatic
  // (Rust ingest + RustLogProcessor post-pass + the xboxMapping schedule), so this exposes just the
  // catalog, stats and search for the game-library list.
  static async getXboxGameMappings(): Promise<XboxGameMappingDto[]> {
    const response = await fetch(`${API_BASE}/xbox/game-mappings`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<XboxGameMappingDto[]>(response);
  }

  static async getXboxMappingStats(): Promise<XboxMappingStats> {
    const response = await fetch(`${API_BASE}/xbox/game-mappings/stats`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<XboxMappingStats>(response);
  }

  static async searchXboxGames(query: string): Promise<XboxGameMappingDto[]> {
    const response = await fetch(
      `${API_BASE}/xbox/game-mappings/search?q=${encodeURIComponent(query)}`,
      { credentials: 'include' }
    );
    return ApiService.handleResponse<XboxGameMappingDto[]>(response);
  }

  // Xbox mapping auth — mirrors Epic's auth-status/login/logout shape (daemon-free MSA device-code).
  static async getXboxMappingAuthStatus(): Promise<XboxMappingAuthStatus> {
    const response = await fetch(`${API_BASE}/xbox/game-mappings/auth-status`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<XboxMappingAuthStatus>(response);
  }

  static async startXboxMappingLogin(
    signal?: AbortSignal
  ): Promise<{ userCode: string; verificationUri: string; expiresIn: number; interval: number }> {
    const response = await fetch(`${API_BASE}/xbox/game-mappings/auth/login`, {
      method: 'POST',
      credentials: 'include',
      signal
    });
    return ApiService.handleResponse<{
      userCode: string;
      verificationUri: string;
      expiresIn: number;
      interval: number;
    }>(response);
  }

  static async logoutXboxMapping(): Promise<void> {
    const response = await fetch(`${API_BASE}/xbox/game-mappings/auth`, {
      method: 'DELETE',
      credentials: 'include'
    });
    await ApiService.handleResponse(response);
  }

  // Cancels a pending device-code login poll (e.g. when the login modal is closed) WITHOUT clearing
  // credentials or signing out an already-authenticated account. Distinct from logoutXboxMapping.
  static async cancelXboxMappingLogin(): Promise<void> {
    const response = await fetch(`${API_BASE}/xbox/game-mappings/auth/cancel`, {
      method: 'POST',
      credentials: 'include'
    });
    await ApiService.handleResponse(response);
  }

  static async getEpicGameMappings(): Promise<EpicGameMappingDto[]> {
    const response = await fetch(`${API_BASE}/epic/game-mappings`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicGameMappingDto[]>(response);
  }

  static async getEpicMappingStats(): Promise<EpicMappingStats> {
    const response = await fetch(`${API_BASE}/epic/game-mappings/stats`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicMappingStats>(response);
  }

  static async searchEpicGames(query: string): Promise<EpicGameMappingDto[]> {
    const response = await fetch(
      `${API_BASE}/epic/game-mappings/search?q=${encodeURIComponent(query)}`,
      { credentials: 'include' }
    );
    return ApiService.handleResponse<EpicGameMappingDto[]>(response);
  }

  static async getEpicMappingAuthStatus(): Promise<EpicMappingAuthStatus> {
    const response = await fetch(`${API_BASE}/epic/game-mappings/auth-status`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicMappingAuthStatus>(response);
  }

  static async getEpicScheduleStatus(): Promise<EpicScheduleStatus> {
    const response = await fetch(`${API_BASE}/epic/game-mappings/schedule`, {
      credentials: 'include'
    });
    return ApiService.handleResponse<EpicScheduleStatus>(response);
  }

  static async setEpicRefreshInterval(intervalHours: number): Promise<void> {
    await fetch(`${API_BASE}/epic/game-mappings/schedule/interval`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify(intervalHours)
    });
  }

  static async startEpicMappingLogin(signal?: AbortSignal): Promise<{ authorizationUrl: string }> {
    const response = await fetch(`${API_BASE}/epic/game-mappings/auth/login`, {
      method: 'POST',
      credentials: 'include',
      signal
    });
    return ApiService.handleResponse<{ authorizationUrl: string }>(response);
  }

  static async logoutEpicMapping(): Promise<void> {
    const response = await fetch(`${API_BASE}/epic/game-mappings/auth`, {
      method: 'DELETE',
      credentials: 'include'
    });
    await ApiService.handleResponse(response);
  }

  static async completeEpicMappingAuth(
    authorizationCode: string,
    signal?: AbortSignal
  ): Promise<void> {
    const response = await fetch(`${API_BASE}/epic/game-mappings/auth/complete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ authorizationCode }),
      signal
    });
    await ApiService.handleResponse(response);
  }

  // ── Log Rotation ──────────────────────────────────────────────────────────

  // ── GC Management ─────────────────────────────────────────────────────────

  static async getGcManagementStatus(): Promise<unknown> {
    const response = await fetch(`${API_BASE}/system/gc-management/status`, this.getFetchOptions());
    return ApiService.handleResponse<unknown>(response);
  }

  // ── Events ────────────────────────────────────────────────────────────────

  static async getEventDownloads<T>(eventId: number): Promise<T> {
    const response = await fetch(
      `${API_BASE}/events/${eventId}/downloads?taggedOnly=true`,
      this.getFetchOptions()
    );
    return ApiService.handleResponse<T>(response);
  }

  // ── Memory ────────────────────────────────────────────────────────────────

  static async getMemoryStats<T>(): Promise<T> {
    const response = await fetch(`${API_BASE}/memory`, this.getFetchOptions());
    return ApiService.handleResponse<T>(response);
  }

  // ── Sessions ──────────────────────────────────────────────────────────────

  static async getSessions<T>(page: number, pageSize: number): Promise<T> {
    const response = await fetch(
      `${API_BASE}/sessions?page=${page}&pageSize=${pageSize}`,
      this.getFetchOptions()
    );
    return ApiService.handleResponse<T>(response);
  }

  static async revokeSession(sessionId: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/sessions/${encodeURIComponent(sessionId)}/revoke`,
      this.getFetchOptions({ method: 'PATCH' })
    );
    await ApiService.handleResponse(response);
  }

  static async deleteSession(sessionId: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/sessions/${encodeURIComponent(sessionId)}`,
      this.getFetchOptions({ method: 'DELETE' })
    );
    await ApiService.handleResponse(response);
  }

  static async getSessionPreferences<T>(sessionId: string): Promise<T> {
    const response = await fetch(
      `${API_BASE}/user-preferences/session/${encodeURIComponent(sessionId)}`,
      this.getFetchOptions()
    );
    return ApiService.handleResponse<T>(response);
  }

  static async saveSessionPreferences<T>(sessionId: string, preferences: unknown): Promise<T> {
    const response = await fetch(
      `${API_BASE}/user-preferences/session/${encodeURIComponent(sessionId)}`,
      this.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(preferences)
      })
    );
    return ApiService.handleResponse<T>(response);
  }

  static async setSessionRefreshRate(sessionId: string, refreshRate: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/sessions/${encodeURIComponent(sessionId)}/refresh-rate`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshRate })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async updateOwnClientInfo(payload: {
    publicIp: string | null;
    timezone: string | null;
    language: string | null;
    screenResolution: string | null;
  }): Promise<void> {
    const response = await fetch(
      `${API_BASE}/sessions/me/client-info`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      })
    );
    await ApiService.handleResponse(response);
  }

  static async toggleGuestPrefillService(
    sessionId: string,
    service: string,
    enabled: boolean
  ): Promise<void> {
    const response = await fetch(
      `${API_BASE}/auth/guest/prefill/toggle/${encodeURIComponent(sessionId)}?service=${service}`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async bulkResetSessionsToDefaults<T>(): Promise<T> {
    const response = await fetch(
      `${API_BASE}/sessions/bulk/reset-to-defaults`,
      this.getFetchOptions({ method: 'POST' })
    );
    return ApiService.handleResponse<T>(response);
  }

  static async bulkClearGuestSessions<T>(): Promise<T> {
    const response = await fetch(
      `${API_BASE}/sessions/bulk/clear-guests`,
      this.getFetchOptions({ method: 'DELETE' })
    );
    return ApiService.handleResponse<T>(response);
  }

  static async getGuestPrefillConfig<T>(
    service: 'prefill' | 'epic-prefill' | 'battlenet-prefill' | 'riot-prefill' | 'xbox-prefill'
  ): Promise<T> {
    const response = await fetch(
      `${API_BASE}/auth/guest/${service}/config`,
      this.getFetchOptions()
    );
    return ApiService.handleResponse<T>(response);
  }

  // ── Guest Config ──────────────────────────────────────────────────────────

  static async getGuestConfig<T>(): Promise<T> {
    const response = await fetch(`${API_BASE}/auth/guest/config`, this.getFetchOptions());
    return ApiService.handleResponse<T>(response);
  }

  static async setGuestConfigLock(isLocked: boolean): Promise<void> {
    const response = await fetch(
      `${API_BASE}/auth/guest/config/lock`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ isLocked })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async setExternalDbCredentials(payload: {
    host: string;
    port: number;
    database: string;
    username: string;
    password: string;
  }): Promise<{ success: boolean; message: string; restartRequired: boolean; error?: string }> {
    const response = await fetch(
      `${API_BASE}/setup/external`,
      ApiService.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      })
    );
    return response.json();
  }

  static async getGuestThemePreference<T>(): Promise<T> {
    const response = await fetch(`${API_BASE}/themes/preferences/guest`, this.getFetchOptions());
    return ApiService.handleResponse<T>(response);
  }

  static async setGuestThemePreference(themeId: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/themes/preferences/guest`,
      this.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ themeId })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async getDefaultGuestRefreshRate<T>(): Promise<T> {
    const response = await fetch(
      `${API_BASE}/system/default-guest-refresh-rate`,
      this.getFetchOptions()
    );
    return ApiService.handleResponse<T>(response);
  }

  static async setDefaultGuestRefreshRate(refreshRate: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/system/default-guest-refresh-rate`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshRate })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async setGuestRefreshRateLock(locked: boolean): Promise<void> {
    const response = await fetch(
      `${API_BASE}/system/guest-refresh-rate-lock`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ locked })
      })
    );
    await ApiService.handleResponse(response);
  }

  // ── Default Guest Preferences ─────────────────────────────────────────────

  static async getDefaultGuestPreferences<T>(): Promise<T> {
    const response = await fetch(
      `${API_BASE}/system/default-guest-preferences`,
      this.getFetchOptions()
    );
    return ApiService.handleResponse<T>(response);
  }

  static async setDefaultGuestPreference(key: string, value: boolean): Promise<void> {
    const response = await fetch(
      `${API_BASE}/system/default-guest-preferences/${key}`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async setDefaultGuestAllowedTimeFormats(formats: string[]): Promise<void> {
    const response = await fetch(
      `${API_BASE}/system/default-guest-preferences/allowed-time-formats`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ formats })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async setDefaultGuestTimeFormat(key: string, value: boolean): Promise<void> {
    const response = await fetch(
      `${API_BASE}/system/default-guest-preferences/${key}`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value })
      })
    );
    await ApiService.handleResponse(response);
  }

  static async updateGuestPrefillConfig<T>(
    service: 'prefill' | 'epic-prefill' | 'battlenet-prefill' | 'riot-prefill' | 'xbox-prefill',
    body: Record<string, unknown>
  ): Promise<T> {
    const response = await fetch(
      `${API_BASE}/auth/guest/${service}/config`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      })
    );
    return ApiService.handleResponse<T>(response);
  }

  // ── Prefill Defaults ──────────────────────────────────────────────────────

  static async getPrefillDefaults<T>(): Promise<T> {
    const response = await fetch(`${API_BASE}/system/prefill-defaults`, this.getFetchOptions());
    return ApiService.handleResponse<T>(response);
  }

  static async updatePrefillDefaults(body: Record<string, unknown>): Promise<void> {
    const response = await fetch(
      `${API_BASE}/system/prefill-defaults`,
      this.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      })
    );
    await ApiService.handleResponse(response);
  }

  // ── Themes ────────────────────────────────────────────────────────────────

  static async uploadTheme<T>(formData: FormData): Promise<T> {
    const response = await fetch(
      `${API_BASE}/themes/upload`,
      this.getFetchOptions({ method: 'POST', body: formData })
    );
    return ApiService.handleResponse<T>(response);
  }

  static async deleteTheme(themeId: string): Promise<void> {
    const response = await fetch(
      `${API_BASE}/themes/${themeId}`,
      this.getFetchOptions({ method: 'DELETE' })
    );
    // 404 is acceptable - theme might already be deleted
    if (!response.ok && response.status !== 404) {
      await ApiService.handleResponse(response);
    }
  }

  // ── Depot Mapping Config ──────────────────────────────────────────────────

  static async setDepotRebuildMode(mode: boolean | 'github'): Promise<void> {
    const response = await fetch(
      `${API_BASE}/depots/rebuild/config/mode`,
      this.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(mode)
      })
    );
    await ApiService.handleResponse(response);
  }

  static async setDepotRebuildInterval(intervalHours: number): Promise<void> {
    const response = await fetch(
      `${API_BASE}/depots/rebuild/config/interval`,
      this.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(intervalHours)
      })
    );
    await ApiService.handleResponse(response);
  }

  static async importDepotsFromLocal(): Promise<void> {
    const response = await fetch(
      `${API_BASE}/depots/import?source=local`,
      this.getFetchOptions({ method: 'POST' })
    );
    await ApiService.handleResponse(response);
  }

  // ── Steam Auth ────────────────────────────────────────────────────────────

  static async clearSteamAuth(): Promise<void> {
    const response = await fetch(
      `${API_BASE}/steam-auth`,
      this.getFetchOptions({ method: 'DELETE' })
    );
    await ApiService.handleResponse(response);
  }

  static async clearSteamApiKey(): Promise<void> {
    const response = await fetch(
      `${API_BASE}/steam-api-keys/current`,
      this.getFetchOptions({ method: 'DELETE' })
    );
    await ApiService.handleResponse(response);
  }

  // ── Version ───────────────────────────────────────────────────────────────

  static async getVersion(): Promise<string> {
    const response = await fetch(`${API_BASE}/version`);
    const data = await ApiService.handleResponse<{ version: string }>(response);
    // handleResponse returns undefined on an empty 2xx body; default to '' rather than deref.
    return data?.version ?? '';
  }

  static async getSchedules(signal?: AbortSignal): Promise<ServiceScheduleInfo[]> {
    try {
      const res = await fetch(`${API_BASE}/system/schedules`, this.getFetchOptions({ signal }));
      return await this.handleResponse<ServiceScheduleInfo[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getSchedules error:', error);
      }
      throw error;
    }
  }

  static async updateSchedule(serviceKey: string, intervalHours: number): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/${serviceKey}`,
        this.getFetchOptions({
          method: 'PUT',
          body: JSON.stringify({ intervalHours }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('updateSchedule error:', error);
      throw error;
    }
  }

  static async setScheduleRunOnStartup(serviceKey: string, runOnStartup: boolean): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/${serviceKey}/runOnStartup`,
        this.getFetchOptions({
          method: 'PUT',
          body: JSON.stringify({ runOnStartup }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('setScheduleRunOnStartup error:', error);
      throw error;
    }
  }

  static async setScheduleNotificationMode(
    serviceKey: string,
    mode: NotificationMode
  ): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/${serviceKey}/notificationMode`,
        this.getFetchOptions({
          method: 'PUT',
          body: JSON.stringify(mode),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('setScheduleNotificationMode error:', error);
      throw error;
    }
  }

  static async triggerSchedule(serviceKey: string): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/${serviceKey}/run`,
        this.getFetchOptions({ method: 'POST' })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('triggerSchedule error:', error);
      throw error;
    }
  }

  static async resetSchedules(): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/reset`,
        this.getFetchOptions({ method: 'POST' })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('resetSchedules error:', error);
      throw error;
    }
  }

  static async getScheduledPrefillConfig(signal?: AbortSignal): Promise<ScheduledPrefillConfigDto> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/scheduledPrefill/config`,
        this.getFetchOptions({ signal })
      );
      const config = await this.handleResponse<ScheduledPrefillConfigDto>(res);
      return {
        ...config,
        steam: { ...config.steam, selectedAppIds: config.steam.selectedAppIds ?? [] },
        epic: { ...config.epic, selectedAppIds: config.epic.selectedAppIds ?? [] },
        xbox: { ...config.xbox, selectedAppIds: config.xbox.selectedAppIds ?? [] },
        battleNet: {
          ...config.battleNet,
          selectedAppIds: config.battleNet.selectedAppIds ?? []
        },
        riot: { ...config.riot, selectedAppIds: config.riot.selectedAppIds ?? [] }
      };
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getScheduledPrefillConfig error:', error);
      }
      throw error;
    }
  }

  static async updateScheduledPrefillConfig(config: ScheduledPrefillConfigDto): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/scheduledPrefill/config`,
        this.getFetchOptions({
          method: 'PUT',
          body: JSON.stringify(config),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('updateScheduledPrefillConfig error:', error);
      throw error;
    }
  }

  static async getScheduledPrefillSchedule(
    signal?: AbortSignal
  ): Promise<ScheduledPrefillServiceScheduleDto[]> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/scheduledPrefill/schedule`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<ScheduledPrefillServiceScheduleDto[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getScheduledPrefillSchedule error:', error);
      }
      throw error;
    }
  }

  static async getPersistentPrefillContainers(
    signal?: AbortSignal
  ): Promise<PersistentPrefillContainerDto[]> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/list`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<PersistentPrefillContainerDto[]>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getPersistentPrefillContainers error:', error);
      }
      throw error;
    }
  }

  /**
   * Lists owned games (+ up-to-date cached app ids) for the RUNNING persistent session of a service.
   * Hits the AdminOnly endpoint that bypasses per-user session ownership (safe because it is
   * restricted to persistent/system-owned sessions), avoiding the 403 from the user-scoped
   * `/{service}-daemon/sessions/{id}/games` route which checks session.UserId == admin session id.
   */
  static async getPersistentPrefillGames(
    service: PersistentPrefillServiceId,
    signal?: AbortSignal
  ): Promise<PersistentPrefillGamesDto> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/games?service=${encodeURIComponent(service)}`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<PersistentPrefillGamesDto>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getPersistentPrefillGames error:', error);
      }
      throw error;
    }
  }

  static async startPersistentLogin(
    service: PersistentPrefillServiceId
  ): Promise<PersistentChallengeResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/login`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ service }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<PersistentChallengeResponse>(res);
    } catch (error: unknown) {
      console.error('startPersistentLogin error:', error);
      throw error;
    }
  }

  /**
   * Parses the typed 409 body `{ error, state }` from the persistent-login session-pinning REST
   * leg (RC3 fix) and attaches it as `.cause`, mirroring the
   * `getPersistentChallenge` 404 pattern below - a caller detects this structurally via
   * `.cause.error`, never by sniffing the message text.
   */
  private static async buildPersistentSessionConflictError(
    res: Response
  ): Promise<Error & { cause: PersistentSessionConflictInfo }> {
    const bodyText = await res.text().catch(() => '');
    let errorKind: PersistentSessionConflictInfo['error'] = 'session_replaced';
    let state = '';
    let message = 'HTTP 409: persistent session replaced';
    try {
      const body = bodyText ? JSON.parse(bodyText) : null;
      if (body?.error === 'credential_rejected' || body?.error === 'session_replaced') {
        errorKind = body.error;
      }
      if (typeof body?.state === 'string') {
        state = body.state;
      }
      if (typeof body?.error === 'string' && body.error) {
        message = `HTTP 409: ${body.error}`;
      }
    } catch {
      // Not JSON - fall through with the defaults above
    }
    const conflictError = new Error(message) as Error & { cause?: PersistentSessionConflictInfo };
    conflictError.cause = { status: 409, error: errorKind, state };
    return conflictError as Error & { cause: PersistentSessionConflictInfo };
  }

  static async providePersistentCredential(
    service: PersistentPrefillServiceId,
    challenge: CredentialChallenge,
    credential: string,
    sessionId: string
  ): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/credential`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ service, challenge, credential, sessionId }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      if (res.status === 409) {
        throw await this.buildPersistentSessionConflictError(res);
      }
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('providePersistentCredential error:', error);
      throw error;
    }
  }

  static async getPersistentChallenge(
    service: PersistentPrefillServiceId,
    timeoutSeconds: number | undefined,
    sessionId: string
  ): Promise<PersistentChallengeResponse> {
    try {
      const params = new URLSearchParams({ service, sessionId });
      if (timeoutSeconds !== undefined) {
        params.set('timeoutSeconds', timeoutSeconds.toString());
      }
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/challenge?${params.toString()}`,
        this.getFetchOptions()
      );
      if (res.status === 404) {
        // Handled here, not by the generic handleResponse below: ResolveRunningPersistentSession's
        // typed NotFound body has an `error` field, so handleResponse's structured-error branch
        // would throw that message with no "HTTP 404" prefix, breaking the poller's terminal-404
        // detection (usePersistentPrefillAuth's isPersistentChallengeNotFoundError). Parse the
        // typed body ourselves and attach status+state as `.cause` so detection is structural.
        const bodyText = await res.text().catch(() => '');
        let state: PersistentSessionNotFoundState = 'notStarted';
        let message = 'HTTP 404: persistent session not found';
        try {
          const body = bodyText ? JSON.parse(bodyText) : null;
          if (body?.state === 'errored' || body?.state === 'notStarted') {
            state = body.state;
          }
          if (typeof body?.error === 'string' && body.error) {
            message = `HTTP 404: ${body.error}`;
          }
        } catch {
          // Not JSON - fall through with the defaults above
        }
        const notFoundError = new Error(message) as Error & {
          cause?: PersistentSessionNotFoundInfo;
        };
        notFoundError.cause = { status: 404, state };
        throw notFoundError;
      }
      if (res.status === 409) {
        // sessionId no longer matches the active session (RC3) - never serve another session's
        // challenge. See buildPersistentSessionConflictError.
        throw await this.buildPersistentSessionConflictError(res);
      }
      return await this.handleResponse<PersistentChallengeResponse>(res);
    } catch (error: unknown) {
      console.error('getPersistentChallenge error:', error);
      throw error;
    }
  }

  static async cancelPersistentLogin(
    service: PersistentPrefillServiceId,
    sessionId: string
  ): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/cancel-login`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ service, sessionId }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      // Per plan.md F3: a sessionId mismatch here is an idempotent 200 no-op (it must NOT cancel a
      // replacement session's login), so there is no 409 branch to special-case - unlike challenge/
      // provide-credential above.
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('cancelPersistentLogin error:', error);
      throw error;
    }
  }

  static async logoutPersistentPrefillContainer(
    service: PersistentPrefillServiceId
  ): Promise<PersistentLogoutResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/logout`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ service }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<PersistentLogoutResponse>(res);
    } catch (error: unknown) {
      console.error('logoutPersistentPrefillContainer error:', error);
      throw error;
    }
  }

  /**
   * Wipes stored logins for every persistent-container service in one call (logs out any running
   * container and removes the saved credentials of stopped ones). The exact per-service result
   * shape is intentionally left as `unknown` here - callers must normalize it defensively rather
   * than assume specific fields, since it comes from a separately-shipped backend endpoint.
   */
  static async clearPersistentLogins(): Promise<unknown> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/clear-logins`,
        this.getFetchOptions({ method: 'POST' })
      );
      return await this.handleResponse<unknown>(res);
    } catch (error: unknown) {
      console.error('clearPersistentLogins error:', error);
      throw error;
    }
  }

  static async startPersistentPrefillContainer(service: PersistentPrefillServiceId): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/start`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ service }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('startPersistentPrefillContainer error:', error);
      throw error;
    }
  }

  static async setPersistentPrefillSelectedApps(
    service: PersistentPrefillServiceId,
    appIds: string[]
  ): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/selected-apps`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ service, appIds }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('setPersistentPrefillSelectedApps error:', error);
      throw error;
    }
  }

  static async startPersistentPrefill(
    service: PersistentPrefillServiceId,
    options: {
      appIds: string[];
      force?: boolean;
      operatingSystems?: string[];
      maxConcurrency?: number | null;
    }
  ): Promise<{ success: boolean; errorMessage?: string }> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/prefill`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({
            service,
            appIds: options.appIds,
            all: false,
            recent: false,
            force: options.force ?? false,
            operatingSystems: options.operatingSystems,
            maxConcurrency: options.maxConcurrency ?? undefined
          }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<{ success: boolean; errorMessage?: string }>(res);
    } catch (error: unknown) {
      console.error('startPersistentPrefill error:', error);
      throw error;
    }
  }

  static async cancelPersistentPrefill(service: PersistentPrefillServiceId): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/cancel-prefill`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ service }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('cancelPersistentPrefill error:', error);
      throw error;
    }
  }

  static async stopPersistentPrefillContainer(sessionId: string): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/stop`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ sessionId }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('stopPersistentPrefillContainer error:', error);
      throw error;
    }
  }

  static async getPersistentPrefillValidity(
    signal?: AbortSignal
  ): Promise<PersistentPrefillValiditySettings> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/validity`,
        this.getFetchOptions({ signal })
      );
      return await this.handleResponse<PersistentPrefillValiditySettings>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getPersistentPrefillValidity error:', error);
      }
      throw error;
    }
  }

  static async updatePersistentPrefillValidity(
    settings: PersistentPrefillValiditySettings
  ): Promise<void> {
    try {
      const res = await fetch(
        `${API_BASE}/system/prefill/persistent/validity`,
        this.getFetchOptions({
          method: 'PUT',
          body: JSON.stringify(settings),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      await this.handleResponse<void>(res);
    } catch (error: unknown) {
      console.error('updatePersistentPrefillValidity error:', error);
      throw error;
    }
  }

  static async getMetricsSecurity(signal?: AbortSignal): Promise<MetricsSecurityResponse> {
    try {
      const res = await fetch(`${API_BASE}/metrics/security`, this.getFetchOptions({ signal }));
      return await this.handleResponse<MetricsSecurityResponse>(res);
    } catch (error: unknown) {
      if (isAbortError(error)) {
        // Silently ignore abort errors
      } else {
        console.error('getMetricsSecurity error:', error);
      }
      throw error;
    }
  }

  static async setMetricsSecurity(enabled: boolean | null): Promise<MetricsSecurityResponse> {
    try {
      const res = await fetch(
        `${API_BASE}/metrics/security`,
        this.getFetchOptions({
          method: 'POST',
          body: JSON.stringify({ enabled }),
          headers: { 'Content-Type': 'application/json' }
        })
      );
      return await this.handleResponse<MetricsSecurityResponse>(res);
    } catch (error: unknown) {
      console.error('setMetricsSecurity error:', error);
      throw error;
    }
  }

  static async runAllSchedules(): Promise<{ triggeredCount: number }> {
    try {
      const res = await fetch(
        `${API_BASE}/system/schedules/run-all`,
        this.getFetchOptions({ method: 'POST' })
      );
      return await this.handleResponse<{ triggeredCount: number }>(res);
    } catch (error: unknown) {
      console.error('runAllSchedules error:', error);
      throw error;
    }
  }

  // Status Check (Management > Status Check DNS diagnostics)
  static async getStatusCheck(signal?: AbortSignal): Promise<StatusCheckStatusResponse> {
    const response = await fetch(`${API_BASE}/status-check`, this.getFetchOptions({ signal }));
    return this.handleResponse<StatusCheckStatusResponse>(response);
  }

  static async runStatusCheck(): Promise<StatusCheckRunResponse> {
    const response = await fetch(
      `${API_BASE}/status-check/run`,
      this.getFetchOptions({ method: 'POST' })
    );
    return this.handleResponse<StatusCheckRunResponse>(response);
  }

  static async testStatusCheckDomain(domain: string): Promise<StatusCheckTestDomainResponse> {
    const response = await fetch(
      `${API_BASE}/status-check/test-domain`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ domain })
      })
    );
    return this.handleResponse<StatusCheckTestDomainResponse>(response);
  }

  static async refreshCacheDomains(): Promise<StatusCheckRefreshDomainsResponse> {
    const response = await fetch(
      `${API_BASE}/status-check/refresh-domains`,
      this.getFetchOptions({ method: 'POST' })
    );
    return this.handleResponse<StatusCheckRefreshDomainsResponse>(response);
  }

  static async getCacheDomains(signal?: AbortSignal): Promise<StatusCheckDomainsResponse> {
    const response = await fetch(
      `${API_BASE}/status-check/domains`,
      this.getFetchOptions({ signal })
    );
    return this.handleResponse<StatusCheckDomainsResponse>(response);
  }

  static async setStatusCheckResolverMode(
    mode: StatusCheckResolverMode
  ): Promise<StatusCheckResolverModeResponse> {
    const response = await fetch(
      `${API_BASE}/status-check/resolver-mode`,
      this.getFetchOptions({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode })
      })
    );
    return this.handleResponse<StatusCheckResolverModeResponse>(response);
  }
}

// Prefill admin types
export interface PrefillSessionDto {
  id: number;
  sessionId: string;
  containerId?: string;
  containerName?: string;
  steamUsername?: string;
  status: PrefillSessionStatus;
  isAuthenticated: boolean;
  isPrefilling: boolean;
  createdAtUtc: string;
  endedAtUtc?: string;
  expiresAtUtc: string;
  terminationReason?: string;
  terminatedBy?: string;
  isLive: boolean;
  platform?: string;
  username?: string;
  isPersistent?: boolean;
}

export interface DnsTestResult {
  domain: string;
  resolvedIps?: string[];
  isPrivateIp: boolean;
  success: boolean;
  error?: string;
}

// Status Check (Management > Status Check) - mirrors the backend StatusCheckResponses contract
export interface StatusCheckDomainResult {
  /** Hostname actually queried (wildcard entries probe a literal "status-check" label). */
  domain: string;
  /** Raw cache-domains list entry (display this, e.g. "*.cdn.blizzard.com"). */
  originalEntry: string;
  service: string;
  /** v1.4 semantics: resolved = heartbeat-verified live OR matches expectedIps; mismatched =
   *  public answer with no heartbeat/match; unverified = private answer with no heartbeat/match.
   *  blocked (v1.5) = every answer is a deliberate blackhole (0.0.0.0/::) - benign, never a failure. */
  status: 'resolved' | 'mismatched' | 'unresolved' | 'unverified' | 'blocked';
  resolvedIps: string[];
  expectedIps: string[];
  /** True when a resolved IP answered /lancache-heartbeat during this sweep (v1.4). */
  heartbeatVerified: boolean;
  /** X-LanCache-Processed-By hostname of the verifying IP; null when not heartbeat-verified. */
  servedBy: string | null;
  error: string | null;
  latencyMs: number | null;
  /** Public-edge HTTP/HTTPS probe of this hostname (v1.5), present on swept and ad hoc tested
   *  domains. Null/absent on pre-v1.5 snapshots and when the probe crashed unexpectedly. */
  edgeProbe?: StatusCheckHostProtocolProbeResult | null;
}

export interface StatusCheckServiceResult {
  service: string;
  description: string;
  /** "disabled" = DISABLE_<SERVICE>=true in lancache-dns; skipped by the sweep (domains: []).
   *  "unverified" = resolving, but no expected cache IP was known to verify against (v1.3). */
  status: 'resolved' | 'partial' | 'unresolved' | 'disabled' | 'unverified';
  resolvedCount: number;
  totalCount: number;
  domains: StatusCheckDomainResult[];
}

export interface StatusCheckHeartbeatResult {
  reachable: boolean;
  servedBy: string | null;
  cacheIp: string | null;
  error: string | null;
}

export interface StatusCheckSummary {
  totalServices: number;
  resolvedServices: number;
  partialServices: number;
  unresolvedServices: number;
  /** Services intentionally not cached (DISABLE_* in lancache-dns) - excluded from verdict math. */
  disabledServices: number;
  /** Services resolving with no expected cache IP to verify against (v1.3) - excluded from verdict math. */
  unverifiedServices: number;
  totalDomains: number;
  resolvedDomains: number;
  /** Domains resolving with no expected cache IP to verify against (v1.3). */
  unverifiedDomains: number;
}

/** One cache node behind the resolved fleet, identified by its X-LanCache-Processed-By hostname,
 *  with every verified IP that answered as it. */
export interface StatusCheckCacheNodeInfo {
  servedBy: string;
  ips: string[];
}

export type StatusCheckContentAvailability = 'available' | 'logMissing' | 'unreadable';

export type StatusCheckProtocolStatus =
  | 'notRun'
  | 'bothUsable'
  | 'httpUsable'
  | 'httpsOnlyCandidate'
  | 'inconclusive';

export type StatusCheckProtocolOutcome =
  | 'content'
  | 'redirectToHttps'
  | 'otherRedirect'
  | 'denied'
  | 'notFoundOrStale'
  | 'rangeRejected'
  | 'serverError'
  | 'tlsCertificateFailure'
  | 'connectFailure'
  | 'timeout'
  | 'invalidResponse';

export interface StatusCheckCacheTraversalEvidence {
  outcome: 'hit' | 'miss';
  statusCode: number;
  bytes: number;
}

export interface StatusCheckProtocolProbeResult {
  outcome: StatusCheckProtocolOutcome;
  statusCode: number | null;
  redirectScheme: string | null;
}

export interface StatusCheckContentPathEdgeResult {
  address: string;
  addressFamily: 'ipv4' | 'ipv6';
  http: StatusCheckProtocolProbeResult;
  https: StatusCheckProtocolProbeResult;
}

export interface StatusCheckContentPathResult {
  service: string;
  host: string;
  pathDisplay: string;
  sampleObservedAtUtc: string | null;
  cacheEvidence: StatusCheckCacheTraversalEvidence | null;
  protocolStatus: StatusCheckProtocolStatus;
  /** Bounded enum-like backend value. The UI maps known values and never prints this raw. */
  protocolReason: string | null;
  consensusEdges: number;
  totalPublicEdges: number;
  edges: StatusCheckContentPathEdgeResult[];
}

export interface StatusCheckContentReport {
  availability: StatusCheckContentAvailability;
  checkedAtUtc: string | null;
  scanTruncated: boolean;
  scannedBytes: number;
  paths: StatusCheckContentPathResult[];
}

export interface StatusCheckResult {
  startedAtUtc: string;
  completedAtUtc: string;
  resolverSource: 'configured' | 'detected' | 'system';
  dnsServer: string | null;
  expectedCacheIps: string[];
  /** Where the expected cache IP(s) came from (contract amendment v1.1/v1.2); "none" means no
   *  source could determine one (expectedCacheIps is empty in that case). */
  expectedIpSource: 'config' | 'dockerInspect' | 'envFile' | 'detected' | 'none';
  heartbeat: StatusCheckHeartbeatResult;
  services: StatusCheckServiceResult[];
  summary: StatusCheckSummary;
  /** Mean latencyMs across every domain result with a non-null latency, regardless of status;
   *  null when nothing in the sweep measured one. */
  avgLatencyMs: number | null;
  /** Verified cache IPs grouped by the hostname that answered their heartbeat; empty when nothing
   *  heartbeat-verified during this sweep. */
  cacheNodes: StatusCheckCacheNodeInfo[];
  /** Optional for stale browser snapshots created before content-path checks were introduced. */
  contentReport?: StatusCheckContentReport | null;
}

export interface StatusCheckDomainsSource {
  repoUrl: string;
  branch: string;
  envFilePath: string | null;
  /** Where CACHE_DOMAINS_REPO/BRANCH/NOFETCH were read from (contract amendment v1.2). */
  envSource: 'dockerInspect' | 'envFile' | 'defaults';
  noFetch: boolean;
  fetchedAtUtc: string | null;
  fromCache: boolean;
  /** Set when the last fetch attempt failed or NOFETCH blocked a fetch with no disk copy. */
  error: string | null;
}

/** Forces which DNS-resolution strategy the next sweep uses. "auto" probes every
 *  strategy and heartbeat-verifies; "bridge" queries the lancache-dns container's
 *  bridge IP; "host" queries the host DNS via the docker bridge gateway / localhost. */
export type StatusCheckResolverMode = 'auto' | 'bridge' | 'host';

export interface StatusCheckStatusResponse {
  lastResult: StatusCheckResult | null;
  domainsSource: StatusCheckDomainsSource | null;
  isRunning: boolean;
  operationId: string | null;
  resolverMode: StatusCheckResolverMode;
}

interface StatusCheckRunResponse {
  operationId: string;
}

interface StatusCheckResolverModeResponse {
  resolverMode: StatusCheckResolverMode;
}

/** Current public-edge HTTP/HTTPS behaviour of one ad hoc tested hostname (v1.5), produced by
 *  the same resolve-and-probe pipeline as the sweep's content lane. */
export interface StatusCheckHostProtocolProbeResult {
  protocolStatus: StatusCheckProtocolStatus;
  /** Bounded enum-like backend value. The UI maps known values and never prints this raw. */
  protocolReason: string | null;
  consensusEdges: number;
  totalPublicEdges: number;
  edges: StatusCheckContentPathEdgeResult[];
}

export interface StatusCheckTestDomainResponse {
  /** Carries the public-edge probe in result.edgeProbe (v1.5). */
  result: StatusCheckDomainResult;
  /** Attempted against the domain's resolved IP when it resolves; null otherwise. */
  heartbeat: StatusCheckHeartbeatResult | null;
}

interface StatusCheckRefreshDomainsResponse {
  domainsSource: StatusCheckDomainsSource;
  serviceCount: number;
  domainCount: number;
}

export interface StatusCheckDomainGroup {
  name: string;
  description: string;
  domains: string[];
}

interface StatusCheckDomainsResponse {
  services: StatusCheckDomainGroup[];
}

export interface NetworkDiagnostics {
  internetConnectivity: boolean;
  internetConnectivityError?: string;
  internetConnectivityIpv4?: boolean | null;
  internetConnectivityIpv4Error?: string;
  internetConnectivityIpv6?: boolean | null;
  internetConnectivityIpv6Error?: string;
  dnsResults: DnsTestResult[];
  testedAt: string;
  /** True if container uses host networking - steam-prefill will detect lancache via localhost/gateway fallback */
  useHostNetworking?: boolean;
  /** Lancache server IP injected via the LANCACHE_IP env var. Null when no cache IP was determined. */
  lancacheIpInjected: string | null;
  /** How the injected IP was located: config | dockerInspect | envFile | detected | none. */
  lancacheIpSource?: string;
}

export interface DaemonSessionDto {
  id: string;
  userId: string;
  containerName: string;
  status: DaemonSessionStatus;
  authState: DaemonAuthState;
  isPrefilling: boolean;
  createdAt: string;
  endedAt?: string;
  expiresAt: string;
  timeRemainingSeconds: number;
  // Client info for admin visibility
  ipAddress?: string;
  operatingSystem?: string;
  browser?: string;
  lastSeenAt: string;
  steamUsername?: string;
  // Current prefill progress info for admin visibility
  currentAppId?: string;
  currentAppName?: string;
  // Total bytes transferred during this session
  totalBytesTransferred?: number;
  // Network diagnostics results
  networkDiagnostics?: NetworkDiagnostics;
  platform?: string;
  username?: string;
  isPersistent?: boolean;
  isTemporary?: boolean;
  needsRelogin?: boolean;
}

interface PrefillSessionsResponse {
  sessions: PrefillSessionDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface BannedSteamUserDto {
  id: number;
  username: string;
  banReason?: string;
  bannedAtUtc: string;
  bannedBy?: string;
  expiresAtUtc?: string;
  isLifted: boolean;
  liftedAtUtc?: string;
  liftedBy?: string;
  isActive: boolean;
}

export interface PrefillHistoryEntryDto {
  id: number;
  sessionId: string;
  appId: string;
  appName?: string;
  startedAtUtc: string;
  completedAtUtc?: string;
  bytesDownloaded: number;
  totalBytes: number;
  status: string;
  errorMessage?: string;
}

interface CachedAppDto {
  appId: string;
  appName?: string;
  depotCount: number;
  totalBytes: number;
  cachedAtUtc: string;
  cachedBy?: string;
}

interface PrefillCacheStatusDto {
  upToDateAppIds: string[];
  outdatedAppIds: string[];
  message?: string;
}

interface PersistentPrefillOwnedGameDto {
  appId: string;
  name: string;
}

interface PersistentPrefillGamesDto {
  games: PersistentPrefillOwnedGameDto[];
  cachedAppIds: string[];
}

interface MigrationValidationResult {
  valid: boolean;
  message: string;
  recordCount?: number;
}

interface MigrationImportResult {
  message: string;
  totalRecords: number;
  imported: number;
  skipped: number;
  errors: number;
  backupPath?: string;
}

export default ApiService;
