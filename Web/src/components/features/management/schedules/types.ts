import type { CustomSchedule } from './custom-schedule/types';

export type NotificationMode = 'all' | 'manual' | 'silent';

export const isNotificationMode = (value: string): value is NotificationMode =>
  value === 'all' || value === 'manual' || value === 'silent';

export type NotificationDisplayMode = 'full' | 'condensed';

export const isNotificationDisplayMode = (value: string): value is NotificationDisplayMode =>
  value === 'full' || value === 'condensed';

/**
 * The scan the game detection schedule runs on each automatic tick and on its Run Now button.
 * `hybrid` names no single run: it scans incrementally until the last full scan is a week old, then
 * runs one full scan and re-anchors on it.
 */
export type GameDetectionScanMode = 'full' | 'incremental' | 'hybrid';

export const isGameDetectionScanMode = (value: string): value is GameDetectionScanMode =>
  value === 'full' || value === 'incremental' || value === 'hybrid';

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
  /**
   * Present on the game detection schedule only, which is the one service whose run type the user
   * chooses. Its presence is what tells the card to render the scan-mode dropdown, so it is absent
   * rather than defaulted on every other schedule. Never null on game detection: a state file
   * written before the setting existed reads as `full`.
   */
  scanMode?: GameDetectionScanMode | null;
  /** Present on the Steam depot mapping schedule only, and only while a full scan is required. */
  pendingFullScan?: PendingFullScan | null;
  /**
   * Present on the Xbox mapping schedule only, and only while its sign-in is waiting for the user to
   * approve a device code. That wait is what makes `isRunning` true, so this is what the row shows
   * instead of leaving Run Now greyed out with no reason.
   */
  awaitingSignIn?: boolean | null;
}
