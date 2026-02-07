export interface PrefillProgress {
  state: string;
  message?: string;
  currentAppId: number;
  currentAppName?: string;
  percentComplete: number;
  bytesDownloaded: number;
  totalBytes: number;
  bytesPerSecond: number;
  elapsedSeconds: number;
}

export interface BackgroundCompletion {
  completedAt: string;
  message: string;
  duration: number;
}

export interface CachedAnimationItem {
  appId: number;
  appName?: string;
  totalBytes: number;
}
