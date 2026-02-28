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
  'PrefillSessionsCleared',
  'BannedSteamUsersCleared',

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
  'UserSessionsUpdated',
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
  'EpicGuestPrefillConfigChanged'
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
  progress?: number;
  message?: string;
  mbProcessed?: number;
  mbTotal?: number;
  entriesProcessed?: number;
  totalLines?: number;
  linesProcessed?: number;
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
  gameAppId?: string;
  gameName?: string;
}
export interface GameRemovalProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  gameAppId: string;
  gameName: string;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface GameRemovalCompleteEvent {
  operationId: string;
  success: boolean;
  message: string;
  cancelled?: boolean;
  gameAppId: string;
  filesDeleted?: number;
  bytesFreed?: number;
  logEntriesRemoved?: number;
}

export interface ServiceRemovalStartedEvent {
  operationId: string;
  message: string;
  serviceName: string;
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

export interface CorruptionDetectionCompleteEvent {
  operationId: string;
  success: boolean;
  message: string;
  cancelled?: boolean;
  error?: string;
  totalServicesWithCorruption?: number;
  totalCorruptedChunks?: number;
}

export interface GameDetectionStartedEvent {
  operationId: string;
  scanType?: 'full' | 'incremental';
  message?: string;
}

export interface GameDetectionProgressEvent {
  operationId: string;
  percentComplete: number;
  status: string;
  message?: string;
  gamesDetected?: number;
  servicesDetected?: number;
  progressPercent?: number;
}

export interface GameDetectionCompleteEvent {
  operationId: string;
  success: boolean;
  message: string;
  cancelled?: boolean;
  error?: string;
  totalGamesDetected?: number;
  totalServicesDetected?: number;
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
  filesDeleted?: number;
  directoriesProcessed?: number;
  datasourceName?: string;
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
  sessionReplacedCount?: number;
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
