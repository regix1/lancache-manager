import { createContext, type ReactNode } from 'react';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  DownloadTotals,
  ServiceFilterOption,
  GameDetectionSummary,
  ServiceDetectionSummary,
  SparklineDataResponse,
  HourlyActivityResponse,
  CacheSnapshotResponse
} from '../../types';

export interface DashboardBatchResponse {
  cache: CacheInfo | null;
  clients: ClientStat[] | null;
  services: ServiceStat[] | null;
  dashboard: DashboardStats | null;
  downloadTotals: DownloadTotals | null;
  filteredDownloadTotals: DownloadTotals | null;
  serviceOptions: ServiceFilterOption[] | null;
  clientOptions: string[] | null;
  recentDownloads: RecentDownloadsSection | null;
  detection: CachedDetectionResponse | null;
  sparklines: SparklineDataResponse | null;
  hourlyActivity: HourlyActivityResponse | null;
  cacheSnapshot: CacheSnapshotResponse | null;
}

/**
 * One game's downloads folded into a single panel row, built server-side. Mirrors the C#
 * `DashboardGameGroup` in `DashboardBatchService.cs`; `scripts/test-dashboard-batch-shape.mjs`
 * compares the two field lists, because no compiler sees both halves of this wire contract.
 */
export interface DashboardGameGroup {
  id: string;
  name: string;
  /** Content, or metadata when every member carried zero bytes. The server answers this from the
   *  group's summed bytes, so it never spells a game; a game is told by the `game-` id prefix. */
  type: 'content' | 'metadata';
  service: string;
  totalBytes: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
  /** How many downloads the group stands for, over the whole selected range. The server groups on
   *  the identity columns each row already carries, so a game downloaded both today and last week
   *  counts both. The same goes for the bytes and the client list, and for an active game appended
   *  below the newest hundred, which carries its range total like every other row. */
  count: number;
  /** Start time of the newest member, which is what the panel orders on. */
  lastSeen: string;
  /** Every member is evicted. */
  isEvicted: boolean;
  /** Some members are evicted and some are not. */
  isPartiallyEvicted: boolean;
  /** The name is a resolved title rather than a service fallback. */
  hasRealGameName: boolean;
  gameAppId?: number;
  gameName?: string;
  /** The distinct client addresses that downloaded this game, so the dropdown can narrow without
   *  a refetch and the "N clients" text can read this list's length. */
  clientIps: string[];
  /** Member download ids, newest first, so an event badge on an older member still resolves. The
   *  server sends at most five hundred, which is what the batch event route answers in one call,
   *  and never more than `count`. A badge on a member older than those five hundred is not drawn. */
  downloadIds: number[];
}

/**
 * The recent-downloads section of the batch. The groups are what the panel draws. The rows are the
 * raw downloads the live previews fingerprint by id, which groups cannot answer, so the section
 * carries both.
 */
export interface RecentDownloadsSection {
  groups: DashboardGameGroup[];
  rows: Download[];
}

/**
 * The service and client that `filteredDownloadTotals` and the recent slice are narrowed to. Both
 * carry 'all' rather than being absent, which is the value the dropdowns themselves use. A dropdown
 * entry can name a client group, so `client` holds that group's member addresses comma-separated;
 * a single address is that list with one member.
 */
export interface DownloadFilters {
  service: string;
  client: string;
}

/**
 * Detection payload shipped by /api/dashboard/batch. Uses slim summary DTOs
 * to minimize payload size - the full GameCacheInfo / ServiceCacheInfo shapes
 * (with cache_file_paths, sample_urls, depot_ids, datasources) are only
 * served by /api/games/cached-detection for the Management tab.
 */
export interface CachedDetectionResponse {
  hasCachedResults: boolean;
  games?: GameDetectionSummary[];
  services?: ServiceDetectionSummary[];
  totalGamesDetected?: number;
  totalServicesDetected?: number;
  lastDetectionTime?: string;
  /** Deduplicated total size of active game cache files (from last detection scan). */
  games_on_disk_bytes?: number;
  /** Count of non-evicted games with cache files on disk. */
  games_on_disk_count?: number;
  /** Deduplicated total size of matched game and service cache files. */
  identified_cache_bytes?: number;
  /** Portion of identified_cache_bytes attributed to non-game services. */
  identified_service_bytes?: number;
  /** UTC timestamp when deduplicated on-disk totals were last computed. */
  detection_summary_computed_at?: string;
  /** True when cache usage drifted since the last detection run; cleared by re-running detection. */
  detection_stale?: boolean;
}

interface DashboardDataContextType {
  // Cache info
  cacheInfo: CacheInfo | null;

  // Stats
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;

  // Downloads
  latestDownloads: Download[];
  downloadGroups: DashboardGameGroup[];
  downloadTotals: DownloadTotals | null;
  filteredDownloadTotals: DownloadTotals | null;
  serviceOptions: ServiceFilterOption[];
  clientOptions: string[];

  // Game detection
  gameDetectionData: CachedDetectionResponse | null;
  gameDetectionLookup: Map<number, GameDetectionSummary> | null;
  gameDetectionByName: Map<string, GameDetectionSummary> | null;
  gameDetectionByService: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;

  // Sparklines & widget data
  sparklines: SparklineDataResponse | null;
  hourlyActivity: HourlyActivityResponse | null;
  cacheSnapshot: CacheSnapshotResponse | null;

  // Loading & Error states
  loading: boolean;
  isRefreshing: boolean;
  error: string | null;
  connectionStatus: string;
  /** True while the latest batch had failed sections; cleared by the next fully successful refresh. */
  dataStale: boolean;
  /** Sections whose sub-query returned null, so a widget can tell a failed load from an empty one. */
  failedSectionKeys: (keyof DashboardBatchResponse)[];

  // Methods
  refreshData: (forceRefresh?: boolean) => Promise<void>;
  updateData: (updater: {
    cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
    clientStats?: (prev: ClientStat[]) => ClientStat[];
    serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
    dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
  }) => void;
  /** Narrows the download totals and the recent slice, then refetches under the new filters. */
  setDownloadFilters: (filters: DownloadFilters) => void;
}

export interface DashboardDataProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

export const DashboardDataContext = createContext<DashboardDataContextType | undefined>(undefined);

/**
 * True while a download-filter change is still being answered. Kept out of the value above because
 * every dashboard card reads that one and would re-render on a flag only the recent-downloads panel
 * has a use for.
 */
export const DownloadFilterFetchContext = createContext(false);
