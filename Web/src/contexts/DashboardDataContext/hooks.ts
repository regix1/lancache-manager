import { useContext } from 'react';
import { DashboardDataContext } from './types';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  GameCacheInfo
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
