/**
 * Single source of truth for per-service brand colors.
 *
 * One row per canonical service carries every representation the app needs, so a new
 * service is one entry here rather than an edit in four files that then drift apart.
 * Consumers: badges and list rows (`getServiceColorClass`, `getServiceBadgeStyles`),
 * the analytics chart legend (`getServiceLegendClass`) and the chart datasets
 * (`getServiceColorVar` / `SERVICE_COLOR_VARS`).
 *
 * Class names are written as literal strings, never assembled from the service id:
 * `.service-*` lives inside `@layer components`, so a name Tailwind's content scanner
 * cannot see as a literal is purged out of the built stylesheet.
 */

interface ServiceBrand {
  /** CSS custom property emitted by the theme service for this service. */
  colorVar: string;
  /** Text-color class from styles/utilities/colors.css. */
  colorClass: string;
  /** Legend swatch class from styles/features/service-analytics.css. */
  legendClass: string;
}

const SERVICE_BRANDS = {
  steam: {
    colorVar: '--theme-steam',
    colorClass: 'service-steam',
    legendClass: 'legend-color-steam'
  },
  epic: {
    colorVar: '--theme-epic',
    colorClass: 'service-epic',
    legendClass: 'legend-color-epic'
  },
  origin: {
    colorVar: '--theme-origin',
    colorClass: 'service-origin',
    legendClass: 'legend-color-origin'
  },
  blizzard: {
    colorVar: '--theme-blizzard',
    colorClass: 'service-blizzard',
    legendClass: 'legend-color-blizzard'
  },
  wsus: {
    colorVar: '--theme-wsus',
    colorClass: 'service-wsus',
    legendClass: 'legend-color-wsus'
  },
  riot: {
    colorVar: '--theme-riot',
    colorClass: 'service-riot',
    legendClass: 'legend-color-riot'
  },
  xbox: {
    colorVar: '--theme-xbox',
    colorClass: 'service-xbox',
    legendClass: 'legend-color-xbox'
  },
  ubisoft: {
    colorVar: '--theme-ubisoft',
    colorClass: 'service-ubisoft',
    legendClass: 'legend-color-ubisoft'
  },
  gog: {
    colorVar: '--theme-gog',
    colorClass: 'service-gog',
    legendClass: 'legend-color-gog'
  },
  rockstar: {
    colorVar: '--theme-rockstar',
    colorClass: 'service-rockstar',
    legendClass: 'legend-color-rockstar'
  },
  arenanet: {
    colorVar: '--theme-arenanet',
    colorClass: 'service-arenanet',
    legendClass: 'legend-color-arenanet'
  },
  bsg: {
    colorVar: '--theme-bsg',
    colorClass: 'service-bsg',
    legendClass: 'legend-color-bsg'
  },
  cityofheroes: {
    colorVar: '--theme-cityofheroes',
    colorClass: 'service-cityofheroes',
    legendClass: 'legend-color-cityofheroes'
  },
  cod: {
    colorVar: '--theme-cod',
    colorClass: 'service-cod',
    legendClass: 'legend-color-cod'
  },
  daybreak: {
    colorVar: '--theme-daybreak',
    colorClass: 'service-daybreak',
    legendClass: 'legend-color-daybreak'
  },
  frontier: {
    colorVar: '--theme-frontier',
    colorClass: 'service-frontier',
    legendClass: 'legend-color-frontier'
  },
  neverwinter: {
    colorVar: '--theme-neverwinter',
    colorClass: 'service-neverwinter',
    legendClass: 'legend-color-neverwinter'
  },
  nexusmods: {
    colorVar: '--theme-nexusmods',
    colorClass: 'service-nexusmods',
    legendClass: 'legend-color-nexusmods'
  },
  nintendo: {
    colorVar: '--theme-nintendo',
    colorClass: 'service-nintendo',
    legendClass: 'legend-color-nintendo'
  },
  pathofexile: {
    colorVar: '--theme-pathofexile',
    colorClass: 'service-pathofexile',
    legendClass: 'legend-color-pathofexile'
  },
  renegadex: {
    colorVar: '--theme-renegadex',
    colorClass: 'service-renegadex',
    legendClass: 'legend-color-renegadex'
  },
  sony: {
    colorVar: '--theme-sony',
    colorClass: 'service-sony',
    legendClass: 'legend-color-sony'
  },
  square: {
    colorVar: '--theme-square',
    colorClass: 'service-square',
    legendClass: 'legend-color-square'
  },
  teso: {
    colorVar: '--theme-teso',
    colorClass: 'service-teso',
    legendClass: 'legend-color-teso'
  },
  test: {
    colorVar: '--theme-test',
    colorClass: 'service-test',
    legendClass: 'legend-color-test'
  },
  warframe: {
    colorVar: '--theme-warframe',
    colorClass: 'service-warframe',
    legendClass: 'legend-color-warframe'
  },
  wargaming: {
    colorVar: '--theme-wargaming',
    colorClass: 'service-wargaming',
    legendClass: 'legend-color-wargaming'
  }
} satisfies Record<string, ServiceBrand>;

type ServiceId = keyof typeof SERVICE_BRANDS;

/**
 * The brand colour property of a known service, as a closed union. `satisfies` above keeps the
 * literal spelling of every `colorVar`, so components that compose a tier onto the name get a
 * compile error for a service the theme declares no colour for.
 */
export type ServiceColorToken = (typeof SERVICE_BRANDS)[ServiceId]['colorVar'];

/** Alternate names the same service arrives under, folded onto its canonical id. */
const SERVICE_ID_BY_ALIAS: Record<string, string> = {
  epicgames: 'epic',
  ea: 'origin',
  battlenet: 'blizzard',
  'battle.net': 'blizzard',
  windows: 'wsus',
  riotgames: 'riot',
  xboxlive: 'xbox',
  uplay: 'ubisoft'
};

/** Text color for a service with no brand color of its own. */
export const UNKNOWN_COLOR_VAR = '--theme-text-secondary';
const UNKNOWN_COLOR_CLASS = 'text-[var(--theme-text-secondary)]';
const UNKNOWN_LEGEND_CLASS = 'legend-color-default';

function findBrand(service: string): ServiceBrand | null {
  const normalized = service.toLowerCase();
  const id = SERVICE_ID_BY_ALIAS[normalized] ?? normalized;
  return SERVICE_BRANDS[id as ServiceId] ?? null;
}

/**
 * Every distinct brand color property, for callers that resolve the whole set up front
 * (the chart reads computed values once per theme change rather than per data point).
 */
export const SERVICE_COLOR_VARS: readonly string[] = Object.values(SERVICE_BRANDS).map(
  (brand) => brand.colorVar
);

/**
 * The CSS custom property carrying a service's brand color, or the muted-text property
 * for a service with none. Cache-domains lists far more services than the app has brand
 * colors for, so an unknown name is expected, not an error.
 */
export function getServiceColorVar(service: string): ServiceColorToken | typeof UNKNOWN_COLOR_VAR {
  return findBrand(service)?.colorVar ?? UNKNOWN_COLOR_VAR;
}

/** Text color class for a service name, used on badges, table cells and list rows. */
export function getServiceColorClass(service: string): string {
  return findBrand(service)?.colorClass ?? UNKNOWN_COLOR_CLASS;
}

/** Swatch class for one row of the analytics chart legend. */
export function getServiceLegendClass(service: string): string {
  return findBrand(service)?.legendClass ?? UNKNOWN_LEGEND_CLASS;
}

/**
 * Inline styles for service badges. The label takes the `-text` tier rather than the brand
 * property: on a light theme the brand value is tuned for chart slices and tiles, and it is
 * too bright to clear 4.5:1 as type on the badge ground. A service with no brand color of its
 * own has no tier to take, so it keeps the muted text color.
 */
export function getServiceBadgeStyles(service: string): { backgroundColor: string; color: string } {
  const brand = findBrand(service);
  return {
    backgroundColor: 'var(--theme-bg-tertiary)',
    color: brand ? `var(${brand.colorVar}-text)` : `var(${UNKNOWN_COLOR_VAR})`
  };
}
