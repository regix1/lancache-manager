import type { ReactNode } from 'react';
import type { CacheInfo, ClientStat, ServiceStat, DashboardStats, Download } from '../../types';

export interface DashboardDataContextType {
  // Cache info
  cacheInfo: CacheInfo | null;

  // Stats
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;

  // Downloads
  latestDownloads: Download[];

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

export interface DashboardDataProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}
