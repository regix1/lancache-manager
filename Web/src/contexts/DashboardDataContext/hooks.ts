import { createContextHook } from '../createContextHook';
import { DashboardDataContext, type CachedDetectionResponse } from './types';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  GameDetectionSummary,
  SparklineDataResponse,
  HourlyActivityResponse,
  CacheSnapshotResponse,
  CacheGrowthResponse
} from '../../types';

// Every selector below reads the same provider, so they share one guard instead of restating it.
const useDashboardDataContext = createContextHook(DashboardDataContext, 'useDashboardData');

export const useStats = () => {
  const context = useDashboardDataContext();
  return {
    cacheInfo: context.cacheInfo,
    clientStats: context.clientStats,
    serviceStats: context.serviceStats,
    dashboardStats: context.dashboardStats,
    loading: context.loading,
    isRefreshing: context.isRefreshing,
    error: context.error,
    connectionStatus: context.connectionStatus,
    dataStale: context.dataStale,
    refreshStats: context.refreshData,
    updateStats: (updater: {
      cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
      clientStats?: (prev: ClientStat[]) => ClientStat[];
      serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
      dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
    }) => context.updateData(updater)
  };
};

export const useDownloads = () => {
  const context = useDashboardDataContext();
  return {
    latestDownloads: context.latestDownloads,
    loading: context.loading,
    error: context.error,
    refreshDownloads: async () => context.refreshData(true),
    updateDownloads: (updater: { latestDownloads?: (prev: Download[]) => Download[] }) =>
      context.updateData(updater)
  };
};

export const useGameDetection = (): {
  detectionLookup: Map<number, GameDetectionSummary> | null;
  detectionByName: Map<string, GameDetectionSummary> | null;
  detectionByService: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
  gameDetectionData: CachedDetectionResponse | null;
  isLoading: boolean;
} => {
  const context = useDashboardDataContext();
  return {
    detectionLookup: context.gameDetectionLookup,
    detectionByName: context.gameDetectionByName,
    detectionByService: context.gameDetectionByService,
    gameDetectionData: context.gameDetectionData,
    isLoading: context.loading
  };
};

export const useSparklines = (): {
  sparklines: SparklineDataResponse | null;
  loading: boolean;
  isRefreshing: boolean;
} => {
  const context = useDashboardDataContext();
  return {
    sparklines: context.sparklines,
    loading: context.loading,
    isRefreshing: context.isRefreshing
  };
};

export const useHourlyActivity = (): {
  hourlyActivity: HourlyActivityResponse | null;
  loading: boolean;
  isRefreshing: boolean;
  error: string | null;
  refetch: () => Promise<void>;
} => {
  const context = useDashboardDataContext();
  return {
    hourlyActivity: context.hourlyActivity,
    loading: context.loading,
    isRefreshing: context.isRefreshing,
    error: context.error,
    refetch: () => context.refreshData(true)
  };
};

export const useCacheSnapshot = (): {
  cacheSnapshot: CacheSnapshotResponse | null;
  loading: boolean;
  isRefreshing: boolean;
} => {
  const context = useDashboardDataContext();
  return {
    cacheSnapshot: context.cacheSnapshot,
    loading: context.loading,
    isRefreshing: context.isRefreshing
  };
};

export const useCacheGrowth = (): {
  cacheGrowth: CacheGrowthResponse | null;
  loading: boolean;
  isRefreshing: boolean;
  error: string | null;
  refetch: () => Promise<void>;
} => {
  const context = useDashboardDataContext();
  return {
    cacheGrowth: context.cacheGrowth,
    loading: context.loading,
    isRefreshing: context.isRefreshing,
    error: context.error,
    refetch: () => context.refreshData(true)
  };
};
