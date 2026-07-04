import { getServiceBadgeStyles, getServiceColorClass } from '@utils/serviceColors';
import { getServiceDisplayName } from '@utils/serviceDisplayName';

const UNKNOWN_SERVICE_COLOR_CLASS = getServiceColorClass('');

/**
 * Accent color string for AccordionSection's iconColor prop. Cache-domains lists
 * many services outside the app's known color map (nvidia, uplay, apple, ...);
 * for those the derived `--theme-<x>-subtle` variable would not exist, so fall
 * back to the theme accent instead of an invalid CSS variable.
 */
export function getServiceAccentColor(service: string): string {
  if (getServiceColorClass(service) === UNKNOWN_SERVICE_COLOR_CLASS) {
    return 'var(--theme-accent)';
  }
  return getServiceBadgeStyles(service).color;
}

/** Display label for a cache-domains service name: alias-folded and capitalized. */
export function formatServiceLabel(service: string): string {
  const display = getServiceDisplayName(service);
  return display.charAt(0).toUpperCase() + display.slice(1);
}

// Intl.ListFormat is in every supported browser but not in this project's TS lib target.
type ListFormatConstructor = new (
  locale: string,
  options: { style: 'long'; type: 'conjunction' }
) => { format: (items: string[]) => string };

const IntlListFormat = (Intl as unknown as { ListFormat?: ListFormatConstructor }).ListFormat;

/** Locale-aware "A, B and C" list for the verdict's supporting sentence. */
export function formatServiceList(services: string[], locale: string): string {
  const labels = services.map(formatServiceLabel);
  if (IntlListFormat) {
    try {
      return new IntlListFormat(locale, { style: 'long', type: 'conjunction' }).format(labels);
    } catch {
      // fall through to the plain join
    }
  }
  return labels.join(', ');
}

/** "https://github.com/uklans/cache-domains.git" -> "uklans/cache-domains". */
export function formatRepoShortName(repoUrl: string): string {
  const match = repoUrl.match(/github\.com[/:]([^/]+\/[^/.]+)/i);
  return match ? match[1] : repoUrl;
}

export function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}
