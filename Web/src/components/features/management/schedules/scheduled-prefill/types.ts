export type ScheduledPrefillServiceKey = 'steam' | 'epic' | 'xbox' | 'battleNet' | 'riot';

export type ScheduledPrefillServiceId = 'Steam' | 'Epic' | 'Xbox' | 'BattleNet' | 'Riot';

export type ScheduledPrefillAccountServiceId = 'steam' | 'epic' | 'xbox';

export type ScheduledPrefillPreset = 'All' | 'Recent' | 'Top';

export type ScheduledPrefillOperatingSystem = 'Windows' | 'Linux' | 'Macos';

export type ScheduledPrefillMaxConcurrencyMode = 'Auto' | 'Fixed';

interface ScheduledPrefillAutoMaxConcurrency {
  mode: 'Auto';
  value?: null;
}

interface ScheduledPrefillFixedMaxConcurrency {
  mode: 'Fixed';
  value: number;
}

export type ScheduledPrefillMaxConcurrency =
  | ScheduledPrefillAutoMaxConcurrency
  | ScheduledPrefillFixedMaxConcurrency;

export interface ScheduledPrefillServiceConfigDto {
  serviceId: ScheduledPrefillServiceId;
  enabled: boolean;
  /**
   * Whether this platform's run appears in the universal notification bar.
   * Optional for compatibility with configurations saved before this setting existed.
   */
  showNotification?: boolean;
  /**
   * Per-service run interval in hours. `>0` = every N hours, `0` = paused,
   * `-1` = run on startup only. Saved via the whole-config round-trip.
   */
  intervalHours: number;
  preset: ScheduledPrefillPreset;
  selectedAppIds: string[];
  topCount?: number | null;
  operatingSystems: ScheduledPrefillOperatingSystem[];
  force: boolean;
  maxConcurrency: ScheduledPrefillMaxConcurrency;
}

/** One row of the per-service schedule summary from `GET .../scheduledPrefill/schedule`. */
export interface ScheduledPrefillServiceScheduleDto {
  serviceId: ScheduledPrefillServiceId;
  intervalHours: number;
  enabled: boolean;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
}

export interface ScheduledPrefillConfigDto {
  version: number;
  maxServiceRuntime: string;
  stallTimeout: string;
  steam: ScheduledPrefillServiceConfigDto;
  epic: ScheduledPrefillServiceConfigDto;
  xbox: ScheduledPrefillServiceConfigDto;
  battleNet: ScheduledPrefillServiceConfigDto;
  riot: ScheduledPrefillServiceConfigDto;
}

/**
 * Per-row account readiness for the schedule table. `null` for anonymous services
 * (Battle.net/Riot), where a running container is all the readiness there is.
 */
export type ScheduledPrefillRowLoginState = 'loggedIn' | 'loginRequired';
