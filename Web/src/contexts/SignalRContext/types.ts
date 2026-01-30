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
  'ProcessingProgress',
  'FastProcessingComplete',
  'LogRemovalProgress',
  'LogRemovalComplete',

  // Database Operations
  'DatabaseResetProgress',
  'PrefillSessionsCleared',
  'BannedSteamUsersCleared',

  // SteamKit2 / Depot Mapping
  'DepotMappingStarted',
  'DepotMappingProgress',
  'DepotMappingComplete',
  'SteamSessionError',
  'SteamAutoLogout',
  'AutomaticScanSkipped',

  // Cache Operations
  'CacheClearProgress',
  'CacheClearComplete',
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
  'GameRemovalProgress',
  'GameRemovalComplete',

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
  'GuestDurationUpdated',
  'GuestPrefillPermissionChanged',

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
  'SessionEnded'
] as const;

/**
 * Events that trigger a data refresh in DownloadsContext/StatsContext.
 * Subset of SIGNALR_EVENTS used by contexts that need to refetch data.
 */
export const SIGNALR_REFRESH_EVENTS = [
  // Background processing events
  'DownloadsRefresh',
  'FastProcessingComplete',
  // User action completions
  'DepotMappingComplete',
  'LogRemovalComplete',
  'CorruptionRemovalComplete',
  'ServiceRemovalComplete',
  'GameDetectionComplete',
  'GameRemovalComplete',
  'CacheClearComplete',
  // Client group changes (affects displayName in client stats)
  'ClientGroupCreated',
  'ClientGroupUpdated',
  'ClientGroupDeleted',
  'ClientGroupMemberAdded',
  'ClientGroupMemberRemoved'
] as const;

// SignalR Event Types

export interface ProcessingProgressEvent {
  percentComplete?: number;
  progress?: number;
  status?: string;
  message?: string;
  mbProcessed?: number;
  mbTotal?: number;
  entriesProcessed?: number;
  totalLines?: number;
  linesProcessed?: number;
}

export interface FastProcessingCompleteEvent {
  success?: boolean;
  message?: string;
  entriesProcessed?: number;
  linesProcessed?: number;
  elapsed?: number;
}

export interface LogRemovalProgressEvent {
  service: string;
  status: 'starting' | 'removing' | 'complete' | 'error';
  message?: string;
  percentComplete?: number;
  linesProcessed?: number;
  linesRemoved?: number;
}

export interface LogRemovalCompleteEvent {
  service: string;
  success: boolean;
  message?: string;
  linesProcessed?: number;
}

export interface GameRemovalProgressEvent {
  gameAppId: number;
  gameName: string;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface GameRemovalCompleteEvent {
  gameAppId: number;
  success: boolean;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
  logEntriesRemoved?: number;
}

export interface ServiceRemovalProgressEvent {
  serviceName: string;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface ServiceRemovalCompleteEvent {
  serviceName: string;
  success: boolean;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
  logEntriesRemoved?: number;
}

export interface CorruptionRemovalStartedEvent {
  service: string;
  operationId?: string;
  message?: string;
}

export interface CorruptionRemovalProgressEvent {
  service: string;
  operationId?: string;
  status: string;
  message: string;
  percentComplete?: number;
  filesProcessed?: number;
  totalFiles?: number;
  timestamp?: string;
}

export interface CorruptionRemovalCompleteEvent {
  service: string;
  success: boolean;
  message?: string;
  error?: string;
}

export interface CorruptionDetectionStartedEvent {
  operationId: string;
  message?: string;
}

export interface CorruptionDetectionProgressEvent {
  operationId: string;
  status: string;
  message?: string;
  filesProcessed?: number;
  totalFiles?: number;
  percentComplete?: number;
  currentFile?: string;
  datasourceName?: string;
}

export interface CorruptionDetectionCompleteEvent {
  operationId: string;
  success: boolean;
  message?: string;
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
  status: string;
  message: string;
  gamesDetected?: number;
  servicesDetected?: number;
  progressPercent?: number;
}

export interface GameDetectionCompleteEvent {
  operationId: string;
  success: boolean;
  message?: string;
  error?: string;
  totalGamesDetected?: number;
  totalServicesDetected?: number;
}

export interface DatabaseResetProgressEvent {
  status: string;
  message?: string;
  percentComplete?: number;
}

export interface CacheClearProgressEvent {
  operationId?: string;
  statusMessage?: string;
  percentComplete?: number;
  filesDeleted?: number;
  directoriesProcessed?: number;
  bytesDeleted?: number;
  datasourceName?: string;
}

export interface CacheClearCompleteEvent {
  success: boolean;
  message?: string;
  error?: string;
  filesDeleted?: number;
  directoriesProcessed?: number;
  datasourceName?: string;
}

export interface DepotMappingStartedEvent {
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
  percentComplete?: number;
  progressPercent?: number;
  message?: string;
  status?: string;
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
  success: boolean;
  cancelled?: boolean;
  scanMode?: 'incremental' | 'full' | 'github';
  message?: string;
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

export interface GuestPrefillPermissionChangedEvent {
  deviceId: string;
  enabled: boolean;
  expiresAt?: string;
}

export interface SteamUserBannedEvent {
  deviceId: string;
  username: string;
  reason?: string;
  expiresAt?: string;
}

export interface SteamUserUnbannedEvent {
  deviceId: string;
  username: string;
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
  currentAppId?: number;
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
  currentAppId?: number;
  currentAppName?: string;
}

export interface DaemonSessionTerminatedEvent {
  sessionId: string;
  reason: string;
}

export interface UserSessionRevokedEvent {
  deviceId: string;
  sessionType: 'authenticated' | 'guest';
}

export interface GuestDurationUpdatedEvent {
  durationHours: number;
}

export interface PrefillHistoryUpdatedEvent {
  sessionId: string;
  appId: number;
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
    allowedTimeFormats?: string[] | null;
  };
}

export interface DefaultGuestThemeChangedEvent {
  newThemeId: string;
}
