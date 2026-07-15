export type ScheduledPrefillServiceKey = 'steam' | 'epic' | 'xbox' | 'battleNet' | 'riot';

export type ScheduledPrefillServiceId = 'Steam' | 'Epic' | 'Xbox' | 'BattleNet' | 'Riot';

export type ScheduledPrefillAccountServiceId = 'steam' | 'epic' | 'xbox';

export type ScheduledPrefillPreset = 'All' | 'Recent' | 'Top';

export type ScheduledPrefillOperatingSystem = 'Windows' | 'Linux' | 'Macos';

export type ScheduledPrefillMaxConcurrencyMode = 'Auto' | 'Fixed';

/**
 * What happens to a persistent prefill container and its saved login across a manager
 * restart. Serialized camelCase on the wire by the backend's dedicated
 * `PersistenceModeJsonConverter` (NOT the bare `JsonStringEnumConverter<T>` used for the
 * other scheduled-prefill enums above, which emits PascalCase instead - do not "fix" this
 * casing to match `ScheduledPrefillPreset` etc., it is deliberately different).
 */
export type ScheduledPrefillPersistenceMode =
  | 'killOnRestart'
  | 'keepAcrossRestart'
  | 'fullPersistence';

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
  /**
   * Per-service override of the global `persistenceMode`. `null`/`undefined` means
   * "use the global setting" - the backend's `GetEffectivePersistenceMode` resolves it as
   * `override ?? global`.
   */
  persistenceMode?: ScheduledPrefillPersistenceMode | null;
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
  /**
   * Global default for what happens to persistent containers across a manager restart.
   * Nullable only on the C# DTO for old-JSON migration compatibility; the backend's
   * `Validate` throws before a config is ever returned, so a loaded config always has this
   * populated.
   */
  persistenceMode: ScheduledPrefillPersistenceMode;
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
