// types.ts
import type { LucideIcon } from 'lucide-react';

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
  gameImageUrl?: string;
  depotId?: number;
  lastUrl?: string;
  displayName?: string;
  /** The datasource this download belongs to (for multi-datasource support). */
  datasource?: string;
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
  serviceBreakdown?: {
    service: string;
    bytes: number;
    percentage: number;
  }[];
  lastUpdated?: Date;
}

// Dashboard Analytics Types
export interface HourlyActivityItem {
  hour: number;
  downloads: number;
  avgDownloads: number;
  bytesServed: number;
  avgBytesServed: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
}

export interface HourlyActivityResponse {
  hours: HourlyActivityItem[];
  peakHour: number;
  totalDownloads: number;
  totalBytesServed: number;
  daysInPeriod: number;
  periodStart?: number;
  periodEnd?: number;
  period: string;
}

export interface CacheGrowthDataPoint {
  timestamp: string;
  cumulativeCacheMissBytes: number;
  growthFromPrevious: number;
}

export interface CacheGrowthResponse {
  dataPoints: CacheGrowthDataPoint[];
  currentCacheSize: number;
  totalCapacity: number;
  averageDailyGrowth: number;
  trend: 'up' | 'down' | 'stable';
  percentChange: number;
  estimatedDaysUntilFull: number | null;
  period: string;
}

export interface SparklineMetric {
  data: number[];
  trend: 'up' | 'down' | 'stable';
  percentChange: number;
}

export interface SparklineDataResponse {
  bandwidthSaved: SparklineMetric;
  cacheHitRatio: SparklineMetric;
  totalServed: SparklineMetric;
  addedToCache: SparklineMetric;
  period: string;
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
  totalLines?: number;
  currentPosition?: number;
  totalSize?: number;
}

export interface ClearCacheResponse {
  operationId: string;
  message: string;
}

export interface DatasourceInfo {
  name: string;
  cachePath: string;
  logsPath: string;
  cacheWritable: boolean;
  logsWritable: boolean;
  enabled: boolean;
}

export interface DatasourceLogPosition {
  datasource: string;
  position: number;
  totalLines: number;
  logPath: string;
  enabled: boolean;
}

export interface DatasourceServiceCounts {
  datasource: string;
  logsPath: string;
  logsWritable: boolean;
  enabled: boolean;
  serviceCounts: Record<string, number>;
}

export interface Config {
  cachePath: string;
  logsPath: string;
  dataPath: string;
  cacheDeleteMode: string;
  steamAuthMode: string;
  timeZone: string;
  cacheWritable: boolean;
  logsWritable: boolean;
  /** List of all configured datasources. Empty indicates single datasource mode. */
  dataSources: DatasourceInfo[];
}

export interface StatCardData {
  key: string;
  title: string;
  value: string | number;
  subtitle?: string;
  icon: LucideIcon;
  color: 'blue' | 'green' | 'emerald' | 'purple' | 'indigo' | 'orange' | 'yellow' | 'cyan';
  visible: boolean;
  tooltip?: string;
}

export interface CorruptedChunkDetail {
  service: string;
  url: string;
  miss_count: number;
  cache_file_path: string;
}

export interface GameCacheInfo {
  game_app_id: number;
  game_name: string;
  cache_files_found: number;
  total_size_bytes: number;
  depot_ids: number[];
  sample_urls: string[];
  cache_file_paths: string[];
  datasources: string[];
}

export interface ServiceCacheInfo {
  service_name: string;
  cache_files_found: number;
  total_size_bytes: number;
  sample_urls: string[];
  cache_file_paths: string[];
  datasources: string[];
}

export interface GameDetectionStatus {
  operationId: string;
  startTime: string;
  status: 'running' | 'complete' | 'failed';
  message?: string;
  games?: GameCacheInfo[];
  services?: ServiceCacheInfo[];
  totalGamesDetected?: number;
  totalServicesDetected?: number;
  error?: string;
}
