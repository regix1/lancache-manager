export type NotificationMode = 'all' | 'manual' | 'silent';

export const isNotificationMode = (value: string): value is NotificationMode =>
  value === 'all' || value === 'manual' || value === 'silent';

export type NotificationDisplayMode = 'full' | 'condensed';

export const isNotificationDisplayMode = (value: string): value is NotificationDisplayMode =>
  value === 'full' || value === 'condensed';

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
}
