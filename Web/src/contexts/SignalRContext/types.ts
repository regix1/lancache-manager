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

  // Connection info
  connectionId: string | null;
}

export interface SignalRProviderProps {
  children: ReactNode;
  mockMode?: boolean;
}

// List of all SignalR events
export const SIGNALR_EVENTS = [
  'DownloadsRefresh',
  'ProcessingProgress',
  'FastProcessingComplete',
  'DownloadSpeedUpdate',
  'NetworkBandwidthUpdate',
  'DepotMappingStarted',
  'DepotMappingProgress',
  'DepotMappingComplete',
  'SteamSessionError',
  'SteamAutoLogout',
  'DatabaseResetProgress',
  'LogRemovalProgress',
  'LogRemovalComplete',
  'GameRemovalProgress',
  'GameRemovalComplete',
  'ServiceRemovalProgress',
  'ServiceRemovalComplete',
  'CacheClearProgress',
  'CacheClearComplete',
  'CorruptionRemovalStarted',
  'CorruptionRemovalComplete',
  'GameDetectionStarted',
  'GameDetectionComplete',
  'GuestDurationUpdated',
  'GuestModeLockChanged',
  'AutomaticScanSkipped',
  'UserPreferencesUpdated',
  'UserPreferencesReset',
  'UserSessionsCleared',
  'DefaultGuestThemeChanged',
  'DefaultGuestPreferencesChanged',
  'AllowedTimeFormatsChanged',
  'UserSessionRevoked',
  'UserSessionCreated',
  'SessionLastSeenUpdated',
  'GuestRefreshRateUpdated',
  'DefaultGuestRefreshRateChanged',
  'EventCreated',
  'EventUpdated',
  'EventDeleted',
  'EventsCleared',
  'ClientGroupCreated',
  'ClientGroupUpdated',
  'ClientGroupDeleted',
  'ClientGroupMemberAdded',
  'ClientGroupMemberRemoved',
  'PrefillProgress',
  'StatusChanged',
  'PrefillStateChanged'
] as const;

export type SignalREvent = (typeof SIGNALR_EVENTS)[number];

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

export type SignalRRefreshEvent = (typeof SIGNALR_REFRESH_EVENTS)[number];

// SignalR Payload Types

export interface ProcessingProgressPayload {
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

export interface FastProcessingCompletePayload {
  entriesProcessed?: number;
  linesProcessed?: number;
  elapsed?: number;
}

export interface DownloadSpeedUpdatePayload {
  timestampUtc: string;
  totalBytesPerSecond: number;
  gameSpeeds: {
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
  }[];
  clientSpeeds: {
    clientIp: string;
    bytesPerSecond: number;
    totalBytes: number;
    activeGames: number;
    cacheHitBytes: number;
    cacheMissBytes: number;
  }[];
  windowSeconds: number;
  entriesInWindow: number;
  hasActiveDownloads: boolean;
}

export interface NetworkBandwidthUpdatePayload {
  timestampUtc: string;
  interfaceName: string;
  downloadBytesPerSecond: number;
  uploadBytesPerSecond: number;
  totalBytesReceived: number;
  totalBytesSent: number;
  isAvailable: boolean;
  errorMessage?: string;
}

export interface LogRemovalProgressPayload {
  service: string;
  status: 'starting' | 'removing' | 'complete' | 'error';
  message?: string;
  percentComplete?: number;
  linesProcessed?: number;
  linesRemoved?: number;
}

export interface LogRemovalCompletePayload {
  service: string;
  success: boolean;
  message?: string;
  linesProcessed?: number;
}

export interface GameRemovalProgressPayload {
  gameAppId: number;
  gameName: string;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface GameRemovalCompletePayload {
  gameAppId: number;
  success: boolean;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
  logEntriesRemoved?: number;
}

export interface ServiceRemovalProgressPayload {
  serviceName: string;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

export interface ServiceRemovalCompletePayload {
  serviceName: string;
  success: boolean;
  message?: string;
  filesDeleted?: number;
  bytesFreed?: number;
  logEntriesRemoved?: number;
}

export interface CorruptionRemovalStartedPayload {
  service: string;
  operationId?: string;
  message?: string;
}

export interface CorruptionRemovalCompletePayload {
  service: string;
  success: boolean;
  message?: string;
  error?: string;
}

export interface GameDetectionStartedPayload {
  operationId: string;
  scanType?: 'full' | 'incremental';
  message?: string;
}

export interface GameDetectionCompletePayload {
  operationId: string;
  success: boolean;
  message?: string;
  error?: string;
  totalGamesDetected?: number;
  totalServicesDetected?: number;
}

export interface DatabaseResetProgressPayload {
  status: string;
  message?: string;
  percentComplete?: number;
}

export interface CacheClearProgressPayload {
  operationId?: string;
  statusMessage?: string;
  percentComplete?: number;
  filesDeleted?: number;
  directoriesProcessed?: number;
  bytesDeleted?: number;
}

export interface CacheClearCompletePayload {
  success: boolean;
  message?: string;
  error?: string;
  filesDeleted?: number;
  directoriesProcessed?: number;
}

export interface DepotMappingStartedPayload {
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

export interface DepotMappingProgressPayload {
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

export interface DepotMappingCompletePayload {
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

export interface SteamSessionErrorPayload {
  errorType: string;
  message?: string;
  sessionReplacedCount?: number;
}

export interface SteamAutoLogoutPayload {
  message: string;
  reason: string;
  replacementCount: number;
  timestamp: string;
}

export interface ShowToastPayload {
  type: 'success' | 'error' | 'info' | 'warning';
  message: string;
  duration?: number;
}

export interface PreferenceChangePayload {
  key: string;
  value: unknown;
}

export interface GuestRefreshRateUpdatedPayload {
  refreshRate: string;
}

export interface DefaultGuestRefreshRateChangedPayload {
  refreshRate: string;
}

export interface DefaultGuestPreferencesChangedPayload {
  key: string;
  value: boolean;
}

export interface AllowedTimeFormatsChangedPayload {
  formats: string[];
}

export interface PrefillProgressPayload {
  state: string;
  currentAppId: number;
  currentAppName?: string;
  totalBytes: number;
  bytesDownloaded: number;
  percentComplete: number;
  bytesPerSecond: number;
  elapsedSeconds: number;
  result?: string;
  errorMessage?: string;
  totalApps: number;
  updatedApps: number;
  alreadyUpToDate: number;
  failedApps: number;
  totalBytesTransferred: number;
  totalTimeSeconds: number;
  updatedAt: string;
}

