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

export const SCHEDULED_PREFILL_ANONYMOUS_SERVICE_IDS = [
  'battleNet',
  'riot'
] as const satisfies readonly ScheduledPrefillServiceKey[];

/**
 * Uniform action button size across the scheduled prefill configure modal.
 * `md` (~40px) matches the rest of Management; every button on the surface reads from this.
 */
export const SCHEDULED_PREFILL_BUTTON_SIZE = 'md' as const;

export const SCHEDULED_PREFILL_PRESET_OPTIONS = [
  {
    value: 'All',
    labelKey: 'management.schedules.services.scheduledPrefill.config.presets.all',
    helpKey: 'management.schedules.services.scheduledPrefill.config.presetHelp.all'
  },
  {
    value: 'Recent',
    labelKey: 'management.schedules.services.scheduledPrefill.config.presets.recent',
    helpKey: 'management.schedules.services.scheduledPrefill.config.presetHelp.recent'
  },
  {
    value: 'Top',
    labelKey: 'management.schedules.services.scheduledPrefill.config.presets.top',
    helpKey: 'management.schedules.services.scheduledPrefill.config.presetHelp.top'
  }
] as const satisfies readonly {
  value: ScheduledPrefillPreset;
  labelKey: string;
  helpKey: string;
}[];

/**
 * Presets each service's daemon can actually back with real data, keyed by service.
 * Reconciled against the daemon workers' real outcomes this swarm:
 *  - Steam: full All/Recent/Top (pre-existing daemon support).
 *  - Xbox: full All/Recent/Top (daemon sorts owned titles by titlehub lastTimePlayed for
 *    Recent, and intersects Microsoft's public most-played ranking for Top).
 *  - Epic: All + Top only. Epic's API exposes cumulative playtime but no last-played
 *    timestamp, so a real Recent ordering is impossible; the daemon gracefully falls back to
 *    all owned games when Recent is requested, so the option is hidden here. Top is real
 *    (owned games ordered by most-played).
 *  - BattleNet and Riot: permanently All-only. Their catalogs are flat, anonymous,
 *    zero-metadata lists with no per-user history or popularity signal to sort by.
 */
export const SCHEDULED_PREFILL_SUPPORTED_PRESETS: Record<
  ScheduledPrefillServiceKey,
  readonly ScheduledPrefillPreset[]
> = {
  steam: ['All', 'Recent', 'Top'],
  epic: ['All', 'Top'],
  xbox: ['All', 'Recent', 'Top'],
  battleNet: ['All'],
  riot: ['All']
};

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

/**
 * Services whose daemon actually varies output by target platform, keyed by service. Reconciled
 * against this swarm's daemon investigation (session 20260704-143707-433023485):
 *  - Steam: real support. The `os` param filters depots via Steam's own PICS `config.oslist`
 *    metadata (`DepotHandler.FilterDepotsToDownloadAsync`, unit-tested).
 *  - Epic, Xbox, BattleNet, Riot: no daemon-side platform concept exists at all - each hardcodes a
 *    single platform (or has none) and silently ignores any `os`/`platform` parameter sent to it.
 *    An empty set means the whole "Target platforms" field is hidden for that service, not just
 *    its options - unlike presets, there is no safe partial-support fallback for this capability.
 */
export const SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS: Record<
  ScheduledPrefillServiceKey,
  readonly ScheduledPrefillOperatingSystem[]
> = {
  steam: ['Windows', 'Linux', 'Macos'],
  epic: [],
  xbox: [],
  battleNet: [],
  riot: []
};

export const SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS = {
  min: 1,
  max: 256
} as const;

/**
 * A single container-list refresh reporting a persistent session as stopped/missing can be a
 * transient blip (adopt-or-replace churn, a SignalR refresh racing the daemon) rather than a real
 * stop. Both `PersistentLoginHost` (before unmounting an in-flight login) and
 * `ScheduledPrefillConfigModal`'s cleanup effect (before wiping `persistentLoginStore`) give a
 * stop this long to prove itself real before acting on it - they must share one grace period so
 * the modal's store cleanup never lands before the host's own grace timer decides the stop is real.
 */
export const SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS = 10_000;

/** Maps backend PrefillPlatform names to frontend service config keys. */
export const SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY: Record<string, ScheduledPrefillServiceKey> =
  {
    Steam: 'steam',
    Epic: 'epic',
    Xbox: 'xbox',
    BattleNet: 'battleNet',
    Riot: 'riot'
  };
