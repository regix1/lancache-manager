import { createContext, type ReactNode } from 'react';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  GameDetectionSummary,
  ServiceDetectionSummary,
  SparklineDataResponse,
  HourlyActivityResponse,
  CacheSnapshotResponse,
  CacheGrowthResponse
} from '../../types';

export interface DashboardBatchResponse {
  cache: CacheInfo | null;
  clients: ClientStat[] | null;
  services: ServiceStat[] | null;
  dashboard: DashboardStats | null;
  downloads: Download[] | null;
  detection: CachedDetectionResponse | null;
  sparklines: SparklineDataResponse | null;
  hourlyActivity: HourlyActivityResponse | null;
  cacheSnapshot: CacheSnapshotResponse | null;
  cacheGrowth: CacheGrowthResponse | null;
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
  cacheGrowth: CacheGrowthResponse | null;

  // Loading & Error states
  loading: boolean;
  isRefreshing: boolean;
  error: string | null;
  connectionStatus: string;
  /** True while the latest batch had failed sections; cleared by the next fully successful refresh. */
  dataStale: boolean;

  // Methods
  refreshData: (forceRefresh?: boolean) => Promise<void>;
  updateData: (updater: {
    cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
    clientStats?: (prev: ClientStat[]) => ClientStat[];
    serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
    dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
    latestDownloads?: (prev: Download[]) => Download[];
  }) => void;
}

export interface DashboardDataProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

export const DashboardDataContext = createContext<DashboardDataContextType | undefined>(undefined);
