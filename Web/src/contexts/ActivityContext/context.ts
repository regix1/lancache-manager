import { createContext } from 'react';
import type { ActivityAspect, ActivityDomain } from '@contexts/SignalRContext/types';

/**
 * Read access to the unified activity/presence state (one snapshot drives every green status dot).
 * Returned by useActivityStatus.
 */
export interface ActivityLookup {
  /** True when the given (domain, key, aspect) entity is currently active. */
  isActive: (domain: ActivityDomain, key: string, aspect: ActivityAspect) => boolean;
  /** Active count for the identity (0 when inactive) - e.g. how many operations of a type are running. */
  activeCount: (domain: ActivityDomain, key: string, aspect: ActivityAspect) => number;
  /**
   * True once at least one ActivityUpdated snapshot has been processed. Before that, isActive/activeCount
   * read as "inactive" for everything simply because no data has arrived yet, not because the registry
   * has confirmed anything - a caller that needs to tell those two apart (e.g. defaulting to "present"
   * during the brief window before the first snapshot lands) should gate on this first.
   */
  ready: boolean;
  /**
   * isActive(domain, key, aspect) once ready, otherwise fallback. Centralizes the ready-gate every
   * consumer with an independent (non-registry) signal for the same entity needs: the registry and a
   * REST-fetched/locally-computed fallback are populated by different code paths on different triggers,
   * so a naive `isActive(...) || fallback` can let a stale-true fallback mask a fresh-false registry
   * value forever. Only correct for a fallback that is itself an independent signal for the SAME
   * (domain, key, aspect) - not for a fallback derived from the exact same upstream snapshot the
   * registry mirrors (there the plain OR is fine and this gate is unnecessary).
   */
  isActiveOrFallback: (
    domain: ActivityDomain,
    key: string,
    aspect: ActivityAspect,
    fallback: boolean
  ) => boolean;
}

export const ActivityContext = createContext<ActivityLookup | null>(null);
