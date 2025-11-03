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
}

export interface StatsProviderProps {
  children: React.ReactNode;
  mockMode?: boolean;
}
