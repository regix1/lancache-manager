export interface ServiceScheduleInfo {
  key: string;
  intervalHours: number;
  runOnStartup: boolean;
  isRunning: boolean;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
}
