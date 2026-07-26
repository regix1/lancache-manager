import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import type {
  BattleNetGuestPrefillConfigChangedEvent,
  EpicGuestPrefillConfigChangedEvent,
  RiotGuestPrefillConfigChangedEvent,
  XboxGuestPrefillConfigChangedEvent
} from '@contexts/SignalRContext/types';
import type { GameServiceId } from '@/types/gameService';
import type { CommandType } from '../types';

/**
 * Name each service gives its thread-limit field on its guest-config SignalR event.
 * The backend names the field after the service, so it cannot be read positionally.
 */
type GuestConfigThreadField = 'maxThreadCount' | 'epicMaxThreadCount' | 'xboxMaxThreadCount';

/** Per-session prefill grant flags carried on the Session model. */
type SessionPrefillField =
  | 'steamPrefillEnabled'
  | 'epicPrefillEnabled'
  | 'battlenetPrefillEnabled'
  | 'riotPrefillEnabled'
  | 'xboxPrefillEnabled';

/** Per-session prefill grant expiry timestamps on the Session model. */
type SessionPrefillExpiryField =
  | 'steamPrefillExpiresAt'
  | 'epicPrefillExpiresAt'
  | 'battlenetPrefillExpiresAt'
  | 'riotPrefillExpiresAt'
  | 'xboxPrefillExpiresAt';

/**
 * Fields shared by the five `*GuestPrefillConfigChanged` SignalR events, so one handler can
 * serve all of them. Widened from the per-service event contracts rather than restated, so
 * renaming a field on the wire surfaces here as a type error instead of a silent null.
 *
 * The thread-limit fields are optional because each service sends only its own and the two
 * anonymous services send none; the one belonging to a service is selected through that
 * service's `configEventThreadField` rather than read positionally.
 */
export interface GuestPrefillConfigChangedPayload
  extends
    Partial<EpicGuestPrefillConfigChangedEvent>,
    Partial<XboxGuestPrefillConfigChangedEvent>,
    Partial<BattleNetGuestPrefillConfigChangedEvent>,
    Partial<RiotGuestPrefillConfigChangedEvent> {
  enabledByDefault: boolean;
  durationHours: number;
  /** Steam's event names the field plainly and has no dedicated event interface. */
  maxThreadCount?: number | null;
}

/**
 * Single source of truth for per-service prefill routing/branding.
 *
 * Replaces the open-coded 3-way ternaries scattered across PrefillPanel.tsx (hubPath,
 * serviceBasePath, header icon/colour). Centralising here removes the latent
 * "default-to-Steam" footgun where a future edit that forgets the `battlenet` arm would
 * silently route Battle.net traffic to the Steam daemon.
 *
 * It also drives every per-service UI block that used to be cloned once per service: the
 * prefill home cards, the session prefill readout and edit rows, and the guest default
 * config panels with their load/update/SignalR wiring. Adding a service means adding one
 * entry here plus its i18n keys and its `prefill-service-card--<id>` accent rule.
 */
interface PrefillServiceBase {
  /** Stable service id, matching the backend's service segment and the Session flags. */
  id: GameServiceId;
  /** Brand name as rendered in admin surfaces ("Epic Games"). Deliberately not localized. */
  displayName: string;
  /** Compact brand name for tight chips and log labels ("Epic"). */
  shortName: string;
  /**
   * Whether the service prefills against a user account. Anonymous services (Battle.net,
   * Riot) pull public CDN content and never prompt for credentials.
   */
  requiresLogin: boolean;
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
  /** Boolean field on the Session model holding this service's per-session grant. */
  sessionEnabledField: SessionPrefillField;
  /** Timestamp field on the Session model holding this service's grant expiry. */
  sessionExpiresAtField: SessionPrefillExpiryField;
  /** REST path of the guest default-config resource (GET to load, POST to update). */
  guestConfigPath: string;
  /** SignalR event announcing a change to the guest default config. */
  guestConfigChangedEvent: string;
  /**
   * Accent modifier on the prefill home card. Kept as a literal so the class stays
   * greppable from the stylesheet side; a template built from `id` would read as dead CSS.
   */
  homeCardClass: string;
  /** i18n key for the home card's one-paragraph description. */
  homeDescriptionKey: string;
  /** i18n keys for the home card's three feature bullets, in display order. */
  homeFeatureKeys: readonly [string, string, string];
  /** i18n key for the home card's account-requirement note. */
  homeLoginNoteKey: string;
}

/**
 * Anonymous services and account services that expose no per-download thread limit. Their
 * guest config carries no thread field on the wire, and the max-threads control is hidden.
 */
interface ThreadlessPrefillService extends PrefillServiceBase {
  supportsMaxThreads: false;
}

/** Services whose daemon honours a per-download thread cap. */
interface ThreadedPrefillService extends PrefillServiceBase {
  supportsMaxThreads: true;
  /** Field this service's guest-config event uses for the thread limit. */
  configEventThreadField: GuestConfigThreadField;
}

export type PrefillServiceConfig = ThreadlessPrefillService | ThreadedPrefillService;

const STEAM_CONFIG: PrefillServiceConfig = {
  id: 'steam',
  displayName: 'Steam',
  shortName: 'Steam',
  requiresLogin: true,
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
  supportedOperatingSystems: ['windows', 'linux', 'macos'],
  supportsMaxThreads: true,
  configEventThreadField: 'maxThreadCount',
  sessionEnabledField: 'steamPrefillEnabled',
  sessionExpiresAtField: 'steamPrefillExpiresAt',
  guestConfigPath: '/api/auth/guest/prefill/config',
  guestConfigChangedEvent: 'GuestPrefillConfigChanged',
  homeCardClass: 'prefill-service-card--steam',
  homeDescriptionKey: 'prefill.home.steamDescription',
  homeFeatureKeys: [
    'prefill.home.steamFeature1',
    'prefill.home.steamFeature2',
    'prefill.home.steamFeature3'
  ],
  homeLoginNoteKey: 'prefill.home.requiresSteamLogin'
};

const EPIC_CONFIG: PrefillServiceConfig = {
  id: 'epic',
  displayName: 'Epic Games',
  shortName: 'Epic',
  requiresLogin: true,
  hubPath: '/epic-prefill-daemon',
  serviceBasePath: 'epic-daemon',
  serviceNameKey: 'prefill.persistent.services.epic',
  icon: EpicIcon,
  colorVar: 'var(--theme-epic)',
  subtleColorVar: 'var(--theme-epic-subtle)',
  iconBgClass: 'bg-[var(--theme-epic)]',
  prefillCommands: ['prefill', 'prefill-all', 'prefill-top'],
  supportedOperatingSystems: [],
  supportsMaxThreads: true,
  configEventThreadField: 'epicMaxThreadCount',
  sessionEnabledField: 'epicPrefillEnabled',
  sessionExpiresAtField: 'epicPrefillExpiresAt',
  guestConfigPath: '/api/auth/guest/epic-prefill/config',
  guestConfigChangedEvent: 'EpicGuestPrefillConfigChanged',
  homeCardClass: 'prefill-service-card--epic',
  homeDescriptionKey: 'prefill.home.epicDescription',
  homeFeatureKeys: [
    'prefill.home.epicFeature1',
    'prefill.home.epicFeature2',
    'prefill.home.epicFeature3'
  ],
  homeLoginNoteKey: 'prefill.home.requiresEpicLogin'
};

const BATTLENET_CONFIG: PrefillServiceConfig = {
  id: 'battlenet',
  displayName: 'Battle.net',
  shortName: 'Battle.net',
  requiresLogin: false,
  hubPath: '/battlenet-prefill-daemon',
  serviceBasePath: 'battlenet-daemon',
  serviceNameKey: 'prefill.persistent.services.battleNet',
  icon: BlizzardIcon,
  colorVar: 'var(--theme-blizzard)',
  subtleColorVar: 'var(--theme-blizzard-subtle)',
  iconBgClass: 'bg-[var(--theme-blizzard)]',
  prefillCommands: ['prefill', 'prefill-all'],
  supportedOperatingSystems: [],
  supportsMaxThreads: false,
  sessionEnabledField: 'battlenetPrefillEnabled',
  sessionExpiresAtField: 'battlenetPrefillExpiresAt',
  guestConfigPath: '/api/auth/guest/battlenet-prefill/config',
  guestConfigChangedEvent: 'BattleNetGuestPrefillConfigChanged',
  homeCardClass: 'prefill-service-card--battlenet',
  homeDescriptionKey: 'prefill.home.battlenetDescription',
  homeFeatureKeys: [
    'prefill.home.battlenetFeature1',
    'prefill.home.battlenetFeature2',
    'prefill.home.battlenetFeature3'
  ],
  homeLoginNoteKey: 'prefill.home.battlenetNoLogin'
};

const RIOT_CONFIG: PrefillServiceConfig = {
  id: 'riot',
  displayName: 'Riot Games',
  shortName: 'Riot',
  requiresLogin: false,
  hubPath: '/riot-prefill-daemon',
  serviceBasePath: 'riot-daemon',
  serviceNameKey: 'prefill.persistent.services.riot',
  icon: RiotIcon,
  colorVar: 'var(--theme-riot)',
  subtleColorVar: 'var(--theme-riot-subtle)',
  iconBgClass: 'bg-[var(--theme-riot)]',
  prefillCommands: ['prefill', 'prefill-all'],
  supportedOperatingSystems: [],
  supportsMaxThreads: false,
  sessionEnabledField: 'riotPrefillEnabled',
  sessionExpiresAtField: 'riotPrefillExpiresAt',
  guestConfigPath: '/api/auth/guest/riot-prefill/config',
  guestConfigChangedEvent: 'RiotGuestPrefillConfigChanged',
  homeCardClass: 'prefill-service-card--riot',
  homeDescriptionKey: 'prefill.home.riotDescription',
  homeFeatureKeys: [
    'prefill.home.riotFeature1',
    'prefill.home.riotFeature2',
    'prefill.home.riotFeature3'
  ],
  homeLoginNoteKey: 'prefill.home.riotNoLogin'
};

const XBOX_CONFIG: PrefillServiceConfig = {
  id: 'xbox',
  displayName: 'Xbox',
  shortName: 'Xbox',
  requiresLogin: true,
  hubPath: '/xbox-prefill-daemon',
  serviceBasePath: 'xbox-daemon',
  serviceNameKey: 'prefill.persistent.services.xbox',
  icon: XboxIcon,
  colorVar: 'var(--theme-xbox)',
  subtleColorVar: 'var(--theme-xbox-subtle)',
  iconBgClass: 'bg-[var(--theme-xbox)]',
  prefillCommands: ['prefill', 'prefill-all', 'prefill-recent', 'prefill-top'],
  supportedOperatingSystems: [],
  supportsMaxThreads: true,
  configEventThreadField: 'xboxMaxThreadCount',
  sessionEnabledField: 'xboxPrefillEnabled',
  sessionExpiresAtField: 'xboxPrefillExpiresAt',
  guestConfigPath: '/api/auth/guest/xbox-prefill/config',
  guestConfigChangedEvent: 'XboxGuestPrefillConfigChanged',
  homeCardClass: 'prefill-service-card--xbox',
  homeDescriptionKey: 'prefill.home.xboxDescription',
  homeFeatureKeys: [
    'prefill.home.xboxFeature1',
    'prefill.home.xboxFeature2',
    'prefill.home.xboxFeature3'
  ],
  homeLoginNoteKey: 'prefill.home.requiresXboxLogin'
};

/**
 * Every prefill service in display order. Iterate this wherever a surface renders one
 * block per service so a new service reaches all of them at once.
 */
export const PREFILL_SERVICES: readonly PrefillServiceConfig[] = [
  STEAM_CONFIG,
  EPIC_CONFIG,
  BATTLENET_CONFIG,
  RIOT_CONFIG,
  XBOX_CONFIG
];

/**
 * Seeds a per-service lookup from the table so state declarations never re-list the
 * service ids. Takes a factory rather than a value so each service gets its own object
 * and callers cannot accidentally share one mutable seed across every key.
 */
export function prefillServiceRecord<TValue>(
  createValue: (service: PrefillServiceConfig) => TValue
): Record<GameServiceId, TValue> {
  const record = {} as Record<GameServiceId, TValue>;
  for (const service of PREFILL_SERVICES) {
    record[service.id] = createValue(service);
  }
  return record;
}

/**
 * Resolves the full routing/branding config for a given service id.
 * Unknown ids fall back to Steam (the historical default) but every known service is
 * handled explicitly so the three-way branch can never silently collapse to Steam.
 */
export function prefillServiceConfig(serviceId: string): PrefillServiceConfig {
  return (
    PREFILL_SERVICES.find((service: PrefillServiceConfig) => service.id === serviceId) ??
    STEAM_CONFIG
  );
}
