import type { ReactNode } from 'react';

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
  'GameImagesUpdated'
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
  status: string;
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
  message: string;
  cancelled?: boolean;
  entriesProcessed?: number;
  linesProcessed?: number;
  elapsed?: number;
}

// Log Processing Started Event
export interface LogProcessingStartedEvent {
  operationId: string;
  message: string;
}
// Standardized Log Removal Events
export interface LogRemovalStartedEvent {
  operationId: string;
  message: string;
  service?: string;
}

export interface LogRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  service: string;
  message?: string;
  filesProcessed?: number;
  linesProcessed?: number;
  linesRemoved?: number;
  datasource?: string;
}

export interface LogRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  status: string;
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
  message: string;
  gameAppId?: number;
  gameName?: string;
  timestamp?: string;
}
export interface GameRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  gameAppId: number;
  gameName: string;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

// C# sends GameRemovalComplete record: Success, OperationId, GameAppId (uint), GameName, Message, FilesDeleted, BytesFreed, LogEntriesRemoved
export interface GameRemovalCompleteEvent {
  operationId: string;
  success: boolean;
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
  message: string;
  serviceName: string;
  timestamp?: string;
}
export interface ServiceRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  serviceName: string;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface ServiceRemovalCompleteEvent {
  operationId: string;
  success: boolean;
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
  message?: string;
  timestamp?: string;
}

export interface CorruptionRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  service: string;
  message?: string;
  filesProcessed?: number;
  totalFiles?: number;
  timestamp?: string;
}

export interface CorruptionRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  message: string;
  cancelled?: boolean;
  service: string;
  error?: string;
  timestamp?: string;
}

export interface CorruptionDetectionStartedEvent {
  operationId: string;
  message?: string;
}

export interface CorruptionDetectionProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
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
  message: string;
  cancelled?: boolean;
  error?: string;
  status?: string;
  totalServicesWithCorruption?: number;
  totalCorruptedChunks?: number;
}

export interface GameDetectionStartedEvent {
  operationId: string;
  scanType?: 'full' | 'incremental';
  message?: string;
  timestamp?: string;
}

export interface GameDetectionProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  message?: string;
  gamesDetected?: number;
  servicesDetected?: number;
  gamesProcessed?: number;
  totalGames?: number;
}

export interface GameDetectionCompleteEvent {
  operationId: string;
  success: boolean;
  message: string;
  cancelled?: boolean;
  error?: string;
  status?: string;
  totalGamesDetected?: number;
  totalServicesDetected?: number;
  timestamp?: string;
}

// Database Reset Events
export interface DatabaseResetStartedEvent {
  operationId: string;
  message: string;
}
export interface DatabaseResetProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  message?: string;
}

// Cache Clear Event Types (used by CacheClearingProgress/CacheClearingComplete handlers)
export interface CacheClearProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
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
  message: string;
  cancelled?: boolean;
  error?: string;
  status?: string;
  filesDeleted?: number;
  directoriesProcessed?: number;
  bytesDeleted?: number;
  datasourcesCleared?: number;
  duration?: number;
}

// Standardized Cache Clearing Events
export interface CacheClearingStartedEvent {
  operationId: string;
  message?: string;
}

export interface DepotMappingStartedEvent {
  operationId: string;
  message?: string;
  isLoggedOn?: boolean;
  status?: string;
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
  status: string;
  progressPercent?: number;
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
  message?: string;
  reconnectAttempts?: number;
  result?: string;
  extendedResult?: string;
  timestamp?: string;
  wasRebuildActive?: boolean;
}

export interface SteamAutoLogoutEvent {
  message: string;
  reason: string;
  replacementCount: number;
  timestamp: string;
}

export interface ShowToastEvent {
  type: 'success' | 'error' | 'info' | 'warning';
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
  sessionType: 'admin' | 'guest';
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
  message?: string;
  importType?: string;
}

export interface DataImportProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  message?: string;
  recordsProcessed?: number;
  totalRecords?: number;
  recordsImported?: number;
  recordsSkipped?: number;
}

export interface DataImportCompleteEvent {
  operationId: string;
  success: boolean;
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
 * Fired when the auth state of an Epic daemon session changes.
 * Sent per-connection to the Epic daemon hub (not broadcast to downloads hub).
 * Handled by usePrefillSignalR.ts on the dedicated Epic prefill daemon hub.
 */
export interface EpicAuthStateChangedEvent {
  sessionId: string;
  authState: string;
}

/**
 * Fired when the Epic daemon needs credentials (e.g., authorization URL).
 * Sent per-connection to the Epic daemon hub (not broadcast to downloads hub).
 * Handled by usePrefillSignalR.ts / EpicAuthModal on the dedicated Epic prefill daemon hub.
 */
export interface EpicCredentialChallengeEvent {
  sessionId: string;
  challenge: {
    credentialType: string;
    authorizationUrl?: string;
  };
}

/**
 * Fired when the Epic daemon status changes (e.g., awaiting-login, logged-in).
 * Sent per-connection to the Epic daemon hub (not broadcast to downloads hub).
 * Handled by usePrefillSignalR.ts on the dedicated Epic prefill daemon hub.
 */
export interface EpicStatusChangedEvent {
  sessionId: string;
  status: {
    status: string;
    displayName?: string;
  };
}

/**
 * Fired when prefill state changes (started, completed, failed, cancelled).
 * Sent per-connection to the Epic daemon hub (not broadcast to downloads hub).
 * Handled by usePrefillSignalR.ts on the dedicated Epic prefill daemon hub.
 */
export interface EpicPrefillStateChangedEvent {
  sessionId: string;
  state: string;
  durationSeconds?: number;
}

/**
 * Fired during prefill download progress for each game.
 * Sent per-connection to the Epic daemon hub (not broadcast to downloads hub).
 * Handled by usePrefillSignalR.ts on the dedicated Epic prefill daemon hub.
 */
export interface EpicPrefillProgressEvent {
  sessionId: string;
  progress: {
    state: string;
    currentAppId?: string;
    currentAppName?: string;
    totalBytes?: number;
    bytesDownloaded?: number;
    percentComplete?: number;
    bytesPerSecond?: number;
    elapsedSeconds?: number;
    totalApps?: number;
    updatedApps?: number;
    result?: string;
    errorMessage?: string;
  };
}

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
 * Fired when a daemon session ends.
 * Sent per-connection to the Epic daemon hub (not broadcast to downloads hub).
 * Handled by usePrefillSignalR.ts on the dedicated Epic prefill daemon hub.
 */
export interface EpicSessionEndedEvent {
  sessionId: string;
  reason: string;
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
  status: string;
  percentComplete: number;
  gamesDiscovered: number;
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
  message: string;
  operationId: string;
}

export interface EvictionScanProgressEvent {
  operationId: string;
  status: string;
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
  message: string;
  processed: number;
  evicted: number;
  unEvicted: number;
  error?: string;
}

export interface EvictionRemovalStartedEvent {
  operationId: string;
  message?: string;
}

export interface EvictionRemovalProgressEvent {
  operationId: string;
  status?: string;
  message?: string;
  percentComplete?: number;
  downloadsRemoved?: number;
  logEntriesRemoved?: number;
}

export interface EvictionRemovalCompleteEvent {
  success: boolean;
  operationId: string;
  message?: string;
  downloadsRemoved?: number;
  logEntriesRemoved?: number;
  error?: string;
}
