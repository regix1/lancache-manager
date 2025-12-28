import type { CacheInfo, ClientStatWithGroup, ServiceStat, DashboardStats } from '../../types';

export interface StatsContextType {
  cacheInfo: CacheInfo | null;
  clientStats: ClientStatWithGroup[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;
  loading: boolean;
  error: string | null;
  connectionStatus: string;
  refreshStats: () => Promise<void>;
  updateStats: (updater: {
    cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
    clientStats?: (prev: ClientStatWithGroup[]) => ClientStatWithGroup[];
    serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
    dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
  }) => void;
}

export interface StatsProviderProps {
  children: React.ReactNode;
  mockMode?: boolean;
}
