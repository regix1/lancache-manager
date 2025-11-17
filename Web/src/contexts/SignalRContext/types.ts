import type { ReactNode } from 'react';

// Event handler type for SignalR events
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
  'DatabaseResetProgress',
  'LogRemovalProgress',
  'LogRemovalComplete',
  'GameRemovalComplete',
  'ServiceRemovalComplete',
  'CacheClearProgress',
  'CacheClearComplete',
  'CorruptionRemovalStarted',
  'CorruptionRemovalComplete',
  'GameDetectionStarted',
  'GameDetectionComplete',
  'GuestDurationUpdated',
  'AutomaticScanSkipped',
  'UserPreferencesUpdated',
  'UserPreferencesReset',
  'UserSessionsCleared',
  'DefaultGuestThemeChanged'
] as const;

export type SignalREvent = (typeof SIGNALR_EVENTS)[number];
