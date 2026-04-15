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
 * to minimize payload size — the full GameCacheInfo / ServiceCacheInfo shapes
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
