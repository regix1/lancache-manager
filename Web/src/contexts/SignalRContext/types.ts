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
  'ServiceRemovalStarted',
  'ServiceRemovalProgress',
  'ServiceRemovalComplete',
  'CorruptionDetectionStarted',
  'CorruptionDetectionProgress',
  'CorruptionDetectionComplete',
  'CorruptionRemovalStarted',
  'CorruptionRemovalProgress',
  'CorruptionRemovalComplete',

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
  'SteamUserBanned',
  'SteamUserUnbanned',
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

  // Epic Guest Prefill Config
  'EpicGuestPrefillConfigChanged',

  // Epic Game Mappings
  'EpicMappingProgress',
  'EpicGameMappingsUpdated',

  // Eviction Scan
  'EvictionScanStarted',
  'EvictionScanProgress',
  'EvictionScanComplete',

  // Eviction Removal
  'EvictionRemovalStarted',
  'EvictionRemovalProgress',
  'EvictionRemovalComplete',

  // Game Images
  'GameImagesUpdated',

  // Schedules
  'SchedulesUpdated'
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
  // Eviction events — refresh evicted items list after scan or removal completes
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
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  gameAppId?: number;
  gameName?: string;
  timestamp?: string;
}
export interface GameRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: OperationStatus;
  gameAppId: number;
  gameName: string;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

// C# sends GameRemovalComplete record: Success, OperationId, GameAppId (uint), GameName, Message, FilesDeleted, BytesFreed, LogEntriesRemoved
export interface GameRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  context?: Record<string, string | number | boolean>;
  /** @deprecated use stageKey instead */
  message: string;
  cancelled?: boolean;
  gameAppId: number;
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
  status: OperationStatus;
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

// C# sends anonymous object: OperationId, Success, Status, Message, Cancelled, totalServicesWithCorruption, totalCorruptedChunks
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
  downloadsRemoved?: number;
  logEntriesRemoved?: number;
  error?: string;
}
