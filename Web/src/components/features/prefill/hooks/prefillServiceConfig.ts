import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import type { CommandType } from '../types';

/**
 * Single source of truth for per-service prefill routing/branding.
 *
 * Replaces the open-coded 3-way ternaries scattered across PrefillPanel.tsx (hubPath,
 * serviceBasePath, header icon/colour). Centralising here removes the latent
 * "default-to-Steam" footgun where a future edit that forgets the `battlenet` arm would
 * silently route Battle.net traffic to the Steam daemon.
 */
interface PrefillServiceConfig {
  /** SignalR hub path for this service's prefill daemon. */
  hubPath: string;
  /** REST controller base path segment for this service's daemon. */
  serviceBasePath: string;
  /**
   * i18n key of this service's localized display name (the `prefill.persistent.services.*`
   * map). Login-flow copy interpolates it so shared strings never default to "Steam". Kept as
   * an explicit key because the map key for Battle.net is `battleNet` while the service id is
   * `battlenet` - a template lookup on the id would silently miss it.
   */
  serviceNameKey: string;
  /** Branded icon component for this service. */
  icon: typeof SteamIcon;
  /** CSS custom property holding this service's accent colour. */
  colorVar: string;
  /** Theme "subtle" tint custom property for this service. */
  subtleColorVar: string;
  /**
   * Literal Tailwind background class for the service header badge. Kept as a literal (not
   * built dynamically) because Tailwind's JIT can't see runtime-concatenated class names.
   */
  iconBgClass: string;
  /**
   * Prefill preset commands this service's daemon can actually back with real data, verified
   * against each daemon's SocketCommandInterface (the manager forwards every preset flag to
   * every daemon; daemons silently ignore flags they don't parse, so an unfiltered UI offers
   * buttons that do nothing or fall back to "all"). Manual-prefill twin of
   * SCHEDULED_PREFILL_SUPPORTED_PRESETS in the schedules feature — update both together:
   *  - Steam: parses all/recent/recently_purchased/top — every preset is real.
   *  - Epic: all + top only. Epic's API has no last-played timestamp, so its Recent branch
   *    falls back to all owned games; recently_purchased is not parsed at all.
   *  - Xbox: all/recent/top (titlehub lastTimePlayed for Recent, Microsoft's public
   *    most-played ranking for Top); recently_purchased is not parsed.
   *  - BattleNet and Riot: all-only — their sockets parse only all/force/products/appIds.
   * 'prefill' (explicitly selected games) works everywhere via appIds/products.
   */
  prefillCommands: readonly CommandType[];
  /**
   * Target-platform (OS) values this service's daemon actually honours. Only Steam's socket
   * reads an `os` filter (PICS config.oslist); Epic/Xbox/BattleNet/Riot silently ignore it
   * (Epic hardcodes Windows manifest URLs, Xbox has no platform concept, BattleNet/Riot
   * hardcode Windows CDN/patchline lookups). An empty array hides the "Target platforms"
   * field entirely for that service rather than offering a control with no effect.
   */
  supportedOperatingSystems: readonly string[];
}

const STEAM_CONFIG: PrefillServiceConfig = {
  hubPath: '/steam-daemon',
  serviceBasePath: 'steam-daemon',
  serviceNameKey: 'prefill.persistent.services.steam',
  icon: SteamIcon,
  colorVar: 'var(--theme-steam)',
  subtleColorVar: 'var(--theme-steam-subtle)',
  iconBgClass: 'bg-[var(--theme-steam)]',
  prefillCommands: [
    'prefill',
    'prefill-all',
    'prefill-recent',
    'prefill-recent-purchased',
    'prefill-top'
  ],
  supportedOperatingSystems: ['windows', 'linux', 'macos']
};

const EPIC_CONFIG: PrefillServiceConfig = {
  hubPath: '/epic-prefill-daemon',
  serviceBasePath: 'epic-daemon',
  serviceNameKey: 'prefill.persistent.services.epic',
  icon: EpicIcon,
  colorVar: 'var(--theme-epic)',
  subtleColorVar: 'var(--theme-epic-subtle)',
  iconBgClass: 'bg-[var(--theme-epic)]',
  prefillCommands: ['prefill', 'prefill-all', 'prefill-top'],
  supportedOperatingSystems: []
};

const BATTLENET_CONFIG: PrefillServiceConfig = {
  hubPath: '/battlenet-prefill-daemon',
  serviceBasePath: 'battlenet-daemon',
  serviceNameKey: 'prefill.persistent.services.battleNet',
  icon: BlizzardIcon,
  colorVar: 'var(--theme-blizzard)',
  subtleColorVar: 'var(--theme-blizzard-subtle)',
  iconBgClass: 'bg-[var(--theme-blizzard)]',
  prefillCommands: ['prefill', 'prefill-all'],
  supportedOperatingSystems: []
};

const RIOT_CONFIG: PrefillServiceConfig = {
  hubPath: '/riot-prefill-daemon',
  serviceBasePath: 'riot-daemon',
  serviceNameKey: 'prefill.persistent.services.riot',
  icon: RiotIcon,
  colorVar: 'var(--theme-riot)',
  subtleColorVar: 'var(--theme-riot-subtle)',
  iconBgClass: 'bg-[var(--theme-riot)]',
  prefillCommands: ['prefill', 'prefill-all'],
  supportedOperatingSystems: []
};

const XBOX_CONFIG: PrefillServiceConfig = {
  hubPath: '/xbox-prefill-daemon',
  serviceBasePath: 'xbox-daemon',
  serviceNameKey: 'prefill.persistent.services.xbox',
  icon: XboxIcon,
  colorVar: 'var(--theme-xbox)',
  subtleColorVar: 'var(--theme-xbox-subtle)',
  iconBgClass: 'bg-[var(--theme-xbox)]',
  prefillCommands: ['prefill', 'prefill-all', 'prefill-recent', 'prefill-top'],
  supportedOperatingSystems: []
};

/**
 * Resolves the full routing/branding config for a given service id.
 * Unknown ids fall back to Steam (the historical default) but every known service is
 * handled explicitly so the three-way branch can never silently collapse to Steam.
 */
export function prefillServiceConfig(serviceId: string): PrefillServiceConfig {
  switch (serviceId) {
    case 'epic':
      return EPIC_CONFIG;
    case 'battlenet':
      return BATTLENET_CONFIG;
    case 'riot':
      return RIOT_CONFIG;
    case 'xbox':
      return XBOX_CONFIG;
    default:
      return STEAM_CONFIG;
  }
}
