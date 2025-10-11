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
  startTimeUtc: string;
  endTimeUtc: string | null;
  startTimeLocal: string;
  endTimeLocal: string | null;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  isActive: boolean;
  gameName?: string;
  gameAppId?: number;
  depotId?: number;
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
  startTimeUtc?: string;
  endTimeUtc?: string | null;
  startTimeLocal?: string;
  endTimeLocal?: string | null;
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
  totalBytes: number; // Total bytes downloaded across all sessions
  totalDownloaded: number; // Total bytes downloaded across all sessions (same as totalBytes)
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
  lastActivityUtc: string;
  lastActivityLocal: string;
}

export interface ServiceStat {
  service: string;
  totalCacheHitBytes: number;
  totalCacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  totalDownloads: number;
  lastActivityUtc: string;
  lastActivityLocal: string;
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
    since?: Date | null;
    bandwidthSaved: number;
    addedToCache: number;
    totalServed: number;
    hitRatio: number;
    downloads: number;
  };
  serviceBreakdown?: Array<{
    service: string;
    bytes: number;
    percentage: number;
  }>;
  lastUpdated?: Date;
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
  filesDeleted?: number;
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
  entriesQueued?: number;
  pendingEntries?: number;
  linesProcessed?: number;
  currentPosition?: number;
  totalSize?: number;
}

export interface ClearCacheResponse {
  operationId: string;
  message: string;
}

export interface Config {
  cachePath: string;
  logPath: string;
  services: string[];
  timezone: string;
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
