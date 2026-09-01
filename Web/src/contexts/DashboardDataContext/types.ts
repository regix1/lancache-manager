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
  recentDownloads: Download[] | null;
  detection: CachedDetectionResponse | null;
  sparklines: SparklineDataResponse | null;
  hourlyActivity: HourlyActivityResponse | null;
  cacheSnapshot: CacheSnapshotResponse | null;
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
