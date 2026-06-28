import type {
  ScheduledPrefillAccountServiceId,
  ScheduledPrefillOperatingSystem,
  ScheduledPrefillPreset,
  ScheduledPrefillServiceKey
} from './types';

export const SCHEDULED_PREFILL_SERVICE_RUN_ORDER = [
  'steam',
  'epic',
  'xbox',
  'battleNet',
  'riot'
] as const satisfies readonly ScheduledPrefillServiceKey[];

export const SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS = [
  'steam',
  'epic',
  'xbox'
] as const satisfies readonly ScheduledPrefillAccountServiceId[];

export const SCHEDULED_PREFILL_PRESET_OPTIONS = [
  {
    value: 'All',
    labelKey: 'management.schedules.services.scheduledPrefill.config.presets.all'
  },
  {
    value: 'Recent',
    labelKey: 'management.schedules.services.scheduledPrefill.config.presets.recent'
  },
  {
    value: 'Top',
    labelKey: 'management.schedules.services.scheduledPrefill.config.presets.top'
  }
] as const satisfies readonly {
  value: ScheduledPrefillPreset;
  labelKey: string;
}[];

export const SCHEDULED_PREFILL_OS_OPTIONS = [
  {
    value: 'Windows',
    labelKey: 'management.schedules.services.scheduledPrefill.config.operatingSystems.windows'
  },
  {
    value: 'Linux',
    labelKey: 'management.schedules.services.scheduledPrefill.config.operatingSystems.linux'
  },
  {
    value: 'Macos',
    labelKey: 'management.schedules.services.scheduledPrefill.config.operatingSystems.macos'
  }
] as const satisfies readonly {
  value: ScheduledPrefillOperatingSystem;
  labelKey: string;
}[];

export const SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS = {
  min: 1,
  max: 256
} as const;

/** Maps backend PrefillPlatform names to frontend service config keys. */
export const SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY: Record<string, ScheduledPrefillServiceKey> =
  {
    Steam: 'steam',
    Epic: 'epic',
    Xbox: 'xbox',
    BattleNet: 'battleNet',
    Riot: 'riot'
  };
