export type NotificationMode = 'all' | 'manual' | 'silent';

export const isNotificationMode = (value: string): value is NotificationMode =>
  value === 'all' || value === 'manual' || value === 'silent';

export type NotificationDisplayMode = 'full' | 'condensed';

export const isNotificationDisplayMode = (value: string): value is NotificationDisplayMode =>
  value === 'full' || value === 'condensed';

/**
 * What the backend measured when it abandoned a scheduled incremental depot scan. It is also the
 * detail of the SHOW_FULL_SCAN_MODAL window event, so the Full Scan Required prompt shows the
 * figures the server found rather than any the client made up.
 */
export interface PendingFullScan {
  changeGap: number;
  estimatedAppsToScan: number;
}

export interface ServiceScheduleInfo {
  key: string;
  intervalHours: number;
  runOnStartup: boolean;
  isRunning: boolean;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
  notificationMode: NotificationMode;
  notificationDisplayMode: NotificationDisplayMode;
  supportsNotifications: boolean;
  /** Present on the Steam depot mapping schedule only, and only while a full scan is required. */
  pendingFullScan?: PendingFullScan | null;
}
