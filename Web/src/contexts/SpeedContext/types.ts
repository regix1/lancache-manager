import type { ReactNode } from 'react';
import type { DownloadSpeedSnapshot, GameSpeedInfo, ClientSpeedInfo } from '../../types';

/**
 * SpeedContext provides a single source of truth for real-time download speed data.
 * It subscribes to SignalR 'DownloadSpeedUpdate' events ONCE and provides debounced
 * updates to all consumers, preventing race conditions and flaky behavior.
 */
export interface SpeedContextType {
  /** Current speed snapshot from real-time SignalR updates */
  speedSnapshot: DownloadSpeedSnapshot | null;

  /** Direct access to game speeds array for convenience */
  gameSpeeds: GameSpeedInfo[];

  /** Direct access to client speeds array for convenience */
  clientSpeeds: ClientSpeedInfo[];

  /** Count of active downloads (games being downloaded) */
  activeDownloadCount: number;

  /** Count of active clients currently downloading */
  totalActiveClients: number;

  /** Whether the context is still loading initial data */
  isLoading: boolean;

  /** Manually refresh speed data from the API */
  refreshSpeed: () => Promise<void>;
}

export interface SpeedProviderProps {
  children: ReactNode;
}
