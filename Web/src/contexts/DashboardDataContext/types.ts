import { createContext, type ReactNode } from 'react';
import type {
  CacheInfo,
  ClientStat,
  ServiceStat,
  DashboardStats,
  Download,
  GameCacheInfo
} from '../../types';

export interface CachedDetectionResponse {
  hasCachedResults: boolean;
  games?: GameCacheInfo[];
  services?: { service_name: string; cache_files_found: number; total_size_bytes: number }[];
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

  // Loading & Error states
  loading: boolean;
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

export interface DashboardCacheEnvelope<T> {
  data: T;
  cachedAt: number;
  version: string;
}

export interface DashboardDataProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

export const DashboardDataContext = createContext<DashboardDataContextType | undefined>(undefined);
