// types.ts
import type { LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import type { StructuralScanMode } from './types/corruptionScan';

export interface CacheInfo {
  totalCacheSize: number;
  configuredCacheSize: number;
  driveCapacity: number;
  usedCacheSize: number;
  freeCacheSize: number;
  usagePercent: number;
  totalFiles: number;
  serviceSizes: Record<string, number>;
  /** True after a scheduled or manual cache-file scan has produced a persisted result. */
  hasCacheScan: boolean;
  /** UTC timestamp of the cache file scan backing totalFiles. */
  cacheScanTimestampUtc?: string;
  /** Total bytes in the cache directory from the last cache file scan. */
  cacheScanTotalBytes?: number;
  /** True when mount usage has drifted significantly since the last cache file scan. */
  scanStale?: boolean;
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

/**
 * GET /api/cache/size returns this (202) instead of a CacheSizeInfo when no cached value
 * exists yet and a scan is already running elsewhere - a waiting state, not an error.
 */
export interface CacheSizeScanningInfo {
  scanning: true;
  operationId?: string;
}

/**
 * An ordinary cache-size read returns this when no scheduled or manual scan has produced a
 * persisted result yet. The UI should show its empty state without reporting an error.
 */
export interface CacheSizeUnavailableInfo {
  available: false;
}

/**
 * Accepted response for an explicit cache-size rescan. The operation either started immediately,
 * was parked in the wait queue, or was deduplicated against an existing scan.
 */
export interface CacheSizeScanStartInfo {
  operationId: string;
  queued: boolean;
  alreadyRunning: boolean;
  status: 'waiting' | 'started' | 'alreadyRunning';
}

/**
 * Mirrors the backend `QueuedOperationResponse` contract shared by every conflict-guarded
 * endpoint: whether the request started the work, found the identical operation already
 * running, or was parked behind a different one in the wait queue.
 */
export interface QueuedOperationResponse {
  operationId: string;
  queued: boolean;
  alreadyRunning: boolean;
  status: 'waiting' | 'started' | 'alreadyRunning' | 'skipped';
  /** Set only with `status: 'skipped'`: why the run was refused before it started. */
  skippedReason?: string;
}

export interface Download {
  id: number;
  service: string;
  clientIp: string;
  startTimeUtc: string;
  endTimeUtc: string | null;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
  cacheHitPercent: number;
  isActive: boolean;
  gameName?: string;
  gameAppId?: number;
  depotId?: number;
  epicAppId?: string;
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

export interface SparklineMetric {
  data: number[];
  trend: 'up' | 'down' | 'stable';
}

export interface EventCompareSeries {
  eventId: number;
  name: string;
  colorIndex: number;
  served: (number | null)[];
  saved: (number | null)[];
  missed: (number | null)[];
}

export interface EventCompareResponse {
  bucketMinutes: number;
  elapsedMinutes: number[];
  series: EventCompareSeries[];
}

export interface SparklineDataResponse {
  bandwidthSaved: SparklineMetric;
  cacheHitRatio: SparklineMetric;
  totalServed: SparklineMetric;
  addedToCache: SparklineMetric;
  period: string;
  bucketMinutes?: number;
  bucketStarts?: number[];
}

export interface CacheSnapshotResponse {
  hasData: boolean;
  startUsedSize: number;
  endUsedSize: number;
  averageUsedSize: number;
  totalCacheSize: number;
  snapshotCount: number;
  isEstimate: boolean;
  nextSnapshotUtc: string | null;
}

export type ClientExclusionMode = 'hide' | 'exclude';

export interface ClientExclusionRule {
  ip: string;
  mode: ClientExclusionMode;
}

export interface StatsExclusionsResponse {
  ips: string[];
  rules: ClientExclusionRule[];
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
  /** Wait-queue model: request was parked behind a conflicting op (purple card owns the UI). */
  queued?: boolean;
  /** Wait-queue model: identical op already active/queued - response carries its id. */
  alreadyRunning?: boolean;
}

export interface MessageResponse {
  message: string;
}

export type NginxReopenHint = 'grantSignalPrivilege' | 'enablePidHost' | 'mountDockerSocket';

export type DatasourceSchemeOverride = 'auto' | 'monolithic' | 'bare_metal';
export type CacheKeyScheme = 'monolithic' | 'bare_metal' | 'mixed' | 'unknown';

export interface DatasourceInfo {
  name: string;
  cachePath: string;
  logsPath: string;
  cacheWritable: boolean;
  logsWritable: boolean;
  enabled: boolean;
  /** Configured selection. Absent only on responses from older servers. */
  schemeOverride?: DatasourceSchemeOverride;
  /** Scheme currently selected after applying the override or log-layout inference. */
  cacheKeyScheme?: CacheKeyScheme;
  /** Backend explanation for a denied object-scoped disk capability. */
  capabilityDenialReason?: string | null;
  /** Presentation-only source layout. Absent on legacy responses (treat as monolithic). */
  layout?: 'monolithic' | 'bare_metal' | 'mixed';
  /** Number of logical access-log sources currently on disk. */
  sourceCount?: number;
  /** User-set cache-size limit override in bytes. Null/absent when unset (auto-detect). */
  cacheSizeOverrideBytes?: number | null;
  /** Effective cache-size limit shown as the dashboard total, after override or auto-detect. */
  resolvedCacheSizeBytes?: number;
  /** Where the resolved size came from: manual, docker, env, or fullDisk. */
  cacheSizeSource?: 'manual' | 'docker' | 'env' | 'fullDisk';
  /** Object-scoped disk features (game/service removal, corruption mapping, eviction) available. */
  canMapLogicalObjects?: boolean;
  /** Whole-root cache clear stays available everywhere. */
  canClearWholeCacheRoot?: boolean;
  /** Manager can reopen nginx after this datasource's access logs are rewritten. */
  nginxReopenAvailable?: boolean;
  /** Action needed to make nginx reopen available, or null when it is already available. */
  nginxReopenHint?: NginxReopenHint | null;
}

export interface DatasourceLogPosition {
  datasource: string;
  position: number;
  totalLines: number;
  logPath: string;
  enabled: boolean;
  layout?: 'monolithic' | 'bare_metal' | 'mixed';
  sourceCount?: number;
  /** Per-source read positions keyed by source stem. */
  sourcePositions?: Record<string, number>;
  /** Lines the parser could not recognize in the last run. */
  unparsedLines?: number;
  /** Lines that look like per-service logs but were found in access.log. */
  hintlessHttpDetailedLines?: number;
  invalidEncodingLines?: number;
  skippedFallbackLines?: number;
  incompleteFinalRecords?: number;
  filesWithErrors?: string[];
  lastRunTerminalStatus?:
    | ''
    | 'completed'
    | 'completed_with_warnings'
    | 'partial'
    | 'failed'
    | 'cancelled';
  /** Human-readable message set while the log directory has no access-log sources at all. */
  missingSourcesMessage?: string | null;
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
  /** Warning chrome (yellow outline + tint) for an outdated scan. */
  tone?: 'warning';
  icon: LucideIcon;
  color: 'blue' | 'green' | 'emerald' | 'purple' | 'indigo' | 'orange' | 'yellow' | 'cyan' | 'teal';
  visible: boolean;
  tooltip?: ReactNode;
  /** Control riding in the footer beside the note, for a card whose value can show more than one figure. */
  footerControl?: ReactNode;
}

type CorruptionSliceKind = 'no_range' | 'noslice' | 'ranged';

export type CorruptionDetectionMethod = 'repeated_miss' | 'structural';

export interface CorruptionObservedRange {
  kind: 'no_range' | 'inclusive';
  start?: number | null;
  end?: number | null;
}

export interface CorruptionCacheSlice {
  kind: CorruptionSliceKind;
  start?: number | null;
  end?: number | null;
}

interface CorruptionCandidateObservation {
  rawUrl: string;
  timestamp: string;
  clientIp: string;
  method: string;
  httpStatus: number;
  cacheStatus: string;
  rawRange?: string | null;
  bytesServed: number;
}

export interface RepeatedMissCorruptionEvidence {
  kind: 'repeated_miss';
  rawUrl: string;
  normalizedUri: string;
  observedRange: CorruptionObservedRange;
  cacheSlice: CorruptionCacheSlice;
  evidenceCount: number;
  firstSeen: string;
  lastSeen: string;
  observations: CorruptionCandidateObservation[];
}

export type StructuralCorruptionIssue =
  | 'empty_cache_file'
  | 'truncated_cache_header'
  | 'malformed_cache_header'
  | 'invalid_payload_offset'
  | 'truncated_before_payload'
  | 'cache_key_path_mismatch'
  | 'payload_length_mismatch'
  | 'content_range_length_mismatch'
  | 'content_length_range_conflict';

interface StructuralFileFingerprint {
  dev: number;
  ino: number;
  len: number;
  mtimeNs: number;
  ctimeNs: number;
}

export interface StructuralCorruptionEvidence {
  kind: 'structural';
  issues: StructuralCorruptionIssue[];
  cacheKeyEncoding: string;
  cacheKey: string;
  cacheKeyMd5: string;
  cacheVersion: number;
  httpStatus?: number | null;
  headerStart?: number | null;
  bodyStart?: number | null;
  fileLength: number;
  actualPayloadLength?: number | null;
  expectedPayloadLength?: number | null;
  contentLength?: number | null;
  contentRange?: string | null;
  fingerprint: StructuralFileFingerprint;
  detectedAtUtc: string;
}

/**
 * One physical-file candidate from a strict contract-v4 saved scan. Whether it is
 * actionable is decided by the scan-level server gate (only the explicit current
 * scan can remove files), not by this evidence DTO — history detail views render
 * the same shape read-only.
 */
export interface CorruptedChunkDetail {
  candidateId: string;
  datasource: string;
  service: string;
  exactPaths: [string];
  evidence: RepeatedMissCorruptionEvidence | StructuralCorruptionEvidence;
}

export interface CorruptionScanCoverage {
  filesSeen: number;
  filesChecked: number;
  consistent: number;
  skippedByReason: Record<string, number>;
  ioErrors: number;
  bytesRead: number;
  sparseFiles: number;
}

export interface CorruptionDetectionSettings {
  threshold?: number | null;
  lookbackDays?: number | null;
  minStableAgeSeconds?: number | null;
  maxPrefixBytes?: number | null;
}

export interface CachedCorruptionDetectionResponse {
  hasCachedResults: boolean;
  scanId?: string;
  detectionMethod?: CorruptionDetectionMethod;
  settings?: CorruptionDetectionSettings;
  threshold?: number | null;
  contractVersion?: number;
  lookbackDays?: number | null;
  corruptionCounts?: Record<string, number>;
  detectionCounts?: Record<string, number>;
  coverage?: CorruptionScanCoverage;
  totalServicesWithCorruption?: number;
  totalCorruptedChunks?: number;
  lastDetectionTime?: string;
}

/**
 * One retained saved-scan snapshot summary from the corruption scan history.
 * `isCurrent` is explicit backend state; never infer currentness from array
 * order or timestamps. `scanMode` is only present when the Structural scan
 * truthfully persisted it — legacy snapshots have an unknown (null) mode.
 */
export interface CorruptionScanHistoryEntry {
  scanId: string;
  detectionMethod: CorruptionDetectionMethod;
  isCurrent: boolean;
  completedAtUtc: string;
  settings: CorruptionDetectionSettings;
  contractVersion: number;
  corruptionCounts: Record<string, number>;
  detectionCounts: Record<string, number>;
  coverage?: CorruptionScanCoverage | null;
  totalServicesWithCorruption: number;
  totalCorruptedChunks: number;
  scanMode?: StructuralScanMode | null;
}

export interface CorruptionScanHistoryResponse {
  scans: CorruptionScanHistoryEntry[];
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
  service?: string; // "steam" (default), "epicgames", "blizzard", or "riot" — blizzard/riot games have game_app_id 0
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

// Cache files the detection walk found on disk that no game and no service claimed.
// Absent from an incremental detection response, which has no cache index to measure against.
export interface UnmappedService {
  service: string;
  file_count: number;
  total_bytes: number;
  sample_urls: string[];
}

// Event Types
export interface Event {
  id: number;
  name: string;
  description?: string;
  startTimeUtc: string;
  endTimeUtc: string;
  colorIndex: number; // 1-8, references theme event colors
  createdAtUtc: string;
  updatedAtUtc?: string;
}

export interface CreateEventRequest {
  name: string;
  description?: string;
  startTime: number; // Unix timestamp
  endTime: number; // Unix timestamp
  colorIndex?: number; // 1-8, references theme event colors
}

export interface UpdateEventRequest {
  name: string;
  description?: string;
  startTime: number; // Unix timestamp
  endTime: number; // Unix timestamp
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
  /** True when the client stats surfaces show one row per member IP instead of one summed row. */
  separateMemberRows: boolean;
  createdAtUtc: string;
  updatedAtUtc?: string;
  memberIps: string[];
}

export interface CreateClientGroupRequest {
  nickname: string;
  description?: string;
  initialIps?: string[];
  separateMemberRows?: boolean;
}

export interface UpdateClientGroupRequest {
  nickname: string;
  description?: string;
  /**
   * Required because the update endpoint fully replaces the group: an omitted field
   * arrives as `false` server-side and would silently reset a group back to one summed row.
   */
  separateMemberRows: boolean;
  /**
   * The copy the editor started from. This is the first write an edit session makes and it moves
   * the group's stamp, so a nickname someone else changed since is refused here rather than
   * overwritten. Null asks for no precondition.
   */
  expectedUpdatedAtUtc?: string | null;
}

/**
 * Reply to a whole-membership save. `rejectedIps` names the addresses another nickname
 * already owns; it is always present and empty when everything applied.
 */
export interface SetMembersResponse {
  group: ClientGroup;
  rejectedIps: string[];
}

/**
 * The server refused a save because the nickname moved on after the editor loaded it. Nothing was
 * written; `currentGroup` is the nickname as it now stands, so the editor can re-seed from it
 * without a second round trip.
 */
export interface ClientGroupConflict {
  error: string;
  currentGroup: ClientGroup;
}

/**
 * Outcome of a whole-membership save. `saved` carries the new membership plus any addresses
 * another nickname already owns; `stale` means the save was refused and nothing changed.
 */
export type SetMembersResult =
  | ({ status: 'saved' } & SetMembersResponse)
  | ({ status: 'stale' } & ClientGroupConflict);

/**
 * Outcome of saving a nickname's fields. `saved` carries the group as written, including the stamp
 * the write moved it to; `stale` means the save was refused and nothing changed.
 */
export type UpdateClientGroupResult =
  | ({ status: 'saved' } & ClientGroup)
  | ({ status: 'stale' } & ClientGroupConflict);

/**
 * Reply to creating a nickname: every group field at the top level plus the addresses
 * from `initialIps` the new group does not hold.
 */
export interface CreateClientGroupResponse extends ClientGroup {
  rejectedIps: string[];
}

/**
 * Outcome of creating a nickname. `created` carries the new nickname plus any addresses it could
 * not take; `rejected` means every requested address was already held elsewhere, so the server
 * removed the half-made nickname and there is nothing to edit.
 */
export type CreateClientGroupResult =
  | ({ status: 'created' } & CreateClientGroupResponse)
  | { status: 'rejected'; error: string; rejectedIps: string[] };

// Real-time download speed types
export interface GameSpeedInfo {
  depotId: number;
  gameName?: string;
  gameAppId?: number;
  service: string;
  clientIp?: string;
  bytesPerSecond: number;
  /**
   * Bytes observed in the tracker's current rolling window (windowSeconds) only. NOT a
   * session or transfer total, and consecutive windows overlap, so values must never be
   * accumulated across snapshots or shown as a download size.
   */
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

/**
 * Whether a cache scan would be refused right now. Answered by the same gate the server uses to
 * refuse one, and read from the UNFILTERED download set: hiding a client takes them off the
 * dashboard, it does not stop their bytes reaching the cache.
 */
export interface CacheScanBlockedResponse {
  blocked: boolean;
  reason: string | null;
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

/**
 * Xbox / Microsoft Store game mapping. Keyed by Microsoft Store ProductId + resolved title;
 * unlike Epic there is no per-mapping discovery `source` (resolution is the Rust ingest pass).
 */
export interface XboxGameMappingDto {
  productId: string;
  title: string;
  discoveredAtUtc: string;
  lastSeenAtUtc: string;
  imageUrl?: string;
}

export interface XboxMappingStats {
  totalGames: number;
  lastUpdatedUtc: string | null;
}

/**
 * Daemon connectivity status. Every service's `/status` route is the same inherited handler on the
 * shared daemon controller base, so Epic, Battle.net, Riot and Xbox return this one shape by
 * construction rather than by coincidence.
 */
export interface DaemonStatusDto {
  dockerAvailable: boolean;
  activeSessions: number;
  /**
   * Count of active daemon sessions that have authenticated. Login-required cards (Xbox) use this
   * to reflect a logged-in state and hide/swap their login control. Optional: older daemon
   * responses (and the daemon-free Epic OAuth path) omit it.
   */
  authenticatedSessions?: number;
  maxSessionsPerUser: number;
  sessionTimeoutMinutes: number;
}

export interface EpicMappingAuthStatus {
  isAuthenticated: boolean;
  displayName: string | null;
  lastCollectionUtc: string | null;
  gamesDiscovered: number;
}

export interface XboxMappingAuthStatus {
  isAuthenticated: boolean;
  displayName: string | null;
  lastCollectionUtc: string | null;
  gamesDiscovered: number;
  /** True while a device-code login attempt is still alive, covering the approval wait and the
   *  catalog harvest after it. False alongside `isAuthenticated: false` is the only pair that means
   *  the attempt is over and did not succeed. */
  loginInProgress: boolean;
  expiresAtUtc: string | null;
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
