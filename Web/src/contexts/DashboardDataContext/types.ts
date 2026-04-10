import { createContext, type ReactNode } from 'react';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  GameCacheInfo,
  ServiceCacheInfo,
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

export interface CachedDetectionResponse {
  hasCachedResults: boolean;
  games?: GameCacheInfo[];
  services?: ServiceCacheInfo[];
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
  gameDetectionLookup: Map<number, GameCacheInfo> | null;
  gameDetectionByName: Map<string, GameCacheInfo> | null;
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
  removeFromDetection: (target: {
    gameAppId?: number;
    gameName?: string;
    serviceName?: string;
  }) => void;
  clearEvictionFromDetection: (target: { gameAppId?: number; serviceName?: string }) => void;
}

export interface DashboardDataProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

export const DashboardDataContext = createContext<DashboardDataContextType | undefined>(undefined);
