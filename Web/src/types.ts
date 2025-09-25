// types.ts
export interface CacheInfo {
  totalCacheSize: number;
  usedCacheSize: number;
  freeCacheSize: number;
  usagePercent: number;
  totalFiles: number;
  serviceSizes: Record<string, number>;
}

export interface Download {
  id: number;
  service: string;
  clientIp: string;
  startTime: string;
  endTime: string | null;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  isActive: boolean;
  gameName?: string;
  gameAppId?: number;
}

export interface GameInfo {
  downloadId: number;
  service: string;
  appId?: number;
  gameName?: string;
  gameType?: string;
  headerImage?: string;
  description?: string;
  totalBytes?: number;
  cacheHitBytes?: number;
  cacheMissBytes?: number;
  cacheHitPercent?: number;
  startTime?: string;
  endTime?: string | null;
  clientIp?: string;
  isActive?: boolean;
  error?: string;
}

export interface DownloadSettings {
  showZeroBytes: boolean;
  showSmallFiles: boolean;
  hideLocalhost: boolean;
  selectedService: string;
  itemsPerPage?: number | 'all';
  groupGames: boolean;
  viewMode: 'compact' | 'normal';
  sortOrder: 'latest' | 'oldest' | 'largest' | 'smallest' | 'service';
}

export interface DownloadGroup {
  id: string;
  name: string;
  type: 'game' | 'metadata' | 'content';
  service: string;
  downloads: Download[];
  totalBytes: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
  clientsSet: Set<string>;
  clientCount?: number;
  firstSeen: string;
  lastSeen: string;
  count: number;
}

export interface DownloadSettings {
  showZeroBytes: boolean;
  showSmallFiles: boolean;
  hideLocalhost: boolean;
  selectedService: string;
  itemsPerPage?: number | 'all';
  groupGames: boolean;
}

export interface DownloadType {
  type: 'game' | 'metadata' | 'content';
  label: string;
  icon: React.ComponentType<any>;
}

export interface ClientStat {
  clientIp: string;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  totalDownloads: number;
  downloadCount?: number;
  lastSeen: string | null;
}

export interface ServiceStat {
  service: string;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  totalDownloads: number;
  lastActivity: string;
}

export interface DashboardStats {
  totalBandwidthSaved: number;
  totalAddedToCache: number;
  totalServed: number;
  cacheHitRatio: number;
  activeDownloads: number;
  uniqueClients: number;
  topService: string;
  period: {
    duration: string;
    bandwidthSaved: number;
    addedToCache: number;
    totalServed: number;
    hitRatio: number;
    downloads: number;
  };
}

// API Response Types - matching api.service.ts
export interface CacheClearStatus {
  operationId: string;
  status: string;
  progress?: number;
  message?: string;
  statusMessage?: string;
  percentComplete?: number;
  bytesDeleted?: number;
  totalBytesToDelete?: number;
  error?: string;
}

export interface ProcessingStatus {
  isProcessing: boolean;
  progress?: number;
  message?: string;
  estimatedTime?: string;
  percentComplete?: number;
  mbProcessed?: number;
  mbTotal?: number;
  processingRate?: number;
  status?: string;
  entriesProcessed?: number;
  linesProcessed?: number;
}

export interface ClearCacheResponse {
  operationId: string;
  message: string;
}

export interface Config {
  cachePath: string;
  logPath: string;
  services: string[];
}

export type CardKey =
  | 'totalCache'
  | 'usedSpace'
  | 'bandwidthSaved'
  | 'addedToCache'
  | 'totalServed'
  | 'activeDownloads'
  | 'activeClients'
  | 'cacheHitRatio';

export interface StatCardData {
  key: string;
  title: string;
  value: string | number;
  subtitle?: string;
  icon: any;
  color: 'blue' | 'green' | 'emerald' | 'purple' | 'indigo' | 'orange' | 'yellow' | 'cyan';
  visible: boolean;
  tooltip?: string;
}

export interface TimeRange {
  label: string;
  value: string;
}

export interface ApiResponse<T> {
  data: T;
  error?: string;
  status: number;
}
