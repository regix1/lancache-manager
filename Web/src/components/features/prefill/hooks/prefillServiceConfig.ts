import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { RiotIcon } from '@components/ui/RiotIcon';

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
}

const STEAM_CONFIG: PrefillServiceConfig = {
  hubPath: '/steam-daemon',
  serviceBasePath: 'steam-daemon',
  icon: SteamIcon,
  colorVar: 'var(--theme-steam)',
  subtleColorVar: 'var(--theme-steam-subtle)',
  iconBgClass: 'bg-[var(--theme-steam)]'
};

const EPIC_CONFIG: PrefillServiceConfig = {
  hubPath: '/epic-prefill-daemon',
  serviceBasePath: 'epic-daemon',
  icon: EpicIcon,
  colorVar: 'var(--theme-epic)',
  subtleColorVar: 'var(--theme-epic-subtle)',
  iconBgClass: 'bg-[var(--theme-epic)]'
};

const BATTLENET_CONFIG: PrefillServiceConfig = {
  hubPath: '/battlenet-prefill-daemon',
  serviceBasePath: 'battlenet-daemon',
  icon: BlizzardIcon,
  colorVar: 'var(--theme-blizzard)',
  subtleColorVar: 'var(--theme-blizzard-subtle)',
  iconBgClass: 'bg-[var(--theme-blizzard)]'
};

const RIOT_CONFIG: PrefillServiceConfig = {
  hubPath: '/riot-prefill-daemon',
  serviceBasePath: 'riot-daemon',
  icon: RiotIcon,
  colorVar: 'var(--theme-riot)',
  subtleColorVar: 'var(--theme-riot-subtle)',
  iconBgClass: 'bg-[var(--theme-riot)]'
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
    default:
      return STEAM_CONFIG;
  }
}
