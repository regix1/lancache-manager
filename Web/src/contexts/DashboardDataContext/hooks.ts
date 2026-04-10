import { useContext } from 'react';
import { DashboardDataContext } from './types';
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

export const useStats = () => {
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useDashboardData must be used within DashboardDataProvider');
  }
  return {
    cacheInfo: context.cacheInfo,
    clientStats: context.clientStats,
    serviceStats: context.serviceStats,
    dashboardStats: context.dashboardStats,
    loading: context.loading,
    isRefreshing: context.isRefreshing,
    error: context.error,
    connectionStatus: context.connectionStatus,
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
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useDashboardData must be used within DashboardDataProvider');
  }
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
  detectionLookup: Map<number, GameCacheInfo> | null;
  detectionByName: Map<string, GameCacheInfo> | null;
  detectionByService: Map<
    string,
    { service_name: string; cache_files_found: number; total_size_bytes: number }
  > | null;
  gameDetectionData: {
    hasCachedResults: boolean;
    games?: GameCacheInfo[];
    services?: ServiceCacheInfo[];
    lastDetectionTime?: string;
  } | null;
  isLoading: boolean;
} => {
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useGameDetection must be used within DashboardDataProvider');
  }
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
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useSparklines must be used within DashboardDataProvider');
  }
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
} => {
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useHourlyActivity must be used within DashboardDataProvider');
  }
  return {
    hourlyActivity: context.hourlyActivity,
    loading: context.loading,
    isRefreshing: context.isRefreshing
  };
};

export const useCacheSnapshot = (): {
  cacheSnapshot: CacheSnapshotResponse | null;
  loading: boolean;
  isRefreshing: boolean;
} => {
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useCacheSnapshot must be used within DashboardDataProvider');
  }
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
} => {
  const context = useContext(DashboardDataContext);
  if (!context) {
    throw new Error('useCacheGrowth must be used within DashboardDataProvider');
  }
  return {
    cacheGrowth: context.cacheGrowth,
    loading: context.loading,
    isRefreshing: context.isRefreshing
  };
};
