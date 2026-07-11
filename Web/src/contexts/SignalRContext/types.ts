import type { ReactNode } from 'react';
import type { OperationStatus, NotificationVariant } from '../../types/operations';
import type { SessionType } from '../../services/auth.service';

// Event handler type for SignalR events
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type EventHandler = (...args: any[]) => void | Promise<void>;

export interface SignalRContextType {
  // Connection status
  isConnected: boolean;
  connectionState: 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

  // Subscribe/unsubscribe to events
  on: (eventName: string, handler: EventHandler) => void;
  off: (eventName: string, handler: EventHandler) => void;

  // Invoke hub methods
  invoke: (methodName: string, ...args: unknown[]) => Promise<void>;

  // Connection info
  connectionId: string | null;
}

export interface SignalRProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

// List of all SignalR events
// Keep in sync with Api/LancacheManager/Hubs/SignalREvents.cs
export const SIGNALR_EVENTS = [
  // Downloads
  'DownloadsRefresh',
  'DownloadSpeedUpdate',

  // Log Processing
  'LogProcessingStarted',
  'LogProcessingProgress',
  'LogProcessingComplete',
  'LogRemovalStarted',
  'LogRemovalProgress',
  'LogRemovalComplete',
  'ServiceCountsChanged',

  // Database Operations
  'DatabaseResetStarted',
  'DatabaseResetProgress',
  'DatabaseResetComplete',

  // Directory Permissions
  'DirectoryPermissionsChanged',

  // SteamKit2 / Depot Mapping
  'DepotMappingStarted',
  'DepotMappingProgress',
  'DepotMappingComplete',
  'SteamSessionError',
  'SteamAutoLogout',
  'AutomaticScanSkipped',

  // Cache Operations
  'CacheClearingStarted',
  'CacheClearingProgress',
  'CacheClearingComplete',
  'CacheScanComplete',
  'ServiceRemovalStarted',
  'ServiceRemovalProgress',
  'ServiceRemovalComplete',
  'CorruptionDetectionStarted',
  'CorruptionDetectionProgress',
  'CorruptionDetectionComplete',
  'CorruptionDetailsProgress',
  'CorruptionRemovalStarted',
  'CorruptionRemovalProgress',
  'CorruptionRemovalComplete',
  'EvictionRemovalComplete',

  // Games
  'GameDetectionStarted',
  'GameDetectionProgress',
  'GameDetectionComplete',
  'GameRemovalStarted',
  'GameRemovalProgress',
  'GameRemovalComplete',

  // Data Import
  'DataImportStarted',
  'DataImportProgress',
  'DataImportComplete',

  // Client Groups
  'ClientGroupCreated',
  'ClientGroupUpdated',
  'ClientGroupDeleted',
  'ClientGroupMemberAdded',
  'ClientGroupMemberRemoved',
  'ClientGroupsCleared',

  // Events
  'EventCreated',
  'EventUpdated',
  'EventDeleted',
  'EventsCleared',
  'DownloadTagged',

  // Sessions
  'UserSessionCreated',
  'UserSessionRevoked',
  'UserSessionDeleted',
  'UserSessionsCleared',
  'SessionLastSeenUpdated',
  'GuestRefreshRateUpdated',

  // User Preferences
  'UserPreferencesUpdated',
  'UserPreferencesReset',

  // System / Config
  'DefaultGuestRefreshRateChanged',
  'AllowedTimeFormatsChanged',
  'DefaultGuestPreferencesChanged',
  'DefaultGuestThemeChanged',

  // Auth / Guest Mode
  'GuestModeLockChanged',
  'GuestRefreshRateLockChanged',
  'GuestDurationUpdated',
  'GuestPrefillPermissionChanged',
  'GuestPrefillConfigChanged',
  'PrefillDefaultsChanged',

  // Prefill Daemon
  'DaemonSessionCreated',
  'DaemonSessionUpdated',
  'DaemonSessionTerminated',
  'SessionSubscribed',
  'AuthStateChanged',
  'CredentialChallenge',
  'StatusChanged',
  'PrefillStateChanged',
  'PrefillProgress',
  'PrefillHistoryUpdated',
  'SessionEnded',

  // Epic Prefill Daemon Events
  'EpicDaemonSessionCreated',
  'EpicDaemonSessionUpdated',
  'EpicDaemonSessionTerminated',
  'EpicAuthStateChanged',
  'EpicCredentialChallenge',
  'EpicStatusChanged',
  'EpicPrefillStateChanged',
  'EpicPrefillProgress',
  'EpicPrefillHistoryUpdated',
  'EpicSessionEnded',

  // Battle.net Prefill Daemon Events
  'BattleNetDaemonSessionCreated',
  'BattleNetDaemonSessionUpdated',
  'BattleNetDaemonSessionTerminated',
  'BattleNetAuthStateChanged',
  'BattleNetCredentialChallenge',
  'BattleNetStatusChanged',
  'BattleNetPrefillStateChanged',
  'BattleNetPrefillProgress',
  'BattleNetPrefillHistoryUpdated',
  'BattleNetSessionEnded',

  // Riot Prefill Daemon Events
  'RiotDaemonSessionCreated',
  'RiotDaemonSessionUpdated',
  'RiotDaemonSessionTerminated',
  'RiotAuthStateChanged',
  'RiotCredentialChallenge',
  'RiotStatusChanged',
  'RiotPrefillStateChanged',
  'RiotPrefillProgress',
  'RiotPrefillHistoryUpdated',
  'RiotSessionEnded',

  // Xbox Prefill Daemon Events
  'XboxDaemonSessionCreated',
  'XboxDaemonSessionUpdated',
  'XboxDaemonSessionTerminated',
  'XboxAuthStateChanged',
  'XboxCredentialChallenge',
  'XboxStatusChanged',
  'XboxPrefillStateChanged',
  'XboxPrefillProgress',
  'XboxPrefillHistoryUpdated',
  'XboxSessionEnded',

  // Epic Guest Prefill Config
  'EpicGuestPrefillConfigChanged',

  // Battle.net Guest Prefill Config
  'BattleNetGuestPrefillConfigChanged',

  // Riot Guest Prefill Config
  'RiotGuestPrefillConfigChanged',

  // Xbox Guest Prefill Config
  'XboxGuestPrefillConfigChanged',

  // Epic Game Mappings
  'EpicMappingProgress',
  'EpicGameMappingsUpdated',

  // Blizzard / Battle.net Game Mappings
  'BlizzardGameMappingsUpdated',

  // Xbox Game Mappings
  'XboxMappingProgress',
  'XboxGameMappingsUpdated',

  // Eviction Scan
  'EvictionScanStarted',
  'EvictionScanProgress',
  'EvictionScanComplete',

  // Cache File Scan (cache_size binary)
  'CacheSizeScanStarted',
  'CacheSizeScanProgress',
  'CacheSizeScanComplete',

  // Operation wait-queue (purple waiting cards)
  'OperationWaiting',
  'OperationWaitingComplete',

  // Eviction Removal
  'EvictionRemovalStarted',
  'EvictionRemovalProgress',
  'EvictionRemovalComplete',

  // Game Images
  'GameImagesUpdated',

  // Schedules
  'SchedulesUpdated',
  'ScheduledPrefillStarted',
  'ScheduledPrefillProgress',
  'ScheduledPrefillCompleted',

  // Metrics Security
  'MetricsSecurityUpdated',

  // Status Check (DNS diagnostics)
  'StatusCheckProgress',
  'StatusCheckComplete',
  'CacheDomainsRefreshed'
] as const;

/**
 * Events that trigger a data refresh in DownloadsContext/StatsContext.
 * Subset of SIGNALR_EVENTS used by contexts that need to refetch data.
 */
export const SIGNALR_REFRESH_EVENTS = [
  // Background processing events
  'DownloadsRefresh',
  'LogProcessingComplete',
  // User action completions
  'DepotMappingComplete',
  'LogRemovalComplete',
  'CorruptionRemovalComplete',
  'ServiceRemovalComplete',
  'GameDetectionComplete',
  'GameRemovalComplete',
  'CacheClearingComplete',
  'CacheScanComplete',
  // Eviction events - refresh evicted items list after scan or removal completes
  'EvictionScanComplete',
  'EvictionRemovalComplete',
  // Client group changes (affects displayName in client stats)
  'ClientGroupCreated',
  'ClientGroupUpdated',
  'ClientGroupDeleted',
  'ClientGroupMemberAdded',
  'ClientGroupMemberRemoved'
] as const;

// SignalR Event Types

export interface ProcessingProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  mbProcessed?: number;
  mbTotal?: number;
  entriesSaved?: number;
  totalLines?: number;
  linesParsed?: number;
}

export interface LogProcessingCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  entriesProcessed?: number;
  linesProcessed?: number;
  elapsed?: number;
}

// Log Processing Started Event
export interface LogProcessingStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
}
// Standardized Log Removal Events
export interface LogRemovalStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  service?: string;
}

export interface LogRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  service: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  filesProcessed?: number;
  linesProcessed?: number;
  linesRemoved?: number;
  datasource?: string;
}

export interface LogRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled: boolean;
  service: string;
  filesProcessed?: number;
  linesProcessed?: number;
  linesRemoved?: number;
  databaseRecordsDeleted?: number;
  datasource?: string;
}

export interface GameRemovalStartedEvent {
  operationId: string;
  gameAppId: number | null;
  epicAppId: string | null;
  gameName: string;
  stageKey: string;
  context?: Record<string, string | number | boolean>;
  timestamp: string;
}
export interface GameRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  gameAppId: number | null;
  epicAppId: string | null;
  gameName: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface GameRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  cancelled?: boolean;
  gameAppId: number | null;
  epicAppId: string | null;
  gameName?: string;
  filesDeleted?: number;
  bytesFreed?: number;
  logEntriesRemoved?: number;
}

export interface ServiceRemovalStartedEvent {
  operationId: string;
  stageKey: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  serviceName: string;
  timestamp?: string;
}
export interface ServiceRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  serviceName: string;
  stageKey: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface ServiceRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  serviceName: string;
  filesDeleted?: number;
  bytesFreed?: number;
  logEntriesRemoved?: number;
}

export interface CorruptionRemovalStartedEvent {
  operationId: string;
  service: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  timestamp?: string;
}

export interface CorruptionRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  service: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  filesProcessed?: number;
  totalFiles?: number;
  timestamp?: string;
}

export interface CorruptionRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  service: string;
  error?: string;
  timestamp?: string;
}

export interface CorruptionDetectionStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
}

export interface CorruptionDetectionProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  filesProcessed?: number;
  totalFiles?: number;
  currentFile?: string;
  datasourceName?: string;
}

// Progress for a single-service "view corrupted chunk details" fetch - distinct from
// CorruptionDetectionProgressEvent (the bulk scan) so it never feeds the global
// 'corruption_detection' notification card.
export interface CorruptionDetailsProgressEvent {
  operationId: string;
  service: string;
  percentComplete: number;
  filesProcessed: number;
  totalFiles: number;
}

// C# sends the aggregate all/removable/review count projections for the completed scan.
export interface CorruptionDetectionCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  error?: string;
  status?: OperationStatus;
  totalServicesWithCorruption?: number;
  totalCorruptedChunks?: number;
  removableServiceCounts?: Record<string, number>;
  reviewOnlyServiceCounts?: Record<string, number>;
  removableTotal?: number;
  reviewOnlyTotal?: number;
}

export interface GameDetectionStartedEvent {
  operationId: string;
  scanType?: 'full' | 'incremental';
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  timestamp?: string;
}

export interface GameDetectionProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  gamesDetected?: number;
  servicesDetected?: number;
  gamesProcessed?: number;
  totalGames?: number;
}

export interface GameDetectionCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  error?: string;
  status?: OperationStatus;
  totalGamesDetected?: number;
  totalServicesDetected?: number;
  newGamesCount?: number;
  timestamp?: string;
}

// Database Reset Events
export interface DatabaseResetStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
}
export interface DatabaseResetProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
}

// Cache Clear Event Types (used by CacheClearingProgress/CacheClearingComplete handlers)
export interface CacheClearProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  statusMessage?: string;
  error?: string;
  filesDeleted?: number;
  directoriesProcessed?: number;
  totalDirectories?: number;
  bytesDeleted?: number;
  datasourceName?: string;
}

export interface CacheClearCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  error?: string;
  status?: OperationStatus;
  filesDeleted?: number;
  directoriesProcessed?: number;
  bytesDeleted?: number;
  datasourcesCleared?: number;
  duration?: number;
}

// Standardized Cache Clearing Events
export interface CacheClearingStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
}

export interface DepotMappingStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  isLoggedOn?: boolean;
  status?: OperationStatus;
  scanMode?: 'incremental' | 'full' | 'github';
  totalApps?: number;
  processedApps?: number;
  percentComplete?: number;
  progressPercent?: number;
  startTime?: string;
}

export interface DepotMappingProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  progressPercent?: number;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  isLoggedOn?: boolean;
  processedBatches?: number;
  totalBatches?: number;
  depotMappingsFound?: number;
  processedMappings?: number;
  totalMappings?: number;
  mappingsApplied?: number;
  totalApps?: number;
  processedApps?: number;
  failedBatches?: number;
  remainingApps?: number[];
}

export interface DepotMappingCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  scanMode?: 'incremental' | 'full' | 'github';
  error?: string;
  totalMappings?: number;
  downloadsUpdated?: number;
  totalApps?: number;
  totalBatches?: number;
  depotMappingsFound?: number;
}

export interface SteamSessionErrorEvent {
  errorType: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  reconnectAttempts?: number;
  result?: string;
  extendedResult?: string;
  timestamp?: string;
  wasRebuildActive?: boolean;
}

export interface SteamAutoLogoutEvent {
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  reason: string;
  replacementCount: number;
  timestamp: string;
}

export interface ShowToastEvent {
  type: NotificationVariant;
  message: string;
  duration?: number;
}

export interface GuestRefreshRateUpdatedEvent {
  refreshRate: string;
}

export interface DefaultGuestRefreshRateChangedEvent {
  refreshRate: string;
}

export interface GuestRefreshRateLockChangedEvent {
  locked: boolean;
}

export interface DaemonSessionCreatedEvent {
  id: string;
  userId: string;
  containerName: string;
  status: string;
  authState: string;
  isPrefilling: boolean;
  createdAt: string;
  endedAt?: string;
  expiresAt: string;
  timeRemainingSeconds: number;
  ipAddress?: string;
  operatingSystem?: string;
  browser?: string;
  lastSeenAt: string;
  steamUsername?: string;
  currentAppId?: string;
  currentAppName?: string;
  platform?: string;
  username?: string;
}

export interface DaemonSessionUpdatedEvent {
  id: string;
  userId: string;
  containerName: string;
  status: string;
  authState: string;
  isPrefilling: boolean;
  createdAt: string;
  endedAt?: string;
  expiresAt: string;
  timeRemainingSeconds: number;
  ipAddress?: string;
  operatingSystem?: string;
  browser?: string;
  lastSeenAt: string;
  steamUsername?: string;
  currentAppId?: string;
  currentAppName?: string;
  platform?: string;
  username?: string;
}

export interface DaemonSessionTerminatedEvent {
  sessionId: string;
  reason: string;
}

export interface UserSessionRevokedEvent {
  sessionId: string;
  sessionType: SessionType;
}

export interface PrefillHistoryUpdatedEvent {
  sessionId: string;
  appId: string;
  status: string;
}

// User Preferences Events
export interface UserPreferencesUpdatedEvent {
  sessionId: string;
  preferences: {
    selectedTheme: string | null;
    sharpCorners: boolean;
    disableFocusOutlines: boolean;
    disableTooltips: boolean;
    picsAlwaysVisible: boolean;
    disableStickyNotifications: boolean;
    useLocalTimezone: boolean;
    use24HourFormat: boolean;
    showDatasourceLabels: boolean;
    showYearInDates: boolean;
    refreshRate?: string | null;
    refreshRateLocked?: boolean | null;
    allowedTimeFormats?: string[] | null;
    maxThreadCount?: number | null;
  };
}

export interface DefaultGuestThemeChangedEvent {
  newThemeId: string;
}

// Data Import Events
export interface DataImportStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  importType?: string;
}

export interface DataImportProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  recordsProcessed?: number;
  totalRecords?: number;
  recordsImported?: number;
  recordsSkipped?: number;
}

export interface DataImportCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  recordsImported?: number;
  recordsSkipped?: number;
  recordsErrors?: number;
  totalRecords?: number;
}

// ============================================================================
// Epic Prefill Daemon Events
// ============================================================================

/**
 * Fired when a prefill history entry is created or updated.
 * BROADCAST to both downloads hub and Epic daemon hub via NotifyAllDownloadsAndEpicHubAsync.
 * Can be handled by NotificationsContext.tsx.
 */
export interface EpicPrefillHistoryUpdatedEvent {
  sessionId: string;
  appId: string;
  status: string;
}

/**
 * Fired when the default Epic guest prefill configuration changes.
 * BROADCAST to all clients via NotifyAllAsync on the downloads hub.
 * Handled by GuestConfiguration.tsx and ActiveSessions.tsx for local state updates.
 */
export interface EpicGuestPrefillConfigChangedEvent {
  enabledByDefault: boolean;
  durationHours: number;
  epicMaxThreadCount: number;
}

/**
 * Battle.net guest prefill config changed (anonymous service - no thread limit).
 * Handled by GuestConfiguration.tsx and ActiveSessions.tsx for local state updates.
 */
export interface BattleNetGuestPrefillConfigChangedEvent {
  enabledByDefault: boolean;
  durationHours: number;
}

/**
 * Riot guest prefill config changed (anonymous service - no thread limit).
 * Handled by GuestConfiguration.tsx and ActiveSessions.tsx for local state updates.
 */
export interface RiotGuestPrefillConfigChangedEvent {
  enabledByDefault: boolean;
  durationHours: number;
}

/**
 * Xbox guest prefill config changed (login-required service - mirrors Epic, has thread limit).
 * Handled by GuestConfiguration.tsx and ActiveSessions.tsx for local state updates.
 */
export interface XboxGuestPrefillConfigChangedEvent {
  enabledByDefault: boolean;
  durationHours: number;
  xboxMaxThreadCount: number;
}

// ============================================================================
// Epic Game Mapping Events
// ============================================================================

export interface EpicMappingProgressEvent {
  operationId: string;
  status: OperationStatus;
  percentComplete: number;
  gamesDiscovered: number;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
}

export interface EpicGameMappingsUpdatedEvent {
  totalGames: number;
  newGames: number;
  updatedGames: number;
  lastUpdatedUtc: string;
}

// ============================================================================
// Xbox Game Mapping Events
// ============================================================================

export interface XboxMappingProgressEvent {
  operationId: string;
  status: OperationStatus;
  percentComplete: number;
  gamesDiscovered: number;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  /** True only on the final success/failure/cancel event; interim progress ticks are false. */
  isTerminal?: boolean;
}

export interface ScheduledPrefillStartedEvent {
  operationId: string;
  serviceCount: number;
  showNotification?: boolean;
}

export interface ScheduledPrefillProgressEvent {
  operationId: string;
  serviceId: string;
  stage: string;
  message: string;
  needsLoginReason?: string | null;
  bytesDownloaded?: number | null;
  downloadSessionId?: string | null;
  percentComplete?: number | null;
  showNotification?: boolean;
}

export interface ScheduledPrefillCompletedEvent {
  operationId: string | null;
  success: boolean;
  error?: string | null;
  showNotification?: boolean;
}

// Mirrors the backend payload emitted by XboxMappingService.MergeDaemonCatalogCoreAsync
// ({ source, newMappings, newPatterns }). Xbox tracks newly discovered games (newMappings) and
// newly stored CDN URL fragments (newPatterns); it does NOT compute a running total / updated count
// like Epic, so this intentionally diverges from EpicGameMappingsUpdatedEvent.
export interface XboxGameMappingsUpdatedEvent {
  source: string;
  newMappings: number;
  newPatterns: number;
}

export interface EvictionScanStartedEvent {
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  operationId: string;
}

export interface EvictionScanProgressEvent {
  operationId: string;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  percentComplete: number;
  processed: number;
  totalEstimate: number;
  evicted: number;
  unEvicted: number;
}

export interface EvictionScanCompleteEvent {
  success: boolean;
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  processed: number;
  evicted: number;
  unEvicted: number;
  prunedOrphans?: number;
  error?: string;
}

export interface CacheSizeScanStartedEvent {
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  operationId: string;
}

export interface CacheSizeScanProgressEvent {
  operationId: string;
  status: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  percentComplete: number;
  directoriesScanned: number;
  totalDirectories: number;
  totalFiles: number;
  totalBytes: number;
}

export interface CacheSizeScanCompleteEvent {
  success: boolean;
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  totalFiles: number;
  totalBytes: number;
  formattedSize?: string;
  error?: string;
}

/**
 * Emitted when an operation is parked in the backend wait-queue behind a conflicting
 * operation. operationType is the backend OperationType wire string (camelCase).
 */
export interface OperationWaitingEvent {
  operationId: string;
  operationType: string;
  name: string;
}

/**
 * Emitted when a WAITING operation terminates without being promoted (cancelled from
 * the card, or its start failed at promotion). Promotion itself emits nothing - the
 * promoted operation's own Started event replaces the waiting card.
 */
export interface OperationWaitingCompleteEvent {
  operationId: string;
  operationType: string;
  cancelled: boolean;
  error?: string;
}

export interface EvictionRemovalStartedEvent {
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  gameName?: string;
  gameAppId?: string;
  epicAppId?: string;
}

export interface EvictionRemovalProgressEvent {
  operationId: string;
  status?: OperationStatus;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  percentComplete?: number;
  downloadsRemoved?: number;
  logEntriesRemoved?: number;
}

export interface EvictionRemovalCompleteEvent {
  success: boolean;
  operationId: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  cancelled?: boolean;
  downloadsRemoved?: number;
  logEntriesRemoved?: number;
  error?: string;
}
