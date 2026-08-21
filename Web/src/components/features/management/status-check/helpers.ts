import type { StatusCheckResult } from '@services/api.service';
import { UNKNOWN_COLOR_VAR, getServiceColorVar } from '@utils/serviceColors';
import type { ColorToken } from '@utils/eventColors';

/**
 * Colour token for AccordionSection's iconColor prop. Cache-domains lists many services
 * outside the app's known colour map (nvidia, apple, ...); those fall back to the muted
 * text property, which is not a brand accent, so use the theme accent for them instead.
 */
export function getServiceAccentColor(service: string): ColorToken {
  const colorVar = getServiceColorVar(service);
  return colorVar === UNKNOWN_COLOR_VAR ? '--theme-accent' : colorVar;
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

/**
 * True when the server sweep's domain result for `host` came back heartbeat-verified. This is the
 * DoH-disagreement signal: if the server reached the cache at this host but a browser-side probe
 * for the same host could not, the browser is very likely resolving names on its own instead of
 * using the network's DNS server.
 */
export function isProbeHostHeartbeatVerified(
  lastResult: StatusCheckResult | null,
  host: string
): boolean {
  if (!lastResult) return false;
  for (const service of lastResult.services) {
    const domain = service.domains.find((entry) => entry.domain === host);
    if (domain) return domain.heartbeatVerified;
  }
  return false;
}
