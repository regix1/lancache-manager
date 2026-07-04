import type { StatusCheckResult, StatusCheckDomainsSource } from '@services/api.service';

// SignalR payloads - keep in sync with the backend StatusCheck* events (camelCase on the wire)

export interface StatusCheckProgressEvent {
  operationId: string;
  completedDomains: number;
  totalDomains: number;
  /** Null on the initial "sweep started" event, before any service is being processed. */
  currentService: string | null;
}

export interface StatusCheckCompleteEvent {
  operationId: string;
  success: boolean;
  error: string | null;
  result: StatusCheckResult | null;
}

export interface CacheDomainsRefreshedEvent {
  domainsSource: StatusCheckDomainsSource;
}

// Browser-side probe ("From this device" card)

export type ClientProbeStatus =
  | 'checking'
  | 'intercepted'
  | 'inconclusive'
  | 'unreachable'
  | 'blocked';

export interface ClientProbeState {
  status: ClientProbeStatus;
  servedBy: string | null;
}

// Resolution ribbon (the tab's signature element)

export type RibbonSegmentStatus =
  // verdict colors, once a sweep has completed
  | 'resolved'
  | 'partial'
  | 'unresolved'
  // intentionally not cached (DISABLE_* in lancache-dns) - neutral, never a problem
  | 'disabled'
  // resolving, but no expected cache IP was known to verify against (v1.3) - info tone, not error
  | 'unverified'
  // live sweep states, while the ribbon doubles as the progress indicator
  | 'pending'
  | 'scanning'
  | 'scanned';

export interface RibbonSegment {
  service: string;
  status: RibbonSegmentStatus;
}
