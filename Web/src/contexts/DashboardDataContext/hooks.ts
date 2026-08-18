import { useMemo } from 'react';
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
  CacheSnapshotResponse
} from '../../types';

// Every selector below reads the same provider, so they share one guard instead of restating it.
const useDashboardDataContext = createContextHook(DashboardDataContext, 'useDashboardData');

interface StatsUpdater {
  cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
  clientStats?: (prev: ClientStat[]) => ClientStat[];
  serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
  dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
}

export const useStats = (): {
  cacheInfo: CacheInfo | null;
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;
  loading: boolean;
  isRefreshing: boolean;
  error: string | null;
  connectionStatus: string;
  dataStale: boolean;
  failedSections: { cache: boolean; clients: boolean; services: boolean; dashboard: boolean };
  refreshStats: (forceRefresh?: boolean) => Promise<void>;
  updateStats: (updater: StatsUpdater) => void;
} => {
  const context = useDashboardDataContext();
  const { failedSectionKeys } = context;
  // Per section, not one flag: each card renders its own, so a failed cache sub-query must not
  // make the clients card claim it failed too. Memoised so a consumer can put it in a dep array.
  const failedSections = useMemo(
    () => ({
      cache: failedSectionKeys.includes('cache'),
      clients: failedSectionKeys.includes('clients'),
      services: failedSectionKeys.includes('services'),
      dashboard: failedSectionKeys.includes('dashboard')
    }),
    [failedSectionKeys]
  );
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
    failedSections,
    refreshStats: context.refreshData,
    updateStats: (updater: StatsUpdater) => context.updateData(updater)
  };
};

export const useDownloads = (): {
  latestDownloads: Download[];
  loading: boolean;
  error: string | null;
  failed: boolean;
  refreshDownloads: () => Promise<void>;
  updateDownloads: (updater: { latestDownloads?: (prev: Download[]) => Download[] }) => void;
} => {
  const context = useDashboardDataContext();
  return {
    latestDownloads: context.latestDownloads,
    loading: context.loading,
    error: context.error,
    failed: context.failedSectionKeys.includes('downloads'),
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
  failed: boolean;
} => {
  const context = useDashboardDataContext();
  return {
    detectionLookup: context.gameDetectionLookup,
    detectionByName: context.gameDetectionByName,
    detectionByService: context.gameDetectionByService,
    gameDetectionData: context.gameDetectionData,
    isLoading: context.loading,
    failed: context.failedSectionKeys.includes('detection')
  };
};

export const useSparklines = (): {
  sparklines: SparklineDataResponse | null;
  loading: boolean;
  isRefreshing: boolean;
  failed: boolean;
} => {
  const context = useDashboardDataContext();
  return {
    sparklines: context.sparklines,
    loading: context.loading,
    isRefreshing: context.isRefreshing,
    failed: context.failedSectionKeys.includes('sparklines')
  };
};

export const useHourlyActivity = (): {
  hourlyActivity: HourlyActivityResponse | null;
  loading: boolean;
  isRefreshing: boolean;
  error: string | null;
  failed: boolean;
  refetch: () => Promise<void>;
} => {
  const context = useDashboardDataContext();
  return {
    hourlyActivity: context.hourlyActivity,
    loading: context.loading,
    isRefreshing: context.isRefreshing,
    error: context.error,
    failed: context.failedSectionKeys.includes('hourlyActivity'),
    refetch: () => context.refreshData(true)
  };
};

export const useCacheSnapshot = (): {
  cacheSnapshot: CacheSnapshotResponse | null;
  loading: boolean;
  isRefreshing: boolean;
  error: string | null;
  failed: boolean;
  refetch: () => Promise<void>;
} => {
  const context = useDashboardDataContext();
  return {
    cacheSnapshot: context.cacheSnapshot,
    loading: context.loading,
    isRefreshing: context.isRefreshing,
    error: context.error,
    failed: context.failedSectionKeys.includes('cacheSnapshot'),
    refetch: () => context.refreshData(true)
  };
};
