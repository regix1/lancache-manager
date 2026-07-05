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

/**
 * Split a list into a few shown examples plus a "+N more" remainder count.
 * Used for the hero's example chips and for collapsing long IP lists so the
 * verdict never enumerates every service name or every cache IP inline.
 */
export function splitExamples<T>(items: T[], max: number): { shown: T[]; moreCount: number } {
  return { shown: items.slice(0, max), moreCount: Math.max(0, items.length - max) };
}

/** "https://github.com/uklans/cache-domains.git" -> "uklans/cache-domains". */
export function formatRepoShortName(repoUrl: string): string {
  const match = repoUrl.match(/github\.com[/:]([^/]+\/[^/.]+)/i);
  return match ? match[1] : repoUrl;
}

export function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}
