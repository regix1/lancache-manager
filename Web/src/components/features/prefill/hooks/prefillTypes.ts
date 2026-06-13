export interface PrefillProgress {
  state: string;
  message?: string;
  currentAppId: string;
  currentAppName?: string;
  percentComplete: number;
  bytesDownloaded: number;
  totalBytes: number;
  bytesPerSecond: number;
  elapsedSeconds: number;
  /**
   * Total number of apps in this prefill job (when known). Used to render the two-tier
   * "Game X of N" overall bar. Seeded from `expectedAppCount` / the daemon's `totalApps`.
   */
  expectedAppCount?: number;
  /** Total apps in the job per the daemon (camelCase via SignalR). */
  totalApps?: number;
  /** Count of apps downloaded so far in the job (camelCase via SignalR). */
  updatedApps?: number;
  /** Count of apps already up-to-date in the job (camelCase via SignalR). */
  alreadyUpToDate?: number;
  /** Count of apps that failed in the job (camelCase via SignalR). */
  failedApps?: number;
}

export interface BackgroundCompletion {
  completedAt: string;
  message: string;
  duration: number;
}

export interface CachedAnimationItem {
  appId: string;
  appName?: string;
  totalBytes: number;
}
