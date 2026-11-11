import type { StatusCheckResult, StatusCheckDomainsSource } from '@services/api.service';
import type { RunWarning } from '@contexts/SignalRContext/types';

// SignalR payloads - keep in sync with the backend StatusCheck* events (camelCase on the wire)

export interface StatusCheckProgressEvent {
  operationId: string;
  completedDomains: number;
  totalDomains: number;
  /** Null on the initial "sweep started" event, before any service is being processed. */
  currentService: string | null;
}

export type StatusCheckCompleteEvent =
  | {
      operationId: string;
      success: true;
      error: null;
      result: StatusCheckResult;
      stageKey: null;
      context: null;
      canceled: false;
    }
  | {
      operationId: string;
      success: false;
      /** English, for logs; the reader sees `stageKey`. */
      error: string;
      result: null;
      stageKey: string;
      context: { errorDetail: string } | null;
      canceled: boolean;
    };

/**
 * A failed sweep or resolver save, kept as its key and its values (or the caught error) so the
 * box is translated when drawn and follows a language switch. A canceled sweep keeps only its key.
 */
export type StatusCheckRunError =
  | RunWarning
  | { stageKey: string; error: unknown }
  | { stageKey: string; canceled: true };

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
