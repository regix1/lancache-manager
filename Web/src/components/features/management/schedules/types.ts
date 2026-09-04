import type { CustomSchedule } from './custom-schedule/types';

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
  /**
   * A cron recurrence plus an optional time-of-day window. `null`/`undefined` means the schedule
   * runs on `intervalHours` exactly as before. When one IS present it wins over the interval, and
   * the interval value is left untouched so clearing the schedule puts it back on the cadence it
   * had before.
   */
  customSchedule?: CustomSchedule | null;
  runOnStartup: boolean;
  isRunning: boolean;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
  notificationMode: NotificationMode;
  notificationDisplayMode: NotificationDisplayMode;
  /**
   * Only on scheduled prefill, the one service running several platforms under a single key. Keyed
   * by the wire platform name (Steam, Epic, Xbox, BattleNet, Riot) and holding only the platforms
   * that chose a style; a platform absent here falls back to `notificationDisplayMode`.
   */
  platformNotificationDisplayModes?: Record<string, NotificationDisplayMode> | null;
  supportsNotifications: boolean;
  /** Present on the Steam depot mapping schedule only, and only while a full scan is required. */
  pendingFullScan?: PendingFullScan | null;
  /**
   * Present on the Xbox mapping schedule only, and only while its sign-in is waiting for the user to
   * approve a device code. That wait is what makes `isRunning` true, so this is what the row shows
   * instead of leaving Run Now greyed out with no reason.
   */
  awaitingSignIn?: boolean | null;
}
