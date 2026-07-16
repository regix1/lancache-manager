export type NotificationMode = 'all' | 'manual' | 'silent';

export const isNotificationMode = (value: string): value is NotificationMode =>
  value === 'all' || value === 'manual' || value === 'silent';

export interface ServiceScheduleInfo {
  key: string;
  intervalHours: number;
  runOnStartup: boolean;
  isRunning: boolean;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
  notificationMode: NotificationMode;
  supportsNotifications: boolean;
}
