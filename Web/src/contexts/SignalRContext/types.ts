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
  'UserSessionRevoked',
  'UserSessionCreated',
  'SessionLastSeenUpdated',
  'GuestPollingRateUpdated',
  'DefaultGuestPollingRateChanged',
  'EventCreated',
  'EventUpdated',
  'EventDeleted',
  'DownloadTagged'
] as const;

export type SignalREvent = (typeof SIGNALR_EVENTS)[number];

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

export interface GuestPollingRateUpdatedPayload {
  pollingRate: string;
}

export interface DefaultGuestPollingRateChangedPayload {
  pollingRate: string;
}
