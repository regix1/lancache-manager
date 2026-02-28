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
