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

// Event Types
export interface Event {
  id: number;
  name: string;
  description?: string;
  startTimeUtc: string;
  endTimeUtc: string;
  startTimeLocal: string;
  endTimeLocal: string;
  color: string;
  createdAtUtc: string;
  updatedAtUtc?: string;
}

export interface EventDownload {
  id: number;
  eventId: number;
  downloadId: number;
  taggedAtUtc: string;
  autoTagged: boolean;
}

export interface CreateEventRequest {
  name: string;
  description?: string;
  startTime: number; // Unix timestamp
  endTime: number; // Unix timestamp
  startTimeLocal?: string;
  endTimeLocal?: string;
  color?: string;
}

export interface UpdateEventRequest {
  name: string;
  description?: string;
  startTime: number; // Unix timestamp
  endTime: number; // Unix timestamp
  startTimeLocal?: string;
  endTimeLocal?: string;
  color?: string;
}

export type EventFilterMode = 'timeWindow' | 'tagged';
export type EventDataStackMode = 'eventOnly' | 'eventAndCurrent';

// Freeform Tag Types
export interface Tag {
  id: number;
  name: string;
  color: string;
  description?: string;
  createdAtUtc: string;
}

export interface DownloadTag {
  id: number;
  tagId: number;
  downloadId: number;
  taggedAtUtc: string;
}

export interface CreateTagRequest {
  name: string;
  color?: string;
  description?: string;
}

export interface UpdateTagRequest {
  name: string;
  color?: string;
  description?: string;
}

// Extended Download type with associations
export interface DownloadWithAssociations {
  download: Download;
  tags: TagSummary[];
  events: EventSummary[];
}

export interface TagSummary {
  id: number;
  name: string;
  color: string;
  description?: string;
}

export interface EventSummary {
  id: number;
  name: string;
  color: string;
  autoTagged: boolean;
}

// Real-time download speed types
export interface GameSpeedInfo {
  depotId: number;
  gameName?: string;
  gameAppId?: number;
  service: string;
  bytesPerSecond: number;
  totalBytes: number;
  requestCount: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
  cacheHitPercent: number;
}

export interface ClientSpeedInfo {
  clientIp: string;
  bytesPerSecond: number;
  totalBytes: number;
  activeGames: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
}

export interface DownloadSpeedSnapshot {
  timestampUtc: string;
  totalBytesPerSecond: number;
  gameSpeeds: GameSpeedInfo[];
  clientSpeeds: ClientSpeedInfo[];
  windowSeconds: number;
  entriesInWindow: number;
  hasActiveDownloads: boolean;
}

export interface NetworkBandwidthSnapshot {
  timestampUtc: string;
  interfaceName: string;
  downloadBytesPerSecond: number;
  uploadBytesPerSecond: number;
  totalBytesReceived: number;
  totalBytesSent: number;
  isAvailable: boolean;
  errorMessage?: string;
  /** Interface link speed in bits per second (e.g., 1 Gbps = 1000000000) */
  linkSpeedBps: number;
  /** Peak download speed observed this session (bytes per second) */
  peakDownloadBytesPerSecond: number;
  /** Peak upload speed observed this session (bytes per second) */
  peakUploadBytesPerSecond: number;
}

export interface CombinedSpeedSnapshot {
  networkBandwidth: NetworkBandwidthSnapshot;
  gameSpeeds: DownloadSpeedSnapshot;
}

// Historical speed types
export interface GameSpeedHistoryInfo {
  gameAppId?: number;
  gameName?: string;
  gameImageUrl?: string;
  service: string;
  totalBytes: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
  cacheHitPercent: number;
  averageBytesPerSecond: number;
  sessionCount: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
  totalDurationSeconds: number;
  uniqueClients: number;
}

export interface ClientSpeedHistoryInfo {
  clientIp: string;
  totalBytes: number;
  cacheHitBytes: number;
  cacheMissBytes: number;
  averageBytesPerSecond: number;
  gamesDownloaded: number;
  sessionCount: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface SpeedHistorySnapshot {
  periodStartUtc: string;
  periodEndUtc: string;
  periodMinutes: number;
  totalBytes: number;
  averageBytesPerSecond: number;
  gameSpeeds: GameSpeedHistoryInfo[];
  clientSpeeds: ClientSpeedHistoryInfo[];
  totalSessions: number;
}
