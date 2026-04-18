// types.ts
import type { LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';

export interface CacheInfo {
  totalCacheSize: number;
  configuredCacheSize: number;
  driveCapacity: number;
  usedCacheSize: number;
  freeCacheSize: number;
  usagePercent: number;
  totalFiles: number;
  serviceSizes: Record<string, number>;
}

export interface CacheSizeInfo {
  totalBytes: number;
  totalFiles: number;
  totalDirectories: number;
  hexDirectories: number;
  scanDurationMs: number;
  formattedSize: string;
  timestamp: string;
  isCached: boolean;
  estimatedDeletionTimes: {
    preserveSeconds: number;
    fullSeconds: number;
    rsyncSeconds: number;
    preserveFormatted: string;
    fullFormatted: string;
    rsyncFormatted: string;
  };
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
  epicAppId?: string;
  lastUrl?: string;
  displayName?: string;
  /** The datasource this download belongs to (for multi-datasource support). */
  datasource?: string;
  /** Duration in seconds calculated from LogEntries (more accurate than EndTime - StartTime). */
  durationSeconds?: number;
  /** Average download speed in bytes per second, calculated from total bytes and duration. */
  averageBytesPerSecond: number;
  /** Whether this download's cache files have been evicted from the lancache. */
  isEvicted: boolean;
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
  /** Average download speed in bytes per second across all sessions. */
  averageBytesPerSecond?: number;
  // Client group fields
  displayName?: string; // Nickname if grouped, undefined if not
  groupId?: number;
  isGrouped: boolean; // true if this client is part of a group
  groupMemberIps?: string[];
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
  /** Net average daily growth accounting for deletions (can be negative) */
  netAverageDailyGrowth: number;
  trend: 'up' | 'down' | 'stable';
  percentChange: number;
  estimatedDaysUntilFull: number | null;
  period: string;
  /** True if actual cache size < cumulative downloads (data was deleted) */
  hasDataDeletion: boolean;
  /** Estimated bytes that were deleted from cache */
  estimatedBytesDeleted: number;
  /** True if cache was essentially cleared (very small relative to historical downloads) */
  cacheWasCleared?: boolean;
}

export interface SparklineMetric {
  data: number[];
  trend: 'up' | 'down' | 'stable';
}

export interface SparklineDataResponse {
  bandwidthSaved: SparklineMetric;
  cacheHitRatio: SparklineMetric;
  totalServed: SparklineMetric;
  addedToCache: SparklineMetric;
  period: string;
}

export interface CacheSnapshotResponse {
  hasData: boolean;
  startUsedSize: number;
  endUsedSize: number;
  averageUsedSize: number;
  totalCacheSize: number;
  snapshotCount: number;
  isEstimate: boolean;
}

export interface StatsExclusionsResponse {
  ips: string[];
}

export interface ProcessingStatus {
  isProcessing: boolean;
  operationId?: string;
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

export interface MessageResponse {
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
  badge?: ReactNode;
  icon: LucideIcon;
  color: 'blue' | 'green' | 'emerald' | 'purple' | 'indigo' | 'orange' | 'yellow' | 'cyan';
  visible: boolean;
  tooltip?: ReactNode;
}

export interface CorruptedChunkDetail {
  service: string;
  url: string;
  miss_count: number;
  cache_file_path: string;
}

export type CacheEntityVariant = 'active' | 'evicted';

/**
 * Slim projection of GameCacheInfo returned by the dashboard batch endpoint
 * (/api/dashboard/batch → detection.games). Drops heavy list fields
 * (cache_file_paths, sample_urls, depot_ids, datasources, evicted_sample_urls,
 * evicted_depot_ids) that only the Management tab consumes via
 * /api/games/cached-detection. Field names match the backend JsonPropertyName
 * snake_case serialization. Must stay in sync with DashboardGameSummary.cs.
 */
export interface GameDetectionSummary {
  game_app_id: number;
  game_name: string;
  cache_files_found: number;
  total_size_bytes: number;
  service?: string;
  image_url?: string;
  epic_app_id?: string;
  is_evicted?: boolean;
  evicted_downloads_count?: number;
}

/**
 * Slim projection of ServiceCacheInfo returned by the dashboard batch endpoint.
 * Drops cache_file_paths, sample_urls, datasources, evicted_sample_urls,
 * evicted_bytes which are only consumed by the Management tab.
 */
export interface ServiceDetectionSummary {
  service_name: string;
  cache_files_found: number;
  total_size_bytes: number;
  is_evicted?: boolean;
  evicted_downloads_count?: number;
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
  service?: string; // "steam" (default) or "epicgames"
  image_url?: string; // Game art URL (Steam header or Epic keyImages)
  epic_app_id?: string; // Epic Games catalog item ID for image proxy
  is_evicted?: boolean; // True if all cache files have been evicted (no longer on disk)
  evicted_downloads_count?: number;
  evicted_bytes?: number;
  evicted_sample_urls?: string[];
  evicted_depot_ids?: number[];
}

export interface ServiceCacheInfo {
  service_name: string;
  cache_files_found: number;
  total_size_bytes: number;
  sample_urls: string[];
  cache_file_paths: string[];
  datasources: string[];
  is_evicted?: boolean;
  evicted_downloads_count?: number;
  evicted_bytes?: number;
  evicted_sample_urls?: string[];
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
  colorIndex: number; // 1-8, references theme event colors
  createdAtUtc: string;
  updatedAtUtc?: string;
}

export interface CreateEventRequest {
  name: string;
  description?: string;
  startTime: number; // Unix timestamp
  endTime: number; // Unix timestamp
  startTimeLocal?: string;
  endTimeLocal?: string;
  colorIndex?: number; // 1-8, references theme event colors
}

export interface UpdateEventRequest {
  name: string;
  description?: string;
  startTime: number; // Unix timestamp
  endTime: number; // Unix timestamp
  startTimeLocal?: string;
  endTimeLocal?: string;
  colorIndex?: number; // 1-8, references theme event colors
}

export type EventFilterMode = 'timeWindow' | 'tagged';
export type EventDataStackMode = 'eventOnly' | 'eventAndCurrent';

export interface EventSummary {
  id: number;
  name: string;
  colorIndex: number; // 1-8, references theme event colors
  autoTagged: boolean;
}

// Client Group Types
export interface ClientGroup {
  id: number;
  nickname: string;
  description?: string;
  createdAtUtc: string;
  updatedAtUtc?: string;
  memberIps: string[];
}

export interface CreateClientGroupRequest {
  nickname: string;
  description?: string;
  initialIps?: string[];
}

export interface UpdateClientGroupRequest {
  nickname: string;
  description?: string;
}

// Real-time download speed types
export interface GameSpeedInfo {
  depotId: number;
  gameName?: string;
  gameAppId?: number;
  service: string;
  clientIp?: string;
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

export interface SpeedHistorySnapshot {
  periodStartUtc: string;
  periodEndUtc: string;
  periodMinutes: number;
  totalBytes: number;
  averageBytesPerSecond: number;
  totalSessions: number;
}

// Epic Game Mappings
export interface EpicGameMappingDto {
  appId: string;
  name: string;
  discoveredAtUtc: string;
  lastSeenAtUtc: string;
  source: string;
  imageUrl?: string;
}

export interface EpicMappingStats {
  totalGames: number;
  lastUpdatedUtc: string | null;
  oldestGameUtc: string | null;
  distinctSources: number;
  cdnPatterns: number;
}

export interface EpicDaemonStatusDto {
  dockerAvailable: boolean;
  activeSessions: number;
  maxSessionsPerUser: number;
  sessionTimeoutMinutes: number;
}

export interface EpicMappingAuthStatus {
  isAuthenticated: boolean;
  displayName: string | null;
  lastCollectionUtc: string | null;
  gamesDiscovered: number;
}

export interface EpicScheduleStatus {
  refreshIntervalHours: number;
  isProcessing: boolean;
  lastRefreshTime: string | null;
  nextRefreshIn: number;
  isAuthenticated: boolean;
  operationId: string | null;
  status: string;
  progressPercent: number;
}

/** PICS data status returned by the /depots/status API endpoint */
export interface PicsStatus {
  isScanning: boolean;
  scanProgress?: number;
  totalDepots?: number;
  lastScanTime?: string;
  nextScanIn?: number | string | { totalSeconds?: number; totalHours?: number };
  jsonFile?: { exists: boolean; totalMappings?: number };
  database?: { totalMappings?: number };
  steamKit2?: { isReady: boolean; isRebuildRunning?: boolean };
  rebuildInProgress?: boolean;
}
