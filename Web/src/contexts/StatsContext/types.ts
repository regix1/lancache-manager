import type { CacheInfo, ClientStat, ServiceStat, DashboardStats } from '../../types';

export interface StatsContextType {
  cacheInfo: CacheInfo | null;
  clientStats: ClientStat[];
  serviceStats: ServiceStat[];
  dashboardStats: DashboardStats | null;
  loading: boolean;
  error: string | null;
  connectionStatus: string;
  refreshStats: () => Promise<void>;
  updateStats: (updater: {
    cacheInfo?: (prev: CacheInfo | null) => CacheInfo | null;
    clientStats?: (prev: ClientStat[]) => ClientStat[];
    serviceStats?: (prev: ServiceStat[]) => ServiceStat[];
    dashboardStats?: (prev: DashboardStats | null) => DashboardStats | null;
  }) => void;
}

export interface StatsProviderProps {
  children: React.ReactNode;
  mockMode?: boolean;
}
